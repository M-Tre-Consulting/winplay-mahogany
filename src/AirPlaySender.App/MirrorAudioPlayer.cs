using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace AirPlaySender.App;

/// <summary>
/// Plays the decoded mirroring audio through a WinRT <see cref="AudioGraph"/>:
/// PCM frames are pushed into a small ring buffer, and an
/// <see cref="AudioFrameInputNode"/> pulls from it each audio quantum. If the
/// buffer runs dry (a network hiccup) it feeds silence rather than glitching
/// the graph; if it overfills (we're drifting ahead of the clock) the oldest
/// samples are dropped to stay near real time.
/// </summary>
internal sealed class MirrorAudioPlayer : IAsyncDisposable
{
    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

    private readonly int _sampleRate;
    private readonly int _channels;

    private readonly object _gate = new();
    private readonly Queue<float> _ring = new();
    private int _maxSamples;   // ring cap (drop-oldest above this)
    private int _primeSamples; // don't start handing out audio until this much is buffered
    private bool _primed;

    private AudioGraph? _graph;
    private AudioDeviceOutputNode? _output;
    private AudioFrameInputNode? _input;

    public Action<string>? Diagnostics;

    public MirrorAudioPlayer(int sampleRate = 44100, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _maxSamples = sampleRate * channels / 2;   // ~500ms hard cap
        _primeSamples = sampleRate * channels / 20; // ~50ms before playback starts
    }

    public async Task StartAsync()
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.Media)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
        };
        CreateAudioGraphResult gr = await AudioGraph.CreateAsync(settings);
        if (gr.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"AudioGraph.CreateAsync: {gr.Status} ({gr.ExtendedError?.Message})");
        _graph = gr.Graph;

        CreateAudioDeviceOutputNodeResult or = await _graph.CreateDeviceOutputNodeAsync();
        if (or.Status != AudioDeviceNodeCreationStatus.Success)
            throw new InvalidOperationException($"CreateDeviceOutputNodeAsync: {or.Status} ({or.ExtendedError?.Message})");
        _output = or.DeviceOutputNode;

        AudioEncodingProperties props = AudioEncodingProperties.CreatePcm((uint)_sampleRate, (uint)_channels, 32);
        props.Subtype = MediaEncodingSubtypes.Float; // AudioFrame wants float32

        _input = _graph.CreateFrameInputNode(props);
        _input.AddOutgoingConnection(_output);
        _input.QuantumStarted += OnQuantumStarted;

        _graph.Start();
        Diagnostics?.Invoke($"AudioGraph avviato: {_sampleRate}Hz {_channels}ch, quantum {_graph.SamplesPerQuantum} campioni");
    }

    /// <summary>Push one decoded PCM frame (interleaved int16).</summary>
    public void Enqueue(short[] pcm)
    {
        lock (_gate)
        {
            for (int i = 0; i < pcm.Length; i++)
                _ring.Enqueue(pcm[i] * (1f / 32768f));

            if (_ring.Count > _maxSamples)
            {
                int drop = _ring.Count - _maxSamples;
                for (int i = 0; i < drop; i++) _ring.Dequeue();
                Diagnostics?.Invoke($"audio: buffer troppo pieno, scartati {drop} campioni");
            }
        }
    }

    private void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        int needSamples = args.RequiredSamples;
        if (needSamples <= 0) return;

        int floats = needSamples * _channels;
        var chunk = new float[floats];

        lock (_gate)
        {
            if (!_primed)
            {
                if (_ring.Count < _primeSamples) return; // still filling — output nothing yet
                _primed = true;
            }
            int take = Math.Min(floats, _ring.Count);
            for (int i = 0; i < take; i++) chunk[i] = _ring.Dequeue();
            // remainder stays 0 => silence padding on underrun
            if (take < floats && _ring.Count == 0) _primed = false; // re-prime after a full drain
        }

        AudioFrame frame = MakeFrame(chunk);
        sender.AddFrame(frame);
    }

    private unsafe AudioFrame MakeFrame(float[] interleaved)
    {
        uint bytes = (uint)(interleaved.Length * sizeof(float));
        var frame = new AudioFrame(bytes);
        using (AudioBuffer buf = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buf.CreateReference())
        {
            ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* dst, out uint cap);
            fixed (float* src = interleaved)
                Buffer.MemoryCopy(src, dst, cap, Math.Min(cap, bytes));
        }
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_input is not null) _input.QuantumStarted -= OnQuantumStarted;
            _graph?.Stop();
        }
        catch { }
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _graph?.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
