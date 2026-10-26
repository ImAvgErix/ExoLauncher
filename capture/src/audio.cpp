// Exo Capture - sound: the mix of the game, Discord and the microphone, then each on its own track, as AAC.
// GPL-3.0-or-later.
#include "audio.h"

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <tlhelp32.h>

#include <algorithm>
#include <map>

namespace
{
constexpr int Rate = 48000;
constexpr int Block = 480;            // 10 ms
// Mix what arrived up to 250 ms ago: Exo's cleaned voice comes as late as its chain holds it (NVIDIA's model 70 ms)
// plus a frame and the pipe, stamped with when it was spoken.
constexpr int64_t MixDelay = 2'500'000;
// Exo fed the microphone track this recently: the raw microphone stays out of it.
constexpr int64_t FedFresh = 3'000'000;
constexpr int64_t RingFrames = Rate * 2;

WAVEFORMATEX Pcm48()
{
    WAVEFORMATEX f{};
    f.wFormatTag = WAVE_FORMAT_PCM;
    f.nChannels = 2;
    f.nSamplesPerSec = Rate;
    f.wBitsPerSample = 16;
    f.nBlockAlign = 4;
    f.nAvgBytesPerSec = Rate * 4;
    return f;
}

struct ActivateHandler : winrt::implements<ActivateHandler, IActivateAudioInterfaceCompletionHandler>
{
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT result = E_FAIL;
    com_ptr<IAudioClient> client;
    ~ActivateHandler() { CloseHandle(done); }
    HRESULT __stdcall ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) noexcept override
    {
        com_ptr<::IUnknown> unknown;
        HRESULT hr = E_FAIL;
        if (SUCCEEDED(operation->GetActivateResult(&hr, unknown.put())) && SUCCEEDED(hr) && unknown)
        {
            client = unknown.try_as<IAudioClient>();
            result = client ? S_OK : E_NOINTERFACE;
        }
        else result = FAILED(hr) ? hr : E_FAIL;
        SetEvent(done);
        return S_OK;
    }
};
}

// ---------------------------------------------------------------- one WASAPI stream

class AudioSource;
namespace
{
// The microphone track's source while a capture runs, for Exo's voice feed; and when Exo last fed it.
std::mutex g_micLock;
AudioSource* g_mic = nullptr;
std::atomic<int64_t> g_fedAt{ 0 };
// Set when Exo's voice feed ends: the microphone starts again that moment.
HANDLE g_feedEnded = CreateEventW(nullptr, FALSE, FALSE, nullptr);
bool Fed() { return Now100ns() - g_fedAt.load() < FedFresh; }
}

class AudioSource
{
public:
    /// Discord: the voice of a call, Discord's process tree or, without the Discord app, Exo's (its own Discord
    /// client plays calls itself), looked for again while neither is running (or after it closed).
    enum class Kind { System, ProcessInclude, ProcessExclude, Discord, Microphone };

    AudioSource(Kind kind, DWORD pid, int64_t origin) : m_kind(kind), m_pid(pid), m_origin(origin), m_ring(RingFrames * 2)
    {
        if (m_kind == Kind::Microphone) { std::lock_guard lock(g_micLock); g_mic = this; }
        m_thread = std::thread([this] { Run(); });
    }

    ~AudioSource()
    {
        if (m_kind == Kind::Microphone) { std::lock_guard lock(g_micLock); if (g_mic == this) g_mic = nullptr; }
        m_stop = true;
        if (m_thread.joinable()) m_thread.join();
    }

    /// Exo's cleaned voice, in place of the raw microphone (StartVoiceFeed).
    void Feed(int64_t qpc, const int16_t* samples, UINT32 frames) { Write(qpc, samples, frames); }

    /// Frames [frame, frame + count) as 16-bit stereo; silence where nothing arrived.
    void Read(int64_t frame, int16_t* out, int count)
    {
        std::lock_guard lock(m_lock);
        for (int i = 0; i < count; ++i)
        {
            int64_t f = frame + i;
            if (m_written >= 0 && f < m_written && f >= m_written - RingFrames && f >= m_floor)
            {
                size_t at = static_cast<size_t>(f % RingFrames) * 2;
                out[i * 2] = m_ring[at];
                out[i * 2 + 1] = m_ring[at + 1];
            }
            else out[i * 2] = out[i * 2 + 1] = 0;
        }
    }

private:
    com_ptr<IAudioClient> Open()
    {
        auto format = Pcm48();
        com_ptr<IAudioClient> client;
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (m_kind == Kind::Discord)
        {
            m_pid = FindDiscord();
            if (!m_pid) throw Failure(S_FALSE, "Discord is not running");
            Log("Sound: the voice track follows process " + std::to_string(m_pid));
            m_process = winrt::handle(OpenProcess(SYNCHRONIZE, FALSE, m_pid));
        }
        if (m_kind != Kind::System && m_kind != Kind::Microphone)
        {
            AUDIOCLIENT_ACTIVATION_PARAMS params{};
            params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            params.ProcessLoopbackParams.TargetProcessId = m_pid;
            params.ProcessLoopbackParams.ProcessLoopbackMode = m_kind == Kind::ProcessExclude
                ? PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE : PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
            PROPVARIANT value{};
            value.vt = VT_BLOB;
            value.blob.cbSize = sizeof(params);
            value.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
            auto handler = winrt::make_self<ActivateHandler>();
            com_ptr<IActivateAudioInterfaceAsyncOperation> operation;
            Check(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &value,
                handler.get(), operation.put()), "app sound");
            if (WaitForSingleObject(handler->done, 5000) != WAIT_OBJECT_0) throw Failure(E_ABORT, "app sound timed out");
            Check(handler->result, "app sound");
            client = handler->client;
            flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        }
        else
        {
            com_ptr<IMMDeviceEnumerator> devices;
            Check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(devices.put())), "sound devices");
            com_ptr<IMMDevice> device;
            Check(devices->GetDefaultAudioEndpoint(m_kind == Kind::Microphone ? eCapture : eRender, eConsole, device.put()),
                m_kind == Kind::Microphone ? "no microphone" : "no sound output");
            Check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()), "sound device");
            flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (m_kind == Kind::System) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        }
        Check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2'000'000, 0, &format, nullptr), "sound format");
        return client;
    }

    void Run()
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        while (!m_stop)
        {
            try
            {
                auto client = Open();
                Check(client->SetEventHandle(ready), "sound event");
                com_ptr<IAudioCaptureClient> capture;
                Check(client->GetService(IID_PPV_ARGS(capture.put())), "sound capture");
                Check(client->Start(), "sound start");
                while (!m_stop)
                {
                    if (m_kind == Kind::Microphone && Fed())
                    {
                        // Exo feeds this track its cleaned voice from the same microphone: this stream stops (Windows then
                        // shows Exo alone using the microphone) and starts again the moment the feed stops, in under a
                        // millisecond, as the stream stays set up. What it held from before is dropped, not written late.
                        client->Stop();
                        client->Reset();
                        while (!m_stop && Fed())
                            WaitForSingleObject(g_feedEnded, static_cast<DWORD>(std::clamp<int64_t>((g_fedAt.load() + FedFresh - Now100ns()) / 10'000 + 1, 1, 50)));
                        if (m_stop) break;
                        Check(client->Start(), "sound start");
                    }
                    WaitForSingleObject(ready, 20);
                    // Discord closed (an update restarts it): find the new one.
                    if (m_process && WaitForSingleObject(m_process.get(), 0) == WAIT_OBJECT_0) throw Failure(S_FALSE, "Discord closed");
                    UINT32 packet = 0;
                    Check(capture->GetNextPacketSize(&packet), "sound read");
                    while (packet > 0)
                    {
                        BYTE* data = nullptr;
                        UINT32 frames = 0;
                        DWORD flags = 0;
                        UINT64 position = 0, qpc = 0;
                        Check(capture->GetBuffer(&data, &frames, &flags, &position, &qpc), "sound buffer");
                        int64_t at = qpc ? static_cast<int64_t>(qpc) : Now100ns() - static_cast<int64_t>(frames) * 10'000'000 / Rate;
                        // Exo's feed just began: the raw microphone is read and let go until the stream stops.
                        if (m_kind != Kind::Microphone || !Fed())
                            Write(at, (flags & AUDCLNT_BUFFERFLAGS_SILENT) ? nullptr : reinterpret_cast<const int16_t*>(data), frames);
                        capture->ReleaseBuffer(frames);
                        Check(capture->GetNextPacketSize(&packet), "sound read");
                    }
                }
                client->Stop();
            }
            catch (const std::exception& e)
            {
                // No microphone or Discord yet, or a device unplugged or switched: the track stays
                // silent and this tries again shortly (Discord less often: finding it walks every process).
                if (!m_failedOnce) Log(std::string("Sound source: ") + e.what());
                m_failedOnce = true;
                for (int i = 0; i < (m_kind == Kind::Discord ? 100 : 20) && !m_stop; ++i) Sleep(50);
            }
        }
        CloseHandle(ready);
    }

    void Write(int64_t qpc, const int16_t* samples, UINT32 frames)
    {
        std::lock_guard lock(m_lock);
        int64_t expected = (qpc - m_origin) * Rate / 10'000'000;
        if (m_written < 0) m_written = m_floor = expected;
        if (std::llabs(expected - m_written) > Rate / 50)
        {
            // Loopback sends nothing while silent; place this packet where it belongs.
            for (int64_t f = m_written; f < expected && f < m_written + RingFrames; ++f)
            {
                size_t at = static_cast<size_t>(((f % RingFrames) + RingFrames) % RingFrames) * 2;
                m_ring[at] = m_ring[at + 1] = 0;
            }
            m_written = expected;
        }
        for (UINT32 i = 0; i < frames; ++i, ++m_written)
        {
            if (m_written < 0) continue;
            size_t at = static_cast<size_t>(m_written % RingFrames) * 2;
            m_ring[at] = samples ? samples[i * 2] : 0;
            m_ring[at + 1] = samples ? samples[i * 2 + 1] : 0;
        }
    }

    Kind m_kind;
    DWORD m_pid;
    winrt::handle m_process; // Discord, to notice it closed
    int64_t m_origin;
    std::mutex m_lock;
    std::vector<int16_t> m_ring;
    int64_t m_written = -1;
    int64_t m_floor = 0;
    bool m_failedOnce = false;
    std::atomic<bool> m_stop{ false };
    std::thread m_thread;
};

// ---------------------------------------------------------------- AAC

class AacEncoder
{
public:
    AacEncoder(int track, std::function<void(PacketPtr)> sink) : m_track(track), m_sink(std::move(sink))
    {
        MFT_REGISTER_TYPE_INFO in{ MFMediaType_Audio, MFAudioFormat_PCM };
        MFT_REGISTER_TYPE_INFO out{ MFMediaType_Audio, MFAudioFormat_AAC };
        IMFActivate** list = nullptr;
        UINT32 count = 0;
        Check(MFTEnumEx(MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, &in, &out, &list, &count), "AAC encoder");
        HRESULT hr = count ? list[0]->ActivateObject(IID_PPV_ARGS(m_mft.put())) : MF_E_TOPO_CODEC_NOT_FOUND;
        for (UINT32 i = 0; i < count; ++i) list[i]->Release();
        CoTaskMemFree(list);
        Check(hr, "AAC encoder");

        com_ptr<IMFMediaType> pcm;
        Check(MFCreateMediaType(pcm.put()), "media type");
        pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        pcm->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
        pcm->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
        pcm->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
        pcm->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Rate * 4);
        // The AAC encoder takes its output type first: 48 kHz stereo at 192 kbit/s, raw AAC.
        com_ptr<IMFMediaType> aac;
        Check(MFCreateMediaType(aac.put()), "media type");
        aac->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        aac->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        aac->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        aac->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
        aac->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
        aac->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
        aac->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
        aac->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        Check(m_mft->SetOutputType(0, aac.get(), 0), "AAC output");
        Check(m_mft->SetInputType(0, pcm.get(), 0), "AAC input");
        // The type it settled on carries the decoder configuration MP4 files need.
        Check(m_mft->GetOutputCurrentType(0, m_type.put()), "AAC type");
        m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        m_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        MFT_OUTPUT_STREAM_INFO info{};
        m_mft->GetOutputStreamInfo(0, &info);
        m_outSize = std::max<DWORD>(info.cbSize, 8192);
    }

    com_ptr<IMFMediaType> Type() const { return m_type; }

    void Encode(const int16_t* pcm, int frames, int64_t time)
    {
        DWORD bytes = static_cast<DWORD>(frames) * 4;
        com_ptr<IMFMediaBuffer> buffer;
        Check(MFCreateMemoryBuffer(bytes, buffer.put()), "sound buffer");
        BYTE* data = nullptr;
        buffer->Lock(&data, nullptr, nullptr);
        memcpy(data, pcm, bytes);
        buffer->Unlock();
        buffer->SetCurrentLength(bytes);
        com_ptr<IMFSample> sample;
        Check(MFCreateSample(sample.put()), "sound sample");
        sample->AddBuffer(buffer.get());
        sample->SetSampleTime(time);
        sample->SetSampleDuration(static_cast<int64_t>(frames) * 10'000'000 / Rate);
        Check(m_mft->ProcessInput(0, sample.get(), 0), "AAC encode");
        for (;;)
        {
            com_ptr<IMFMediaBuffer> outBuffer;
            Check(MFCreateMemoryBuffer(m_outSize, outBuffer.put()), "AAC buffer");
            com_ptr<IMFSample> outSample;
            Check(MFCreateSample(outSample.put()), "AAC sample");
            outSample->AddBuffer(outBuffer.get());
            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = outSample.get();
            DWORD status = 0;
            HRESULT hr = m_mft->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            Check(hr, "AAC output");
            auto packet = std::make_shared<Packet>();
            packet->track = m_track;
            packet->key = true;
            LONGLONG t = 0, d = 0;
            outSample->GetSampleTime(&t);
            outSample->GetSampleDuration(&d);
            packet->time = t;
            packet->duration = d;
            BYTE* encoded = nullptr;
            DWORD length = 0;
            outBuffer->Lock(&encoded, nullptr, &length);
            packet->data.assign(encoded, encoded + length);
            outBuffer->Unlock();
            if (!packet->data.empty() && m_sink) m_sink(packet);
        }
    }

private:
    int m_track;
    std::function<void(PacketPtr)> m_sink;
    com_ptr<IMFTransform> m_mft;
    com_ptr<IMFMediaType> m_type;
    DWORD m_outSize = 8192;
};

// ---------------------------------------------------------------- capture and mix

AudioCapture::AudioCapture(const Options& options, std::function<void(PacketPtr)> sink)
{
    m_origin = Now100ns();
    if (options.gamePid)
        m_sources.push_back(std::make_unique<AudioSource>(AudioSource::Kind::ProcessInclude, options.gamePid, m_origin));
    else if (options.discordPid)
        m_sources.push_back(std::make_unique<AudioSource>(AudioSource::Kind::ProcessExclude, options.discordPid, m_origin));
    else
        m_sources.push_back(std::make_unique<AudioSource>(AudioSource::Kind::System, 0, m_origin));
    m_sources.push_back(std::make_unique<AudioSource>(AudioSource::Kind::Discord, 0, m_origin));
    m_sources.push_back(std::make_unique<AudioSource>(AudioSource::Kind::Microphone, 0, m_origin));

    // Track 1 is what players play: the mix. Each source follows on its own track, to mute in an editor.
    for (int track = 1; track <= static_cast<int>(m_sources.size()) + 1; ++track)
        m_encoders.push_back(std::make_unique<AacEncoder>(track, sink));
    m_thread = std::thread([this] { Mix(); });
}

AudioCapture::~AudioCapture()
{
    m_stop = true;
    if (m_thread.joinable()) m_thread.join();
    m_sources.clear();
}

std::vector<com_ptr<IMFMediaType>> AudioCapture::Types() const
{
    std::vector<com_ptr<IMFMediaType>> out;
    for (auto& e : m_encoders) out.push_back(e->Type());
    return out;
}

void AudioCapture::Mix()
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    std::vector<std::vector<int16_t>> blocks(m_sources.size(), std::vector<int16_t>(Block * 2));
    std::vector<int16_t> mix(Block * 2);
    int64_t position = 0;
    while (!m_stop)
    {
        Sleep(10);
        int64_t due = (Now100ns() - m_origin - MixDelay) * Rate / 10'000'000;
        while (position + Block <= due && !m_stop)
        {
            for (size_t s = 0; s < m_sources.size(); ++s) m_sources[s]->Read(position, blocks[s].data(), Block);
            int64_t time = m_origin + position * 10'000'000 / Rate;
            try
            {
                for (int i = 0; i < Block * 2; ++i)
                {
                    int sum = 0;
                    for (auto& b : blocks) sum += b[i];
                    mix[i] = static_cast<int16_t>(std::clamp(sum, -32768, 32767));
                }
                m_encoders[0]->Encode(mix.data(), Block, time);
                for (size_t s = 0; s < m_sources.size(); ++s) m_encoders[s + 1]->Encode(blocks[s].data(), Block, time);
            }
            catch (const std::exception& e) { Log(std::string("Sound encode: ") + e.what()); }
            position += Block;
        }
    }
}

void StartVoiceFeed(HANDLE pipe)
{
    if (!pipe || pipe == INVALID_HANDLE_VALUE) return;
    std::thread([pipe] {
        Log("Sound: the microphone track takes Exo's cleaned voice");
        struct Header { int64_t at; int32_t frames; int32_t channels; } header{};
        std::vector<int16_t> samples;
        auto readAll = [pipe](void* to, DWORD bytes) {
            auto* p = static_cast<BYTE*>(to);
            while (bytes)
            {
                DWORD got = 0;
                if (!ReadFile(pipe, p, bytes, &got, nullptr) || !got) return false;
                p += got;
                bytes -= got;
            }
            return true;
        };
        while (readAll(&header, sizeof(header)))
        {
            // Exo's frames are 960 stereo samples; anything else is not a frame of its voice.
            if (header.channels != 2 || header.frames <= 0 || header.frames > Rate) break;
            samples.resize(static_cast<size_t>(header.frames) * 2);
            if (!readAll(samples.data(), static_cast<DWORD>(samples.size() * sizeof(int16_t)))) break;
            g_fedAt = Now100ns();
            std::lock_guard lock(g_micLock);
            if (g_mic) g_mic->Feed(header.at, samples.data(), static_cast<UINT32>(header.frames));
        }
        // Exo stopped feeding (the capture stopped, or Exo closed): the raw microphone comes back at once.
        g_fedAt = 0;
        SetEvent(g_feedEnded);
        CloseHandle(pipe);
        Log("Sound: Exo's voice feed ended");
    }).detach();
}

DWORD FindDiscord()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    std::map<DWORD, std::pair<DWORD, std::wstring>> processes;
    PROCESSENTRY32W entry{ sizeof(entry) };
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry))
        processes[entry.th32ProcessID] = { entry.th32ParentProcessID, entry.szExeFile };
    CloseHandle(snapshot);
    // The root of a tree of these names: the Discord app's, else Exo's, whose own Discord client plays a call from
    // Exo itself (with no Discord app the voices were on no track, and missing from the mix of a game's capture).
    auto rootOf = [&](auto matches) -> DWORD {
        for (auto& [pid, info] : processes)
        {
            if (!matches(info.second)) continue;
            auto parent = processes.find(info.first);
            if (parent == processes.end() || !matches(parent->second.second)) return pid;
        }
        return 0;
    };
    if (DWORD discord = rootOf([](const std::wstring& name) {
        return _wcsicmp(name.c_str(), L"Discord.exe") == 0 || _wcsicmp(name.c_str(), L"DiscordPTB.exe") == 0 ||
            _wcsicmp(name.c_str(), L"DiscordCanary.exe") == 0;
    })) return discord;
    return rootOf([](const std::wstring& name) { return _wcsicmp(name.c_str(), L"ExoLauncher.exe") == 0; });
}
