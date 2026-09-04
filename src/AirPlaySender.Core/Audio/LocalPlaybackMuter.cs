using NAudio.CoreAudioApi;

namespace AirPlaySender.Core.Audio;

/// <summary>
/// Mutes/restores the default audio render device's own hardware mute —
/// used so that streaming system audio to an AirPlay speaker doesn't also
/// keep playing out of the PC's own speakers at the same time (reported by
/// the user live: connecting to a HomePod duplicated the audio, one copy
/// from the PC and one from the HomePod).
///
/// This works because <see cref="WasapiLoopbackSource"/>'s WASAPI loopback
/// capture taps the render endpoint's audio engine mix *upstream* of the
/// final hardware mute/volume stage — muting the endpoint via
/// <see cref="AudioEndpointVolume"/> silences the PC's own speakers without
/// stopping what loopback capture sees, so the AirPlay stream is unaffected.
/// This is standard, well-known Windows audio engine behavior (the same
/// trick most "silence local playback while casting" tools use), not
/// something specific to this project.
/// </summary>
public sealed class LocalPlaybackMuter : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    // The device's mute state from BEFORE this class first touched it — so
    // Restore() puts it back exactly as found, not just "unmuted": the user
    // may already have muted their speakers themselves before connecting.
    private bool? _originalMute;

    /// <summary>Mutes the current default render device. Best-effort — a failure here should never be allowed to break streaming itself.</summary>
    public void Mute()
    {
        try
        {
            _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _originalMute ??= _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.Mute = true;
        }
        catch { /* best-effort — losing the mute toggle isn't worth failing streaming over */ }
    }

    /// <summary>Restores whatever mute state the device had before the first <see cref="Mute"/> call.</summary>
    public void Restore()
    {
        try
        {
            if (_device is not null && _originalMute is { } original)
                _device.AudioEndpointVolume.Mute = original;
        }
        catch { /* best-effort */ }
        finally { _originalMute = null; }
    }

    public void Dispose()
    {
        Restore();
        try { _device?.Dispose(); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
