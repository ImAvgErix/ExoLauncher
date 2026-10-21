namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Noise suppression on one person's voice in a call (Voice & Video › Noise Suppression for Others, off by default; never
/// on a Go Live stream's sound): their decoded frames go through a DeepFilterNet of their own, as each voice has its own
/// room and the model its own state for it, on a thread of their own, so one person's few milliseconds a frame never hold
/// up another's or the receive thread; then to the mix, 20 ms later. Each instance is its own copy of the plugin with its
/// own model (instances sharing one corrupted each other, see NoiseSuppressor.LoadCopy) and, while they talk, costs what the
/// microphone's does. It loads off this thread (an instance let go of before
/// comes back at once); until it is in, their voice plays as it came. The end of each sentence is pushed out by the
/// silence Discord sends after it (<see cref="Drain"/>), instead of waiting in the model for their next word.
/// </summary>
internal sealed class InboundCleaner : IDisposable
{
    private const int MostQueued = 10; // 200 ms: past that the oldest frame is dropped, not played late
    private const int CatchUp = 5; // 100 ms behind: frames play uncleaned until it has caught up
    private readonly Action<float[], float> _play;
    private readonly Func<float[]> _rent;
    private readonly Queue<(float[]? Frame, float Gain)> _frames = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private readonly Task<NoiseSuppressor?> _loading = Task.Run(NoiseSuppressor.Create);
    private NoiseSuppressor? _suppressor;
    private volatile bool _stop;

    /// <param name="play">To the mix: a cleaned frame and their User Volume (<see cref="VoiceAudio.Play"/> for their SSRC).</param>
    /// <param name="rent">A frame to play the model's last of them in (<see cref="VoiceAudio.Rent"/>).</param>
    internal InboundCleaner(Action<float[], float> play, Func<float[]> rent)
    {
        (_play, _rent) = (play, rent);
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo voice in", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>A decoded 20 ms stereo frame of theirs (from <see cref="VoiceAudio.Rent"/>), cleaned and played at their User Volume.</summary>
    internal void Play(float[] frame, float gain) => Queue(frame, gain);

    /// <summary>Discord's silence after they stop: a silent frame through, so what the model still holds of them plays now.</summary>
    internal void Drain() => Queue(null, 1f);

    private void Queue(float[]? frame, float gain)
    {
        lock (_frames)
        {
            _frames.Enqueue((frame, gain));
            while (_frames.Count > MostQueued) _frames.Dequeue();
        }
        _ready.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            _ready.WaitOne(200);
            if (_suppressor is null && _loading.IsCompleted) _suppressor = _loading.IsCompletedSuccessfully ? _loading.Result : null;
            while (!_stop)
            {
                (float[]? Frame, float Gain) next;
                int waiting;
                lock (_frames)
                {
                    if (!_frames.TryDequeue(out next)) break;
                    waiting = _frames.Count;
                }
                var frame = next.Frame;
                if (frame is null)
                {
                    if (_suppressor is null) continue;
                    frame = _rent();
                    Array.Clear(frame);
                }
                // Behind (a busy PC): the frames waiting play as they came until it has caught up, rather than late or
                // not at all; a voice is never lost to its cleaning.
                if (waiting < CatchUp) _suppressor?.Process(frame);
                _play(frame, next.Gain);
            }
        }
    }

    /// <summary>They left or the call ended: the thread stops and the model goes back for the next one.</summary>
    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        _thread.Join(1000);
        _suppressor?.Dispose();
        // One still loading is let go of when it is in.
        if (_suppressor is null) _loading.ContinueWith(t => t.Result?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }
}
