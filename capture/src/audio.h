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
        DWORD discordPid = 0; // Discord's root process now, 0 when it is not running
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

/// The process id of the running Discord app (its top process), or 0.
DWORD FindDiscord();
