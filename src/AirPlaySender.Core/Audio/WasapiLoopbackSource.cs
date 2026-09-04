using System.Buffers.Binary;
using NAudio.Wave;

namespace AirPlaySender.Core.Audio;

/// <summary>
/// Captures Windows' system audio output — WASAPI loopback on the default
/// render device, exactly what every "AirPlay/Cast from Windows" app taps —
/// and exposes it as 44.1kHz interleaved 16-bit stereo PCM via
/// <see cref="IPcmFrameSource"/>. Whatever the device's native mix format is
/// (typically 48kHz/32-bit-float on modern Windows), Media Foundation's own
/// resampler does the rate/bit-depth conversion — no hand-rolled DSP.
/// </summary>
public sealed class WasapiLoopbackSource : IAudioCaptureSource
{
    private const int TargetSampleRate = 44100;
    private const int TargetChannels = 2;
    private const int TargetBitsPerSample = 16;
    private const int BytesPerSample = TargetBitsPerSample / 8;

    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _buffer;
    private readonly MediaFoundationResampler _resampler;
    private readonly object _readLock = new();

    public WasapiLoopbackSource()
    {
        _recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
        _buffer = new BufferedWaveProvider(_recorder.WaveFormat, TimeSpan.FromMilliseconds(500))
        {
            // If the pacer falls behind (GUI stall, debugger pause), drop the
            // oldest audio rather than growing unbounded or blocking capture.
            // Bounded to 500ms, not the 2s this used to allow: with
            // DiscardOnBufferOverflow only kicking in once the buffer is
            // completely full, a longer cap meant capture could silently run
            // up to that whole amount of latency behind real time before
            // correcting — worth tightening on its own even without a live
            // repro, since it only ever makes worst-case staleness better,
            // never worse (steady-state playback stays near-empty regardless
            // of the cap, this only bounds how bad a stall can get before
            // catching up).
            DiscardOnBufferOverflow = true,
        };
        var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
        _resampler = new MediaFoundationResampler(_buffer, targetFormat) { ResamplerQuality = 60 };
        _recorder.DataAvailable += OnDataAvailable;
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, NAudio.CoreAudioApi.AudioClientBufferFlags flags, long devicePosition, long qpcPosition) =>
        _buffer.AddSamples(buffer);

    public void Start() => _recorder.StartRecording();
    public void Stop() => _recorder.StopRecording();

    /// <summary>Non-blocking: pads with silence if not enough audio is buffered yet (nothing currently playing) — the RTP timeline must keep advancing regardless.</summary>
    public void FillFrames(Span<short> destination)
    {
        int neededBytes = destination.Length * BytesPerSample;
        Span<byte> byteBuf = neededBytes <= 4096 ? stackalloc byte[neededBytes] : new byte[neededBytes];

        int got;
        lock (_readLock) // MediaFoundationResampler.Read is not documented thread-safe; the pacer is the only caller today, but guard against future misuse
        {
            got = _resampler.Read(byteBuf);
        }
        if (got < neededBytes) byteBuf[got..].Clear();

        for (int i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(byteBuf.Slice(i * BytesPerSample, BytesPerSample));
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.DataAvailable -= OnDataAvailable;
        await _recorder.DisposeAsync().ConfigureAwait(false);
        _resampler.Dispose();
    }
}
