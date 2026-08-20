namespace AirPlaySender.Core.Audio;

/// <summary>
/// Supplies 44.1kHz interleaved 16-bit stereo PCM to the RTP pacer.
/// Implementations must be non-blocking and pad with silence when not
/// enough audio is available yet (a paused source, a starved capture
/// buffer) — the RTP timeline must keep advancing regardless, or the
/// receiver declares the stream dead.
/// </summary>
public interface IPcmFrameSource
{
    /// <summary>Fills <paramref name="destination"/> (length = frameCount*2 samples) with the next frames.</summary>
    void FillFrames(Span<short> destination);
}

/// <summary>An <see cref="IPcmFrameSource"/> with an explicit start/stop lifecycle — what <see cref="AirPlaySession"/> actually drives. Lets tests substitute a deterministic fake for the real WASAPI capture.</summary>
public interface IAudioCaptureSource : IPcmFrameSource, IAsyncDisposable
{
    void Start();
    void Stop();
}
