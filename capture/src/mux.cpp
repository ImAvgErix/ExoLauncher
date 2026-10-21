// Exo Capture - encoded packets, the replay buffer, MP4 files, trim and export. GPL-3.0-or-later.
#include "mux.h"

#include <propvarutil.h>
#include <algorithm>

#pragma comment(lib, "propsys.lib")

namespace
{
constexpr size_t BufferCapBytes = size_t(3) * 1024 * 1024 * 1024; // never more than 3 GB of RAM

bool ByTime(const PacketPtr& a, const PacketPtr& b)
{
    if (a->time != b->time) return a->time < b->time;
    return a->track < b->track;
}
}

// ---------------------------------------------------------------- replay buffer

void ReplayBuffer::SetLength(int64_t length100ns)
{
    std::lock_guard lock(m_lock);
    m_length = std::max<int64_t>(length100ns, 5LL * 10'000'000);
    TrimLocked();
}

void ReplayBuffer::Push(const PacketPtr& packet)
{
    std::lock_guard lock(m_lock);
    if (packet->track == 0)
    {
        // The buffer always opens on a key frame.
        if (m_video.empty() && !packet->key) return;
        m_video.push_back(packet);
    }
    else
    {
        if (m_video.empty()) return;
        m_audio.push_back(packet);
    }
    m_bytes += packet->data.size();
    TrimLocked();
}

void ReplayBuffer::TrimLocked()
{
    if (m_video.empty()) return;
    int64_t newest = m_video.back()->time;
    if (!m_audio.empty()) newest = std::max(newest, m_audio.back()->time);
    int64_t cutoff = newest - m_length;
    for (;;)
    {
        // The next key frame after the oldest one; drop the oldest group of
        // pictures while that key frame still leaves the full length behind it.
        auto next = std::find_if(m_video.begin() + 1, m_video.end(), [](const PacketPtr& p) { return p->key; });
        if (next == m_video.end()) break;
        if ((*next)->time > cutoff && m_bytes <= BufferCapBytes) break;
        for (auto it = m_video.begin(); it != next; ++it) m_bytes -= (*it)->data.size();
        m_video.erase(m_video.begin(), next);
    }
    int64_t start = m_video.front()->time;
    while (!m_audio.empty() && m_audio.front()->time < start)
    {
        m_bytes -= m_audio.front()->data.size();
        m_audio.pop_front();
    }
}

std::vector<PacketPtr> ReplayBuffer::Snapshot(int64_t length100ns) const
{
    std::lock_guard lock(m_lock);
    std::vector<PacketPtr> out;
    if (m_video.empty()) return out;
    auto from = m_video.begin();
    if (length100ns > 0)
    {
        int64_t newest = m_video.back()->time;
        int64_t cutoff = newest - length100ns;
        for (auto it = m_video.begin(); it != m_video.end(); ++it)
            if ((*it)->key && (*it)->time <= cutoff) from = it;
    }
    int64_t start = (*from)->time;
    out.insert(out.end(), from, m_video.end());
    for (auto& a : m_audio)
        if (a->time >= start) out.push_back(a);
    std::stable_sort(out.begin(), out.end(), ByTime);
    return out;
}

void ReplayBuffer::Clear()
{
    std::lock_guard lock(m_lock);
    m_video.clear();
    m_audio.clear();
    m_bytes = 0;
}

size_t ReplayBuffer::Bytes() const
{
    std::lock_guard lock(m_lock);
    return m_bytes;
}

int64_t ReplayBuffer::Span() const
{
    std::lock_guard lock(m_lock);
    if (m_video.empty()) return 0;
    return m_video.back()->time - m_video.front()->time;
}

// ---------------------------------------------------------------- MP4 writer

Mp4Writer::Mp4Writer(const std::wstring& path, const StreamFormat& format)
{
    com_ptr<IMFAttributes> attrs;
    Check(MFCreateAttributes(attrs.put(), 3), "writer attributes");
    attrs->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
    attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    attrs->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
    Check(MFCreateSinkWriterFromURL(path.c_str(), nullptr, attrs.get(), m_writer.put()), "MP4 file");
    DWORD index = 0;
    Check(m_writer->AddStream(format.video.get(), &index), "video track");
    Check(m_writer->SetInputMediaType(index, format.video.get(), nullptr), "video track type");
    for (auto& audio : format.audio)
    {
        Check(m_writer->AddStream(audio.get(), &index), "audio track");
        Check(m_writer->SetInputMediaType(index, audio.get(), nullptr), "audio track type");
        ++m_audioTracks;
    }
    if (format.fps > 0) m_nominal = 10'000'000 / format.fps;
    Check(m_writer->BeginWriting(), "MP4 start");
}

Mp4Writer::~Mp4Writer()
{
    if (!m_finished)
    {
        try { Finish(); } catch (...) {}
    }
}

void Mp4Writer::Write(const PacketPtr& packet)
{
    if (m_finished) return;
    if (m_base < 0)
    {
        if (packet->track != 0 || !packet->key) return;
        m_base = packet->time;
    }
    if (packet->time < m_base) return;
    if (packet->track > m_audioTracks) return;
    if (packet->track == 0)
    {
        // A frame lasts until the next one: frames arrive as the game draws them.
        if (m_pendingVideo)
        {
            int64_t duration = packet->time - m_pendingVideo->time;
            if (duration <= 0) duration = 1;
            WriteNow(*m_pendingVideo, duration);
        }
        m_pendingVideo = packet;
        return;
    }
    WriteNow(*packet, packet->duration);
}

void Mp4Writer::WriteNow(const Packet& packet, int64_t duration)
{
    com_ptr<IMFMediaBuffer> buffer;
    Check(MFCreateMemoryBuffer(static_cast<DWORD>(packet.data.size()), buffer.put()), "sample buffer");
    BYTE* bytes = nullptr;
    Check(buffer->Lock(&bytes, nullptr, nullptr), "sample lock");
    memcpy(bytes, packet.data.data(), packet.data.size());
    buffer->Unlock();
    buffer->SetCurrentLength(static_cast<DWORD>(packet.data.size()));
    com_ptr<IMFSample> sample;
    Check(MFCreateSample(sample.put()), "sample");
    sample->AddBuffer(buffer.get());
    sample->SetSampleTime(packet.time - m_base);
    sample->SetSampleDuration(duration);
    if (packet.key) sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
    Check(m_writer->WriteSample(static_cast<DWORD>(packet.track), sample.get()), "write sample");
    m_last = std::max(m_last, packet.time - m_base + duration);
}

int64_t Mp4Writer::Finish()
{
    if (m_finished) return m_last;
    m_finished = true;
    if (m_pendingVideo) WriteNow(*m_pendingVideo, m_nominal);
    m_pendingVideo = nullptr;
    if (m_base < 0)
    {
        // Nothing was written; the sink still needs closing.
        m_writer->Finalize();
        return 0;
    }
    Check(m_writer->Finalize(), "MP4 finish");
    return m_last;
}

// ---------------------------------------------------------------- journal

namespace
{
constexpr char JournalMagic[8] = { 'E', 'X', 'O', 'R', 'E', 'C', '1', 0 };

void WriteAll(HANDLE file, const void* data, DWORD size)
{
    DWORD written = 0;
    if (!WriteFile(file, data, size, &written, nullptr) || written != size) throw Failure(HRESULT_FROM_WIN32(GetLastError()), "journal write");
}

bool ReadAll(HANDLE file, void* data, DWORD size)
{
    DWORD read = 0;
    return ReadFile(file, data, size, &read, nullptr) && read == size;
}

void WriteType(HANDLE file, IMFMediaType* type)
{
    UINT32 size = 0;
    Check(MFGetAttributesAsBlobSize(type, &size), "journal type");
    std::vector<UINT8> blob(size);
    Check(MFGetAttributesAsBlob(type, blob.data(), size), "journal type");
    WriteAll(file, &size, sizeof(size));
    WriteAll(file, blob.data(), size);
}

com_ptr<IMFMediaType> ReadType(HANDLE file)
{
    UINT32 size = 0;
    if (!ReadAll(file, &size, sizeof(size)) || size > 1 << 20) throw Failure(E_FAIL, "journal type");
    std::vector<UINT8> blob(size);
    if (!ReadAll(file, blob.data(), size)) throw Failure(E_FAIL, "journal type");
    com_ptr<IMFMediaType> type;
    Check(MFCreateMediaType(type.put()), "media type");
    Check(MFInitAttributesFromBlob(type.get(), blob.data(), size), "journal type");
    return type;
}
}

Journal::Journal(const std::wstring& path, const StreamFormat& format) : m_path(path)
{
    m_file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_HIDDEN, nullptr);
    if (m_file == INVALID_HANDLE_VALUE) throw Failure(HRESULT_FROM_WIN32(GetLastError()), "journal");
    WriteAll(m_file, JournalMagic, sizeof(JournalMagic));
    UINT32 fps = static_cast<UINT32>(format.fps), count = static_cast<UINT32>(1 + format.audio.size());
    WriteAll(m_file, &fps, sizeof(fps));
    WriteAll(m_file, &count, sizeof(count));
    WriteType(m_file, format.video.get());
    for (auto& a : format.audio) WriteType(m_file, a.get());
}

Journal::~Journal()
{
    if (m_file != INVALID_HANDLE_VALUE) CloseHandle(m_file);
}

void Journal::Append(const Packet& packet)
{
    if (m_file == INVALID_HANDLE_VALUE) return;
    uint8_t head[2] = { static_cast<uint8_t>(packet.track), static_cast<uint8_t>(packet.key ? 1 : 0) };
    UINT32 size = static_cast<UINT32>(packet.data.size());
    WriteAll(m_file, head, sizeof(head));
    WriteAll(m_file, &packet.time, sizeof(packet.time));
    WriteAll(m_file, &packet.duration, sizeof(packet.duration));
    WriteAll(m_file, &size, sizeof(size));
    WriteAll(m_file, packet.data.data(), size);
}

void Journal::Discard()
{
    if (m_file != INVALID_HANDLE_VALUE) CloseHandle(m_file);
    m_file = INVALID_HANDLE_VALUE;
    DeleteFileW(m_path.c_str());
}

int64_t RecoverJournal(const std::wstring& journal)
{
    std::wstring mp4 = journal.substr(0, journal.size() - std::wstring(L".exo-rec").size());
    HANDLE file = CreateFileW(journal.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw Failure(HRESULT_FROM_WIN32(GetLastError()), "journal");
    int64_t duration = 0;
    try
    {
        char magic[8];
        UINT32 fps = 0, count = 0;
        if (!ReadAll(file, magic, 8) || memcmp(magic, JournalMagic, 8) || !ReadAll(file, &fps, 4) || !ReadAll(file, &count, 4) || count == 0 || count > 16)
            throw Failure(E_FAIL, "not a recording journal");
        StreamFormat format;
        format.fps = static_cast<int>(fps);
        format.video = ReadType(file);
        for (UINT32 i = 1; i < count; ++i) format.audio.push_back(ReadType(file));
        DeleteFileW(mp4.c_str());
        Mp4Writer writer(mp4, format);
        for (;;)
        {
            uint8_t head[2];
            auto packet = std::make_shared<Packet>();
            UINT32 size = 0;
            if (!ReadAll(file, head, 2) || !ReadAll(file, &packet->time, 8) || !ReadAll(file, &packet->duration, 8) ||
                !ReadAll(file, &size, 4) || size > 64u * 1024 * 1024)
                break;
            packet->data.resize(size);
            if (!ReadAll(file, packet->data.data(), size)) break; // the last packet was cut off
            packet->track = head[0];
            packet->key = head[1] != 0;
            writer.Write(packet);
        }
        duration = writer.Finish();
    }
    catch (...)
    {
        CloseHandle(file);
        throw;
    }
    CloseHandle(file);
    DeleteFileW(journal.c_str());
    return duration;
}

// ---------------------------------------------------------------- recorder

Recorder::Recorder(const std::wstring& path, const StreamFormat& format)
    : m_path(path), m_writer(std::make_unique<Mp4Writer>(path, format)),
      m_journal(std::make_unique<Journal>(path + L".exo-rec", format))
{
    m_thread = std::thread([this] { Run(); });
}

Recorder::~Recorder()
{
    if (m_thread.joinable()) Stop();
}

void Recorder::Push(const PacketPtr& packet)
{
    std::lock_guard lock(m_lock);
    if (m_stop) return;
    if (!m_keySeen)
    {
        if (packet->track != 0 || !packet->key) return;
        m_keySeen = true;
    }
    m_queue.push_back(packet);
    m_wake.notify_one();
}

void Recorder::Run()
{
    for (;;)
    {
        std::deque<PacketPtr> work;
        {
            std::unique_lock lock(m_lock);
            m_wake.wait(lock, [this] { return m_stop || !m_queue.empty(); });
            work.swap(m_queue);
            if (work.empty() && m_stop) break;
        }
        for (auto& packet : work)
        {
            try
            {
                m_journal->Append(*packet);
                m_writer->Write(packet);
            }
            catch (const std::exception& e)
            {
                std::lock_guard lock(m_lock);
                if (m_error.empty()) m_error = e.what();
            }
        }
    }
    bool closed = false;
    try { m_duration = m_writer->Finish(); closed = true; }
    catch (const std::exception& e)
    {
        std::lock_guard lock(m_lock);
        if (m_error.empty()) m_error = e.what();
    }
    m_writer.reset();
    // A clean close needs no journal; a failed one keeps it for the next start.
    if (closed) m_journal->Discard();
    m_journal.reset();
}

int64_t Recorder::Stop()
{
    {
        std::lock_guard lock(m_lock);
        m_stop = true;
        m_wake.notify_one();
    }
    if (m_thread.joinable()) m_thread.join();
    return m_duration;
}

std::string Recorder::Error() const
{
    std::lock_guard lock(const_cast<std::mutex&>(m_lock));
    return m_error;
}

// ---------------------------------------------------------------- trim

int64_t MediaDuration(const std::wstring& path)
{
    com_ptr<IMFSourceReader> reader;
    if (FAILED(MFCreateSourceReaderFromURL(path.c_str(), nullptr, reader.put()))) return 0;
    PROPVARIANT value;
    PropVariantInit(&value);
    int64_t duration = 0;
    if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &value)))
        duration = static_cast<int64_t>(value.uhVal.QuadPart);
    PropVariantClear(&value);
    return duration;
}

int64_t TrimCopy(const std::wstring& input, const std::wstring& output, int64_t start, int64_t end)
{
    com_ptr<IMFSourceReader> reader;
    Check(MFCreateSourceReaderFromURL(input.c_str(), nullptr, reader.put()), "open the video");

    // Every stream as it is in the file: no decoder, no encoder.
    struct Stream { DWORD index; bool video; DWORD out; bool done; };
    std::vector<Stream> streams;
    StreamFormat format;
    for (DWORD i = 0;; ++i)
    {
        com_ptr<IMFMediaType> type;
        HRESULT hr = reader->GetNativeMediaType(i, 0, type.put());
        if (hr == MF_E_INVALIDSTREAMNUMBER) break;
        Check(hr, "read the video's tracks");
        GUID major{};
        type->GetMajorType(&major);
        if (major != MFMediaType_Video && major != MFMediaType_Audio) { reader->SetStreamSelection(i, FALSE); continue; }
        bool video = major == MFMediaType_Video;
        if (video && format.video) { reader->SetStreamSelection(i, FALSE); continue; }
        reader->SetStreamSelection(i, TRUE);
        Check(reader->SetCurrentMediaType(i, nullptr, type.get()), "keep the video's encoding");
        if (video) format.video = type;
        else format.audio.push_back(type);
        streams.push_back({ i, video, 0, false });
    }
    if (!format.video) throw Failure(E_FAIL, "the file has no video");
    // Writer track order: video first, then audio in file order.
    DWORD nextAudio = 1;
    for (auto& s : streams) s.out = s.video ? 0 : nextAudio++;

    UINT32 num = 0, den = 0;
    if (SUCCEEDED(MFGetAttributeRatio(format.video.get(), MF_MT_FRAME_RATE, &num, &den)) && den) format.fps = std::max<int>(1, num / den);

    PROPVARIANT position;
    InitPropVariantFromInt64(std::max<int64_t>(0, start), &position);
    Check(reader->SetCurrentPosition(GUID_NULL, position), "seek");
    PropVariantClear(&position);

    Mp4Writer writer(output, format);
    int64_t base = -1;
    size_t open = streams.size();
    while (open > 0)
    {
        DWORD actual = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        Check(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &actual, &flags, &time, sample.put()), "read");
        auto stream = std::find_if(streams.begin(), streams.end(), [&](const Stream& s) { return s.index == actual; });
        if (stream == streams.end()) continue;
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
        {
            if (!stream->done) { stream->done = true; --open; }
            continue;
        }
        if (!sample || stream->done) continue;
        if (end > 0 && time > end)
        {
            stream->done = true;
            --open;
            continue;
        }
        auto packet = std::make_shared<Packet>();
        packet->track = static_cast<int>(stream->out);
        packet->time = time;
        LONGLONG duration = 0;
        sample->GetSampleDuration(&duration);
        packet->duration = duration;
        packet->key = MFGetAttributeUINT32(sample.get(), MFSampleExtension_CleanPoint, FALSE) != FALSE;
        if (stream->video && base < 0)
        {
            if (!packet->key) continue;
            base = time;
        }
        if (base < 0 || time < base) continue;
        com_ptr<IMFMediaBuffer> buffer;
        Check(sample->ConvertToContiguousBuffer(buffer.put()), "sample data");
        BYTE* bytes = nullptr;
        DWORD length = 0;
        Check(buffer->Lock(&bytes, nullptr, &length), "sample lock");
        packet->data.assign(bytes, bytes + length);
        buffer->Unlock();
        writer.Write(packet);
    }
    return writer.Finish();
}

// ---------------------------------------------------------------- export (re-encode)

namespace
{
com_ptr<IMFMediaType> VideoType(const GUID& subtype, UINT32 width, UINT32 height, UINT32 num, UINT32 den)
{
    com_ptr<IMFMediaType> type;
    Check(MFCreateMediaType(type.put()), "media type");
    type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    type->SetGUID(MF_MT_SUBTYPE, subtype);
    MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(type.get(), MF_MT_FRAME_RATE, num, den);
    MFSetAttributeRatio(type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    return type;
}

com_ptr<IMFMediaType> PcmType()
{
    com_ptr<IMFMediaType> type;
    Check(MFCreateMediaType(type.put()), "media type");
    type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
    type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
    type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
    type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192000);
    return type;
}

com_ptr<IMFMediaType> AacType(UINT32 bytesPerSecond)
{
    com_ptr<IMFMediaType> type;
    Check(MFCreateMediaType(type.put()), "media type");
    type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
    type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytesPerSecond);
    return type;
}

std::string ExportOnce(const std::wstring& input, const std::wstring& output, const std::wstring& preset,
    int64_t start, int64_t end, double scale)
{
    com_ptr<IMFAttributes> readAttrs;
    Check(MFCreateAttributes(readAttrs.put(), 1), "reader attributes");
    readAttrs->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
    com_ptr<IMFSourceReader> reader;
    Check(MFCreateSourceReaderFromURL(input.c_str(), readAttrs.get(), reader.put()), "open the video");
    reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    const DWORD videoStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    const DWORD audioStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
    reader->SetStreamSelection(videoStream, TRUE);
    bool hasAudio = SUCCEEDED(reader->SetStreamSelection(audioStream, TRUE));

    com_ptr<IMFMediaType> native;
    Check(reader->GetNativeMediaType(videoStream, 0, native.put()), "video track");
    UINT32 width = 0, height = 0, num = 60, den = 1;
    MFGetAttributeSize(native.get(), MF_MT_FRAME_SIZE, &width, &height);
    // The picture, without the rows and columns the encoder padded to its block size.
    MFVideoArea area{};
    if (SUCCEEDED(native->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr)) &&
        area.Area.cx > 0 && area.Area.cy > 0)
    {
        width = static_cast<UINT32>(area.Area.cx) & ~1u;
        height = static_cast<UINT32>(area.Area.cy) & ~1u;
    }
    MFGetAttributeRatio(native.get(), MF_MT_FRAME_RATE, &num, &den);
    if (!width || !height) return "The video has no picture size.";
    if (!den) { num = 60; den = 1; }

    int64_t duration = (end > 0 ? end : MediaDuration(input)) - start;
    if (duration <= 0) return "Nothing to export.";
    double seconds = duration / 1e7;

    bool vertical = preset == L"vertical";
    UINT32 readW = width, readH = height, outW = width, outH = height, cropX = 0;
    UINT32 videoBitrate = 0, audioBytes = 24000;
    if (vertical)
    {
        outW = (height * 9 / 16) & ~1u;
        if (outW > width) outW = width & ~1u;
        cropX = ((width - outW) / 2) & ~1u;
        videoBitrate = static_cast<UINT32>(std::min(20e6, outW * double(outH) * (num / double(den)) * 0.12));
    }
    else
    {
        // Discord's upload limit is 10 MB; aim under it with the audio at 96 kbit/s.
        audioBytes = 12000;
        double total = 9.4 * 8 * 1000 * 1000 * scale / seconds;
        double video = total - (hasAudio ? 96000 : 0);
        if (video < 300000)
        {
            int most = static_cast<int>(9.4 * 8 * 1000 * 1000 / (300000 + 96000));
            return "Too long for Discord's 10 MB. Trim it to " + std::to_string(most) + " seconds or less.";
        }
        UINT32 targetH = video >= 5e6 ? 1080 : video >= 2.2e6 ? 720 : video >= 1e6 ? 540 : 360;
        if (targetH < height)
        {
            readH = outH = targetH;
            readW = outW = static_cast<UINT32>(width * double(targetH) / height) & ~1u;
        }
        videoBitrate = static_cast<UINT32>(video);
    }

    Check(reader->SetCurrentMediaType(videoStream, nullptr, VideoType(MFVideoFormat_NV12, readW, readH, num, den).get()), "decode the video");
    if (hasAudio && FAILED(reader->SetCurrentMediaType(audioStream, nullptr, PcmType().get()))) hasAudio = false;
    if (!hasAudio) reader->SetStreamSelection(audioStream, FALSE);

    auto makeWriter = [&](bool hardware) -> std::pair<com_ptr<IMFSinkWriter>, HRESULT>
    {
        com_ptr<IMFAttributes> attrs;
        MFCreateAttributes(attrs.put(), 2);
        attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware ? TRUE : FALSE);
        attrs->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
        DeleteFileW(output.c_str());
        com_ptr<IMFSinkWriter> writer;
        HRESULT hr = MFCreateSinkWriterFromURL(output.c_str(), nullptr, attrs.get(), writer.put());
        if (FAILED(hr)) return { nullptr, hr };
        auto outType = VideoType(MFVideoFormat_H264, outW, outH, num, den);
        outType->SetUINT32(MF_MT_AVG_BITRATE, videoBitrate);
        outType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
        DWORD index = 0;
        if (FAILED(hr = writer->AddStream(outType.get(), &index))) return { nullptr, hr };
        if (FAILED(hr = writer->SetInputMediaType(index, VideoType(MFVideoFormat_NV12, outW, outH, num, den).get(), nullptr))) return { nullptr, hr };
        if (hasAudio)
        {
            if (FAILED(hr = writer->AddStream(AacType(audioBytes).get(), &index))) return { nullptr, hr };
            if (FAILED(hr = writer->SetInputMediaType(index, PcmType().get(), nullptr))) return { nullptr, hr };
        }
        if (FAILED(hr = writer->BeginWriting())) return { nullptr, hr };
        return { writer, S_OK };
    };
    auto [writer, writerHr] = makeWriter(true);
    if (!writer) std::tie(writer, writerHr) = makeWriter(false);
    if (!writer) return Failure(writerHr, "No H.264 encoder").what();

    PROPVARIANT position;
    InitPropVariantFromInt64(std::max<int64_t>(0, start), &position);
    reader->SetCurrentPosition(GUID_NULL, position);
    PropVariantClear(&position);

    bool videoDone = false, audioDone = !hasAudio;
    std::vector<uint8_t> frame(static_cast<size_t>(outW) * outH * 3 / 2);
    while (!videoDone || !audioDone)
    {
        DWORD actual = 0, flags = 0;
        LONGLONG time = 0;
        com_ptr<IMFSample> sample;
        Check(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &actual, &flags, &time, sample.put()), "decode");
        com_ptr<IMFMediaType> current;
        GUID major{};
        if (SUCCEEDED(reader->GetCurrentMediaType(actual, current.put()))) current->GetMajorType(&major);
        bool isVideo = major == MFMediaType_Video;
        bool& done = isVideo ? videoDone : audioDone;
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { done = true; continue; }
        if (!sample || done) continue;
        if (time < start) continue;
        if (end > 0 && time > end) { done = true; continue; }
        LONGLONG sampleDuration = 0;
        sample->GetSampleDuration(&sampleDuration);
        com_ptr<IMFSample> out;
        if (isVideo)
        {
            // Copy the picture (cropped for vertical) into one tight NV12 frame.
            com_ptr<IMFMediaBuffer> buffer;
            Check(sample->GetBufferByIndex(0, buffer.put()), "frame");
            BYTE* src = nullptr;
            LONG pitch = 0;
            auto twoD = buffer.try_as<IMF2DBuffer>();
            DWORD length = 0;
            if (twoD) Check(twoD->Lock2D(&src, &pitch), "frame lock");
            else { Check(buffer->Lock(&src, nullptr, &length), "frame lock"); pitch = static_cast<LONG>(readW); }
            const BYTE* luma = src;
            // NV12 chroma follows the luma plane; its height is padded to the pitch's surface.
            UINT32 surfaceH = readH;
            if (!twoD && length >= static_cast<DWORD>(pitch) * readH * 3 / 2) surfaceH = static_cast<UINT32>(length * 2 / 3 / pitch);
            const BYTE* chroma = src + static_cast<size_t>(pitch) * surfaceH;
            if (twoD)
            {
                DWORD contiguous = 0;
                twoD->GetContiguousLength(&contiguous);
                if (contiguous >= static_cast<DWORD>(std::abs(pitch)) * readH * 3 / 2)
                    chroma = src + static_cast<size_t>(pitch) * (contiguous * 2 / 3 / std::abs(pitch));
            }
            for (UINT32 y = 0; y < outH; ++y)
                memcpy(frame.data() + static_cast<size_t>(y) * outW, luma + static_cast<size_t>(y) * pitch + cropX, outW);
            for (UINT32 y = 0; y < outH / 2; ++y)
                memcpy(frame.data() + static_cast<size_t>(outW) * outH + static_cast<size_t>(y) * outW,
                    chroma + static_cast<size_t>(y) * pitch + cropX, outW);
            if (twoD) twoD->Unlock2D(); else buffer->Unlock();

            com_ptr<IMFMediaBuffer> copy;
            Check(MFCreateMemoryBuffer(static_cast<DWORD>(frame.size()), copy.put()), "frame buffer");
            BYTE* dst = nullptr;
            copy->Lock(&dst, nullptr, nullptr);
            memcpy(dst, frame.data(), frame.size());
            copy->Unlock();
            copy->SetCurrentLength(static_cast<DWORD>(frame.size()));
            Check(MFCreateSample(out.put()), "sample");
            out->AddBuffer(copy.get());
        }
        else out = sample;
        out->SetSampleTime(time - start);
        out->SetSampleDuration(sampleDuration);
        Check(writer->WriteSample(isVideo ? 0 : 1, out.get()), "encode");
    }
    Check(writer->Finalize(), "finish the file");
    return {};
}
}

std::string ExportClip(const std::wstring& input, const std::wstring& output, const std::wstring& preset, int64_t start, int64_t end)
{
    try
    {
        auto error = ExportOnce(input, output, preset, start, end, 1.0);
        if (!error.empty() || preset != L"discord") return error;
        // Encoders overshoot a little on busy scenes: one tighter pass if it did.
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (GetFileAttributesExW(output.c_str(), GetFileExInfoStandard, &info) &&
            (static_cast<uint64_t>(info.nFileSizeHigh) << 32 | info.nFileSizeLow) > 10ull * 1000 * 1000)
            return ExportOnce(input, output, preset, start, end, 0.8);
        return {};
    }
    catch (const std::exception& e)
    {
        DeleteFileW(output.c_str());
        return std::string("Export failed: ") + e.what();
    }
}
