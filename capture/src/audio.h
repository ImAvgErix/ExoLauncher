// Exo Capture - sound: the mix of the game, Discord and the microphone, then each on its own track, as AAC.
// GPL-3.0-or-later.
#pragma once
#include "common.h"
#include "mux.h"

class AudioSource;
class AacEncoder;

/// Always four tracks: 1 the mix, 2 the game, 3 Discord voice, 4 the microphone. A source that is
/// not there (no Discord, no microphone) is a silent track until it is, so the layout never changes.
class AudioCapture
{
public:
    struct Options
    {
        DWORD gamePid = 0;    // 0: everything on the default output except Discord
        DWORD discordPid = 0; // where a call's voices play now (FindDiscord), 0 when nowhere
    };

    AudioCapture(const Options& options, std::function<void(PacketPtr)> sink);
    ~AudioCapture();
    /// The AAC type of each audio track, track 1 first.
    std::vector<com_ptr<IMFMediaType>> Types() const;

private:
    void Mix();
    std::vector<std::unique_ptr<AudioSource>> m_sources;
    std::vector<std::unique_ptr<AacEncoder>> m_encoders;
    int64_t m_origin = 0;
    std::atomic<bool> m_stop{ false };
    std::thread m_thread;
};

/// Where a call's voices play: the running Discord app's top process, else Exo's (its own Discord client), or 0.
DWORD FindDiscord();

/// Exo's cleaned voice for the microphone track: a pipe Exo writes 20 ms frames to (each a 16-byte header, when its
/// sound was captured in 100 ns units, its sample frames and channels, then 16-bit stereo at 48 kHz). While frames
/// come, the microphone track takes them instead of the raw microphone; this takes the pipe and closes it at its end.
void StartVoiceFeed(HANDLE pipe);
