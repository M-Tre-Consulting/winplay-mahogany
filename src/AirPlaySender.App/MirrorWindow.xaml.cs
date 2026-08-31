using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using AirPlaySender.Core.Receiving;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace AirPlaySender.App;

/// <summary>
/// The window that actually shows the mirrored iPhone screen — subscribes
/// directly to one <see cref="MirroringDataReceiver"/>'s SPS/PPS and NAL
/// events (see <see cref="AirPlayReceiverServer.MirroringSessionStarted"/>)
/// and feeds them into a WinRT <see cref="MediaStreamSource"/>, which does
/// the actual H.264 decode (hardware-accelerated, via the OS) and rendering
/// through a <see cref="MediaPlayerElement"/>.
///
/// Confirmed against Microsoft's own reference implementation
/// (webrtc-uwp's <c>impl_webrtc_MediaStreamSource.cpp</c>, a real,
/// shipped custom-H264-over-MediaStreamSource integration) before writing
/// this, not guessed: a <see cref="MediaStreamSource"/> built from
/// <see cref="VideoEncodingProperties.CreateH264"/> wants **Annex-B**
/// (<c>00 00 00 01</c> start codes) samples, SPS/PPS included as ordinary
/// in-band samples — not the AVCC framing <see cref="MirroringDataReceiver"/>
/// hands over, which is why every sample gets re-prefixed here before being
/// handed to the pipeline. Tried switching to AVCC + <c>SetFormatUserData</c>
/// (matching how virtually every real MP4 on Windows is fed to this same
/// decoder) as a later experiment, reasoning CreateH264()'s Annex-B
/// expectation might just be about the in-band-vs-extradata SPS/PPS
/// convention — confirmed live that it isn't: <c>MediaOpened</c> stopped
/// firing at all (worse than Annex-B's "renders then freezes"), so
/// CreateH264()'s preset is tied to Annex-B framing itself, not just where
/// the parameter sets come from. Reverted.
/// </summary>
public sealed partial class MirrorWindow : Window
{
    private readonly Channel<byte[]> _samples = Channel.CreateUnbounded<byte[]>();
    // Reset in StartPipeline, right before Play() — NOT the moment this constructor runs.
    // Between construction and the pipeline actually starting, Attach() + waiting for the
    // phone's SPS/PPS packet can burn real wall-clock time; leaving this at construction
    // time meant the very first sample's timestamp was already offset from zero by however
    // long that took, while SetActualStartPosition(TimeSpan.Zero) told the engine playback
    // starts AT zero — a real, avoidable mismatch between what we declared and what we did.
    private DateTime _streamStart = DateTime.UtcNow;
    private MediaStreamSource? _mss;
    private MediaPlayer? _player;
    private bool _configured;

    public MirrorWindow()
    {
        InitializeComponent();
        Title = "WinPlay Mahogany — Mirroring";
        SetWindowIcon();
    }

    private void SetWindowIcon()
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Attaches this window to one mirroring session's events. Call once, right after construction.</summary>
    public void Attach(MirroringDataReceiver receiver)
    {
        receiver.ConfigReceived += OnConfigReceived;
        receiver.NalReceived += OnNalReceived;
        receiver.SessionEnded += OnSessionEnded;
        AppLog.Write("MirrorWindow.Attach: in ascolto di ConfigReceived/NalReceived/SessionEnded");
    }

    /// <summary>
    /// True once this window is closing because <see cref="OnSessionEnded"/> fired, as
    /// opposed to the user clicking the window's own close button. App.xaml.cs checks this
    /// before calling <see cref="MirroringDataReceiver.RequestSessionClose"/> on <see
    /// cref="Closed"/> — without it, a window that auto-closes because ITS OWN receiver was
    /// superseded (the proactive-offer receiver every session briefly creates, replaced the
    /// instant a real streams-array SETUP arrives — see AirPlayReceiverServer) would end up
    /// closing the shared RTSP connection too, taking the real, just-started session down
    /// with it — confirmed live: connect, then an near-instant disconnect with no video ever
    /// shown, from exactly this.
    /// </summary>
    private bool _closingBecauseSessionEnded;

    /// <summary>The phone stopped mirroring (or the connection otherwise ended) — close instead of sitting there showing a frozen last frame forever.</summary>
    private void OnSessionEnded()
    {
        AppLog.Write("MirrorWindow: sessione di mirroring terminata, chiudo la finestra");
        DispatcherQueue.TryEnqueue(() =>
        {
            _closingBecauseSessionEnded = true;
            Close();
        });
    }

    /// <summary>Whether <see cref="Closed"/> should also ask the RTSP connection to close (a real, user-initiated close) or not (this window's own session already ended on its own).</summary>
    public bool ShouldRequestSessionClose => !_closingBecauseSessionEnded;

    private void OnConfigReceived(byte[] sps, byte[] pps)
    {
        AppLog.Write($"MirrorWindow.OnConfigReceived: SPS {sps.Length} byte, PPS {pps.Length} byte");
        if (_configured) return; // one MediaStreamSource per window/session — a re-SETUP mid-session isn't handled here
        _configured = true;

        if (!H264Sps.TryParseDimensions(sps, out int width, out int height))
        {
            // Common-case parser (see H264Sps's doc comment) didn't recognize this SPS shape —
            // fall back to a size that still lets the pipeline negotiate the real one from the stream itself.
            AppLog.Write("  H264Sps non ha riconosciuto questa SPS — uso il fallback 1920x1080");
            width = 1920;
            height = 1080;
        }
        else
        {
            AppLog.Write($"  Dimensioni vere lette dalla SPS: {width}x{height}");
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ResizeToVideo(width, height);
                // AppWindow.Resize is a Win32-level HWND resize — force XAML's own layout
                // pass to catch up synchronously before any real frame starts flowing,
                // rather than trusting it happens before Player's SwapChainPanel gets its
                // first frame on its own schedule. Cheap insurance against exactly the kind
                // of "video renders tiny, anchored top-left, at whatever size the window
                // was before this resize" symptom a live session hit.
                (Content as FrameworkElement)?.UpdateLayout();
                AppLog.Write($"  Player.ActualSize dopo UpdateLayout: {Player.ActualWidth}x{Player.ActualHeight}");
                StartPipeline(width, height, sps, pps);
                AppLog.Write("  Pipeline avviata, finestra attivata");
            }
            catch (Exception ex)
            {
                AppLog.Write($"  Avvio pipeline fallito: {ex}");
            }
        });
    }

    private void ResizeToVideo(int width, int height)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            // AppWindow.Resize sets the OUTER window size (title bar + borders included),
            // not the client area the video actually renders into — confirmed live:
            // asking for 666x1440 left a ClientSize of 650x1401, ~16x39px short. Resize
            // once to measure the real non-client overhead, then correct for it, so the
            // video (Stretch="Uniform") doesn't end up letterboxed against a client area
            // whose aspect ratio doesn't quite match the stream's.
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            int deltaW = width - appWindow.ClientSize.Width;
            int deltaH = height - appWindow.ClientSize.Height;
            if (deltaW != 0 || deltaH != 0)
                appWindow.Resize(new Windows.Graphics.SizeInt32(width + deltaW, height + deltaH));

            AppLog.Write($"  ResizeToVideo({width}x{height}) -> ClientSize effettivo: {appWindow.ClientSize.Width}x{appWindow.ClientSize.Height}");
        }
        catch (Exception ex) { AppLog.Write($"  ResizeToVideo fallito: {ex}"); /* non-fatal — window just keeps its default size */ }
    }

    private void StartPipeline(int width, int height, byte[] sps, byte[] pps)
    {
        // See the field's own doc comment: this must reflect when playback actually starts,
        // not when the window was constructed.
        _streamStart = DateTime.UtcNow;
        _lastTimestampTicks = -1;
        _lastSampleDeliveryUtc = null;

        // Diagnostic for a live symptom: video rendering tiny (anchored top-left, whatever
        // the window's un-resized default size was) instead of filling the real, correctly-
        // resized window — logs Player's actual XAML-layout size whenever it changes, to
        // tell apart "the layout pass never caught up with the resize" from "the video
        // surface itself is the wrong size for some other reason".
        Player.SizeChanged += (_, args) =>
            AppLog.Write($"  Player.SizeChanged: {args.PreviousSize.Width}x{args.PreviousSize.Height} -> {args.NewSize.Width}x{args.NewSize.Height}");

        // NOT setting Width/Height here: confirmed against the real docs (MS
        // Learn, VideoEncodingProperties class remarks) that "properties that
        // are manually set are ignored" for an instance returned by a preset
        // factory like CreateH264() — width/height end up decided by the
        // decoder from the real SPS in the stream regardless, the same way a
        // real elementary-stream consumer (FFmpegInteropX's H264 system-decoder
        // path, checked before writing this) also never sets them here. The
        // window itself is still sized correctly, from H264Sps — see ResizeToVideo.
        VideoEncodingProperties videoProps = VideoEncodingProperties.CreateH264();
        // Tried AVCC (length-prefixed NALs) + SetFormatUserData here instead of Annex-B —
        // made things categorically worse (MediaOpened never even fired; before, it always
        // did). CreateH264()'s preset is tied to Annex-B regardless of SetFormatUserData —
        // confirmed live, not guessed twice. Back to Annex-B: start codes, SPS/PPS as
        // in-band samples (see EnqueueSample calls below and in StartPipeline's caller).

        var descriptor = new VideoStreamDescriptor(videoProps);
        var mss = new MediaStreamSource(descriptor)
        {
            BufferTime = TimeSpan.Zero, // real-time mirroring: never intentionally add latency
            IsLive = true,
            // Never set before now — confirmed against a real Microsoft-documented live-
            // streaming MediaStreamSource setup (Q&A: "How to optimize MediaElement for
            // live streaming") that this is required alongside IsLive. Left at its default
            // (true), the pipeline can treat an unboundedly-growing live stream as a finite,
            // seekable timeline — a real candidate for exactly the failure this hit: it
            // renders whatever arrived within its (undocumented, ~3s per that same source —
            // MediaPlayerElement is noted to ignore BufferTime) initial buffering window,
            // then never resumes because its own bookkeeping for "how much of the seekable
            // timeline is buffered" doesn't fit a stream that isn't actually seekable at all.
            CanSeek = false,
        };
        mss.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += OnSampleRequested;
        mss.Closed += (_, args) => AppLog.Write($"  MediaStreamSource.Closed: {args.Request.Reason}");
        _mss = mss;

        var player = new MediaPlayer
        {
            Source = MediaSource.CreateFromMediaStreamSource(mss),
            // Documented specifically for this scenario (live/low-latency streaming, not
            // file playback): "changes the internal update logic to place higher emphasis
            // on video refresh from available samples" — directly relevant to a live freeze
            // where samples kept arriving and being handed over but the displayed frame
            // stopped updating.
            RealTimePlayback = true,
        };
        _player = player;
        player.MediaFailed += (_, args) => AppLog.Write($"  MediaPlayer.MediaFailed: {args.Error} / {args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult:X8})");
        player.MediaOpened += (_, _) => AppLog.Write("  MediaPlayer.MediaOpened (il decoder ha accettato lo stream)");
        player.CurrentStateChanged += (sender, _) => AppLog.Write($"  MediaPlayer.CurrentStateChanged: {sender.CurrentState}");
        // The dimensions the decoder itself actually negotiated from the real SPS —
        // compared against the {width}x{height} H264Sps computed and what ResizeToVideo
        // logged, this is what tells us whether a tiny/garbled render is a decoder-geometry
        // mismatch or something else entirely (corruption inside the frame data itself).
        player.PlaybackSession.NaturalVideoSizeChanged += (session, _) =>
            AppLog.Write($"  NaturalVideoSizeChanged: {session.NaturalVideoWidth}x{session.NaturalVideoHeight} (atteso da SPS: {width}x{height})");
        Player.SetMediaPlayer(player);

        // Confirmed live tonight: a MediaPlayer created by hand and attached
        // via SetMediaPlayer (rather than MediaPlayerElement's own default
        // instance) does NOT autoplay on MediaOpened — CurrentStateChanged sat
        // at Paused for an entire real session, video never once rendered
        // (black window) despite MediaOpened firing with no error. An explicit
        // Play() is required.
        player.Play();

        // The very first samples the pipeline sees: SPS then PPS, each its own Annex-B sample.
        EnqueueSample(sps);
        EnqueueSample(pps);

        Activate();
    }

    private int _nalCount;

    private void OnNalReceived(byte[] nal, bool isIdr)
    {
        // Throttled: thousands of these arrive per session, only the first few
        // are useful to confirm NALs are actually reaching the sample queue.
        if (Interlocked.Increment(ref _nalCount) <= 5)
            AppLog.Write($"  NAL #{_nalCount}: {nal.Length} byte, isIdr={isIdr}");
        EnqueueSample(nal);
    }

    private static readonly byte[] AnnexBStartCode = [0x00, 0x00, 0x00, 0x01];

    private void EnqueueSample(byte[] nal)
    {
        var buf = new byte[AnnexBStartCode.Length + nal.Length];
        AnnexBStartCode.CopyTo(buf, 0);
        nal.CopyTo(buf, AnnexBStartCode.Length);
        _samples.Writer.TryWrite(buf);
    }

    // MediaStreamSource can call SampleRequested again before an earlier call's deferral
    // completes (read-ahead buffering) — without serializing them, two concurrent requests
    // each independently racing to read _samples can complete (and so hand back a decoded
    // sample) out of request order. That silently scrambles which NAL lands in which
    // presentation slot — a real, confirmed live cause of exactly the failure this hit:
    // structurally-correct-but-corrupted (chroma-fringed) video that then froze solid with
    // no error anywhere, matching a scrambled P-frame poisoning the decoder's reference
    // frame, with everything after inheriting the corruption. This gate makes requests
    // serviced strictly in arrival order — only one channel-read-and-assign in flight ever.
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private int _samplesServed;

    // DateTime.UtcNow's real resolution on Windows is ~15ms, not the millisecond precision
    // it prints — a live log showed several consecutive samples (SPS/PPS plus the first
    // couple of NALs, delivered near-instantly since the channel already had them queued)
    // landing on the EXACT SAME timestamp. If the decoder requires strictly increasing PTS,
    // duplicates are a plausible way to silently wedge it — no error, just stops advancing,
    // matching a live freeze (data kept flowing the whole time, confirmed via the counter
    // above; only the displayed frame stopped changing). Guarded by _sampleGate already
    // being held for the whole critical section below, so no extra locking needed here.
    private long _lastTimestampTicks = -1;

    // Real, measured gap since the previous sample was handed over — NOT a flat assumed
    // frame rate. NALs arrive in network bursts (confirmed live: several landing within the
    // same millisecond), so a fixed ~33ms Duration on every one of them makes their SUMMED
    // declared duration run far ahead of how much real wall-clock time actually elapsed —
    // exactly the kind of bookkeeping a buffering engine that tracks "how much have I got
    // queued" by summing Duration would use to (wrongly, early) decide it's buffered far
    // enough ahead and can stop pulling more. A backward-looking, measured duration keeps
    // that sum honest against real time regardless of how bursty delivery actually is.
    private DateTime? _lastSampleDeliveryUtc;

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequestDeferral deferral = args.Request.GetDeferral();
        _ = ServiceSampleRequestAsync(args, deferral);
    }

    private async Task ServiceSampleRequestAsync(MediaStreamSourceSampleRequestedEventArgs args, MediaStreamSourceSampleRequestDeferral deferral)
    {
        await _sampleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] nalWithStartCode = await _samples.Reader.ReadAsync().AsTask().ConfigureAwait(false);
            IBuffer buffer = nalWithStartCode.AsBuffer();

            DateTime nowUtc = DateTime.UtcNow;
            long ticks = (nowUtc - _streamStart).Ticks;
            if (ticks <= _lastTimestampTicks) ticks = _lastTimestampTicks + 1; // strictly increasing, never equal or earlier
            _lastTimestampTicks = ticks;
            TimeSpan timestamp = TimeSpan.FromTicks(ticks);

            // Measured gap since the last sample, clamped to a sane range: near-0 for a
            // burst (several NALs delivered within the same millisecond — don't claim each
            // one occupies a full frame's worth of time, that's the overselling this
            // replaced) and capped for the very first sample or after any real stall
            // (don't let one long gap claim an implausibly huge duration either).
            TimeSpan duration = _lastSampleDeliveryUtc is { } last
                ? nowUtc - last
                : TimeSpan.FromMilliseconds(33); // nothing to measure yet — first sample only
            if (duration < TimeSpan.FromMilliseconds(1)) duration = TimeSpan.FromMilliseconds(1);
            if (duration > TimeSpan.FromMilliseconds(100)) duration = TimeSpan.FromMilliseconds(100);
            _lastSampleDeliveryUtc = nowUtc;

            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);
            sample.Duration = duration;
            args.Request.Sample = sample;

            // Throttled: confirms whether MediaStreamSource keeps pulling samples at all —
            // a live freeze showed zero errors anywhere, so "did it stop asking" vs.
            // "asked but something else stalled" was otherwise impossible to tell apart.
            int served = Interlocked.Increment(ref _samplesServed);
            if (served <= 10 || served % 150 == 0)
                AppLog.Write($"  OnSampleRequested: campione #{served} consegnato ({nalWithStartCode.Length} byte)");
        }
        catch (Exception ex)
        {
            // Channel closed (window disposed) or a transient buffer failure — the pipeline
            // just waits for the next SampleRequested rather than tearing down the whole player.
            // Logged (not just swallowed) — this used to hide the real cause of any failure here.
            AppLog.Write($"  OnSampleRequested fallito: {ex}");
        }
        finally
        {
            _sampleGate.Release();
            deferral.Complete();
        }
    }

    /// <summary>Detaches from the receiver's events and stops feeding the pipeline — call when the mirroring session ends.</summary>
    public void Detach(MirroringDataReceiver receiver)
    {
        receiver.ConfigReceived -= OnConfigReceived;
        receiver.NalReceived -= OnNalReceived;
        receiver.SessionEnded -= OnSessionEnded;
        _samples.Writer.TryComplete();

        // Never disposed before — across many mirroring attempts in the same running app
        // process (exactly what real usage AND every one of tonight's test cycles look
        // like), each left its hardware H.264 decoder session behind for the GC to
        // eventually finalize on its own schedule, not promptly. A very plausible source
        // of the non-determinism actually observed live: an identical setup rendering
        // perfectly once and corrupted-and-frozen the next, with nothing else different.
        try { _player?.Pause(); } catch { /* already gone, or never got far enough to matter */ }
        try { _player?.Dispose(); } catch { }
        _player = null;
        _mss = null;
    }
}
