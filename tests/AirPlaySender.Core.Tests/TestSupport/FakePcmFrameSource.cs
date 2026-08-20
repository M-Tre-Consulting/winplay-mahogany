using AirPlaySender.Core.Audio;

namespace AirPlaySender.Core.Tests.TestSupport;

/// <summary>A deterministic, hardware-free stand-in for <see cref="WasapiLoopbackSource"/> — fills every frame with a fixed, recognizable non-zero pattern instead of touching real audio hardware.</summary>
internal sealed class FakePcmFrameSource : IAudioCaptureSource
{
    public const short SampleValue = 0x1111;

    public bool Started { get; private set; }

    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void FillFrames(Span<short> destination) => destination.Fill(SampleValue);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
