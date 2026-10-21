// Exo Capture - encoded packets, the replay buffer, MP4 files, trim and export. GPL-3.0-or-later.
#pragma once
#include "common.h"

/// One encoded frame. Track 0 is video; 1.. are audio tracks.
struct Packet
{
    int track = 0;
    int64_t time = 0;     // 100 ns, QPC clock
    int64_t duration = 0; // 100 ns
    bool key = false;
    std::vector<uint8_t> data;
};
using PacketPtr = std::shared_ptr<const Packet>;

/// The media types of a capture session's tracks, as the encoders produced them.
struct StreamFormat
{
    com_ptr<IMFMediaType> video;
    std::vector<com_ptr<IMFMediaType>> audio;
    int fps = 60;
};

/// Encoded packets in RAM, the last N seconds starting at a video key frame.
class ReplayBuffer
{
public:
    void SetLength(int64_t length100ns);
    void Push(const PacketPtr& packet);
    /// Every packet from the oldest video key frame, ordered by time.
    std::vector<PacketPtr> Snapshot(int64_t length100ns = 0) const;
    void Clear();
    size_t Bytes() const;
    int64_t Span() const;

private:
    void TrimLocked();
    mutable std::mutex m_lock;
    std::deque<PacketPtr> m_video;
    std::deque<PacketPtr> m_audio;
    size_t m_bytes = 0;
    int64_t m_length = 60LL * 10'000'000;
};

/// An MP4 written from encoded packets, no re-encode.
class Mp4Writer
{
public:
    Mp4Writer(const std::wstring& path, const StreamFormat& format);
    ~Mp4Writer();
    /// Writes a packet; times are rebased so the first video key frame is 0.
    void Write(const PacketPtr& packet);
    /// Flushes and closes; returns the duration written.
    int64_t Finish();

private:
    void WriteNow(const Packet& packet, int64_t duration);
    com_ptr<IMFSinkWriter> m_writer;
    int m_audioTracks = 0;
    int64_t m_base = -1;
    int64_t m_last = 0;
    int64_t m_nominal = 166'667;
    PacketPtr m_pendingVideo;
    bool m_finished = false;
};

/// Every packet of a recording as it arrives, beside the MP4 ("<file>.mp4.exo-rec"). An MP4
/// is only playable once closed (Windows' fragmented MP4 writer takes H.264 only), so if Exo
/// Capture or the PC stops mid-recording, the journal rebuilds the file on the next start.
class Journal
{
public:
    Journal(const std::wstring& path, const StreamFormat& format);
    ~Journal();
    void Append(const Packet& packet);
    /// The recording closed cleanly: the journal is not needed.
    void Discard();

private:
    HANDLE m_file = INVALID_HANDLE_VALUE;
    std::wstring m_path;
};
/// Rebuilds the MP4 a journal belongs to; returns its duration and deletes the journal.
int64_t RecoverJournal(const std::wstring& journal);

/// A manual recording: packets go to a writer thread so the encoders never wait on the disk.
class Recorder
{
public:
    Recorder(const std::wstring& path, const StreamFormat& format);
    ~Recorder();
    void Push(const PacketPtr& packet);
    /// Stops, closes the file and returns its duration.
    int64_t Stop();
    const std::wstring& Path() const { return m_path; }
    std::string Error() const;

private:
    void Run();
    std::wstring m_path;
    std::unique_ptr<Mp4Writer> m_writer;
    std::unique_ptr<Journal> m_journal;
    std::mutex m_lock;
    std::condition_variable m_wake;
    std::deque<PacketPtr> m_queue;
    bool m_stop = false;
    bool m_keySeen = false;
    int64_t m_duration = 0;
    std::string m_error;
    std::thread m_thread;
};

/// Rewrites a video from start to end without re-encoding; cuts at the key frame at or before start.
int64_t TrimCopy(const std::wstring& input, const std::wstring& output, int64_t start, int64_t end);

/// Re-encodes start..end as H.264 for sharing: "discord" fits Discord's 10 MB limit,
/// "vertical" crops the middle to 9:16. Returns a short error, or empty on success.
std::string ExportClip(const std::wstring& input, const std::wstring& output, const std::wstring& preset, int64_t start, int64_t end);

/// The duration of a media file, 100 ns; 0 when unknown.
int64_t MediaDuration(const std::wstring& path);
