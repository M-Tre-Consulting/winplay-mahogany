using System.Linq;
using System.Threading;
using AirPlaySender.Core.Receiving;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using WinRT.Interop;

namespace AirPlaySender.App;

/// <summary>
/// The window that shows the mirrored iPhone screen. It subscribes to one
/// <see cref="MirroringDataReceiver"/> and runs its own decode pipeline:
/// each Annex-B access unit off the queue is fed straight to the Windows
/// H.264 decoder MFT (<see cref="H264Mft"/>), and every decoded frame is
/// blitted onto a Win2D <see cref="CanvasSwapChainPanel"/>.
///
/// This is the third rendering approach for the same bug ("perfect image
/// for ~1s, then frozen solid"). The first (MediaPlayerElement) and second
/// (MediaPlayer frame-server mode + Win2D) both wedged the same way, with
/// the data pipeline provably healthy the whole time — <see cref="H264Mft"/>
/// removes MediaPlayer / MediaStreamSource entirely, so there is no hidden
/// clock or buffering heuristic left to stall. See README, "La caccia al
/// bug del rendering".
///
/// A live iPhone mirror stream sends exactly one IDR, at the start, then
/// only P-frames indefinitely — so frames are delivered whole and in order
/// and NEVER dropped (one gap makes everything after it undecodable until
/// the phone volunteers another IDR, which may be a long way off). A hard
/// cap only exists to notice a genuinely wedged pipeline and end the
/// session so the user can start a clean one.
/// </summary>
public sealed partial class MirrorWindow : Window
{
    private sealed class QueuedSample(byte[] annexB, TimeSpan pts, bool isKeyFrame)
    {
        public byte[] AnnexB { get; } = annexB;
        public TimeSpan Pts { get; } = pts;
        public bool IsKeyFrame { get; } = isKeyFrame;
    }

    private readonly object _queueGate = new();
    private readonly Queue<QueuedSample> _queue = new();
    // Counts frames sitting in _queue (one Release per enqueue, one Wait per dequeue).
    private readonly SemaphoreSlim _framesAvailable = new(0);
    // Cancelled when the window/session is finished — stops the decode thread.
    private readonly CancellationTokenSource _closed = new();

    private const int HardCapFrames = 900; // ~15s at 60fps
    private bool _forcedClose;

    // PTS timeline state (guarded by _queueGate; touched from the receiver thread).
    private ulong? _firstTimestampNs;
    private long _lastPtsTicks = -1;

    private bool _configured;
    private int _videoWidth = 1920, _videoHeight = 1080;
    private volatile bool _ended;

    // ---- F11: true full screen (no title bar/borders) --------------------------------
    private bool _isFullScreen;

    // ---- decode + render ------------------------------------------------------------
    private H264Mft? _decoder;
    private Thread? _decodeThread;
    private int _framesIn;
    private int _framesDecoded;
    private long _framesShown;
    private DateTime _lastDecodedUtc;
    private DateTime _lastFrameShownUtc;

    // Win2D surface. Device is created lazily (first CanvasDevice.GetSharedDevice() can
    // take a few hundred ms) and pre-warmed at startup (App.xaml.cs). _renderGate guards
    // the swap chain / render target against the (cold) resize path swapping them out
    // from the UI thread while the decode thread is mid-present.
    private readonly object _renderGate = new();
    private CanvasDevice? _canvasDevice;
    private Viewbox? _swapViewbox;
    private CanvasSwapChainPanel? _swapPanel;
    private CanvasSwapChain? _swapChain;
    private CanvasRenderTarget? _renderTarget; // persistent blit target — SetPixelBytes each frame, no per-frame GPU alloc
    private int _surfaceWidth, _surfaceHeight;

    public MirrorWindow()
    {
        InitializeComponent();
        Title = "WinPlay Mahogany — Mirroring";
        SetWindowIcon();
        RootGrid.KeyDown += RootGrid_KeyDown;
    }

    /// <summary>
    /// F11 toggles a true full-screen presenter (no title bar, no borders,
    /// covers the whole monitor) — requested so mirroring a wide/oddly-shaped
    /// source (e.g. a Mac's own screen) isn't cropped by window chrome the way
    /// it would be under a maximized-but-still-bordered window. The video
    /// already scales correctly to whatever space it's given (see the Viewbox
    /// in <see cref="EnsureSwapChain"/>), so this only needs to change how
    /// much space that is.
    /// </summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            _isFullScreen = !_isFullScreen;
            appWindow.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            AppLog.Write($"  MirrorWindow: schermo intero {(_isFullScreen ? "attivato" : "disattivato")} (F11)");
        }
        catch (Exception ex) { AppLog.Write($"  ToggleFullScreen fallito: {ex}"); }
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

    /// <summary>Attaches this window to one mirroring session. The receiver replays any config/frames that already arrived before this hop ran.</summary>
    public void Attach(MirroringDataReceiver receiver)
    {
        receiver.AttachRenderer(OnConfigReceived, OnFrameReceived, OnSessionEnded);
        AppLog.Write("MirrorWindow.Attach: agganciato (con replay di config/frame già arrivati)");
    }

    /// <summary>Detaches from the receiver and tears down the pipeline — call when the mirroring session ends.</summary>
    public void Detach(MirroringDataReceiver receiver)
    {
        receiver.DetachRenderer(OnConfigReceived, OnFrameReceived, OnSessionEnded);
        _ended = true;
        try { _closed.Cancel(); } catch { }
        TearDownDecoder();
        lock (_renderGate)
        {
            try { _swapChain?.Dispose(); } catch { }
            try { _renderTarget?.Dispose(); } catch { }
            _swapChain = null;
            _renderTarget = null;
        }
    }

    /// <summary>
    /// True once this window is closing because <see cref="OnSessionEnded"/> fired, as
    /// opposed to the user closing it. App.xaml.cs checks this before calling
    /// <see cref="MirroringDataReceiver.RequestSessionClose"/> on <see cref="Closed"/> —
    /// without it, the proactive-offer window closing itself would take the shared RTSP
    /// connection (and the real, just-started session) down with it.
    /// </summary>
    private bool _closingBecauseSessionEnded;

    private void OnSessionEnded()
    {
        AppLog.Write("MirrorWindow: sessione di mirroring terminata, chiudo la finestra");
        _ended = true;
        try { _closed.Cancel(); } catch { }
        DispatcherQueue.TryEnqueue(() =>
        {
            _closingBecauseSessionEnded = true;
            Close();
        });
    }

    public bool ShouldRequestSessionClose => !_closingBecauseSessionEnded;

    // ---- video config (SPS/PPS): size the window, start the pipeline once ------------

    private void OnConfigReceived(byte[] sps, byte[] pps)
    {
        AppLog.Write($"MirrorWindow.OnConfigReceived: SPS {sps.Length} byte, PPS {pps.Length} byte");

        if (_configured)
        {
            // A later config packet — the iPhone re-sends SPS/PPS whenever the screen is
            // rotated or the resolution changes. Nothing to do here: the decode loop
            // rebuilds its H.264 decoder off the next IDR's in-band SPS, and OnDecodedFrame
            // re-fits the window + render surface to whatever size actually comes out.
            if (H264Sps.TryParseDimensions(sps, out int cw, out int ch))
                AppLog.Write($"  Config aggiornata a sessione avviata: {cw}x{ch} (rotazione / cambio risoluzione) — se ne occupa la pipeline di decodifica");
            return;
        }

        if (H264Sps.TryParseDimensions(sps, out int width, out int height))
        {
            AppLog.Write($"  Dimensioni vere lette dalla SPS: {width}x{height}");
        }
        else
        {
            AppLog.Write("  H264Sps non ha riconosciuto questa SPS — fallback 1920x1080 (il decoder negozierà la dimensione vera)");
            width = 1920; height = 1080;
        }

        _configured = true;
        _videoWidth = width;
        _videoHeight = height;

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ResizeToVideo(width, height);
                (Content as FrameworkElement)?.UpdateLayout();
                EnsureSwapChain(width, height);
                StartDecodePipeline(width, height);
                Activate();
                // Needed for RootGrid_KeyDown (F11) to ever fire — a plain Grid
                // has no keyboard focus by default, even with IsTabStop set.
                RootGrid.Focus(FocusState.Programmatic);
                AppLog.Write("  Pipeline di decodifica avviata, finestra attivata");
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

            // Don't fight a window the user has maximized or put full screen (F11): the
            // Viewbox already rescales the video to whatever space it has, and the
            // swap-chain / decoder resize is handled separately. Only auto-size a normal
            // restored window — this matters on a mid-session rotation, where ResizeToVideo
            // now runs again with the new orientation's dimensions.
            if (_isFullScreen || appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            {
                AppLog.Write($"  ResizeToVideo({width}x{height}) saltato (finestra massimizzata / schermo intero) — ci pensa il Viewbox");
                return;
            }

            // AppWindow.Resize sets the OUTER window size (title bar + borders included),
            // not the client area — measure the non-client overhead once, then correct.
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            int deltaW = width - appWindow.ClientSize.Width;
            int deltaH = height - appWindow.ClientSize.Height;
            if (deltaW != 0 || deltaH != 0)
                appWindow.Resize(new Windows.Graphics.SizeInt32(width + deltaW, height + deltaH));

            AppLog.Write($"  ResizeToVideo({width}x{height}) -> ClientSize effettivo: {appWindow.ClientSize.Width}x{appWindow.ClientSize.Height}");
        }
        catch (Exception ex) { AppLog.Write($"  ResizeToVideo fallito: {ex}"); }
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (width <= 0 || height <= 0) { width = 1920; height = 1080; }

        if (_swapPanel is null)
        {
            // Fixed size (native video resolution) — a CanvasSwapChainPanel
            // doesn't scale its swap chain's pixels to fill a differently-sized
            // panel, it composites them at native size anchored top-left. Sat
            // directly in RootGrid with Center/Center that gave the right look
            // ONLY while the window was >= native size (letterbox/pillarbox
            // around a 1:1 video) — shrinking the window below native size
            // just clipped the panel against the window edge instead of
            // scaling it down, cutting off part of the picture (found live,
            // reported by the user: "riduco le proporzioni e taglia parte
            // dello schermo"). Fix: wrap the fixed-size panel in a Viewbox
            // (Stretch="Uniform") instead of parenting it to RootGrid
            // directly — the Viewbox scales the whole panel as a unit to fit
            // whatever space is available, in both directions, always
            // preserving the video's real aspect ratio; the swap chain itself
            // keeps rendering at true native pixel resolution regardless (no
            // resize of the actual surface on every window drag), only the
            // on-screen presentation scales. RootGrid stays Stretch and black,
            // still giving the letterbox/pillarbox behind it.
            _swapPanel = new CanvasSwapChainPanel();
            _swapViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
                Child = _swapPanel,
            };
            RootGrid.Children.Add(_swapViewbox);
        }

        if (_swapChain is not null && _surfaceWidth == width && _surfaceHeight == height) return;

        _swapPanel.Width = width;
        _swapPanel.Height = height;

        CanvasDevice device = _canvasDevice ??= CanvasDevice.GetSharedDevice();
        lock (_renderGate)
        {
            CanvasSwapChain? oldChain = _swapChain;
            CanvasRenderTarget? oldTarget = _renderTarget;
            _swapChain = new CanvasSwapChain(device, width, height, 96f,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Ignore);
            _renderTarget = new CanvasRenderTarget(device, width, height, 96f);
            _surfaceWidth = width;
            _surfaceHeight = height;
            _swapPanel.SwapChain = _swapChain;
            try { oldChain?.Dispose(); } catch { }
            try { oldTarget?.Dispose(); } catch { }
        }
    }

    // ---- decode thread -------------------------------------------------------------

    private void StartDecodePipeline(int width, int height)
    {
        _lastDecodedUtc = DateTime.UtcNow;
        _lastFrameShownUtc = DateTime.UtcNow;
        // Media Foundation MFTs want one dedicated MTA thread — create + drive the
        // decoder entirely on it, with a blocking wait (no async continuation hopping
        // threadpool threads).
        _decodeThread = new Thread(() => DecodeLoop(width, height))
        {
            IsBackground = true,
            Name = "H264Decode",
        };
        _decodeThread.SetApartmentState(ApartmentState.MTA);
        _decodeThread.Start();
        StartWatchdog();
    }

    private void DecodeLoop(int width, int height)
    {
        H264Mft decoder;
        try
        {
            decoder = new H264Mft(width, height);
            decoder.FrameDecoded += OnDecodedFrame;
            _decoder = decoder;
            AppLog.Write($"  H264Mft pronto: coded->display {decoder.DisplayW}x{decoder.DisplayH}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"  H264Mft non creato: {ex}");
            return;
        }

        int curW = width, curH = height;
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                try { _framesAvailable.Wait(_closed.Token); }
                catch (OperationCanceledException) { break; }

                QueuedSample s;
                lock (_queueGate)
                {
                    if (_queue.Count == 0) continue;
                    s = _queue.Dequeue();
                }

                // The iPhone re-encodes at a new resolution whenever the screen is
                // rotated (portrait <-> landscape) or the resolution otherwise changes,
                // and marks the switch with a fresh IDR carrying its own SPS in-band.
                // Feeding those frames to a decoder still set up for the old orientation
                // is what shredded the picture. Rebuild the MFT from scratch for the new
                // size — cleaner and more reliable than riding a mid-stream
                // MF_E_TRANSFORM_STREAM_CHANGE across a 90° flip.
                if (s.IsKeyFrame
                    && TryReadKeyFrameDimensions(s.AnnexB, out int kfW, out int kfH)
                    && (kfW != curW || kfH != curH))
                {
                    AppLog.Write($"  Risoluzione stream cambiata {curW}x{curH} -> {kfW}x{kfH}: ricreo il decoder H.264");
                    try
                    {
                        decoder.FrameDecoded -= OnDecodedFrame;
                        decoder.Dispose();
                        decoder = new H264Mft(kfW, kfH);
                        decoder.FrameDecoded += OnDecodedFrame;
                        _decoder = decoder;
                        curW = kfW; curH = kfH;
                        AppLog.Write($"  Decoder H.264 ricreato: coded->display {decoder.DisplayW}x{decoder.DisplayH}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"  Ricreazione decoder fallita: {ex}");
                    }
                }

                try { decoder.Decode(s.AnnexB, s.Pts.Ticks); }
                catch (Exception ex) { AppLog.Write($"  decode fallito: {ex.Message}"); }
            }
        }
        finally
        {
            try { decoder.Dispose(); } catch { }
            _decoder = null;
        }
    }

    /// <summary>
    /// Finds the first SPS NAL (type 7) in an Annex-B access unit and reads its picture
    /// size. A mirroring key frame always carries SPS/PPS in-band (prepended by the
    /// receiver if the phone didn't), so this is how the decode loop notices a rotation
    /// without depending on the separate config packet arriving.
    /// </summary>
    private static bool TryReadKeyFrameDimensions(byte[] annexB, out int width, out int height)
    {
        width = 0;
        height = 0;
        int n = annexB.Length;
        int nalStart = -1;
        int p = 0;
        while (p + 2 < n)
        {
            if (annexB[p] == 0 && annexB[p + 1] == 0 && annexB[p + 2] == 1)
            {
                if (nalStart >= 0 && TrySpsSlice(annexB, nalStart, p, out width, out height)) return true;
                nalStart = p + 3;
                p = nalStart;
            }
            else
            {
                p++;
            }
        }
        return nalStart >= 0 && nalStart < n && TrySpsSlice(annexB, nalStart, n, out width, out height);

        static bool TrySpsSlice(byte[] buf, int start, int endExclusive, out int w, out int h)
        {
            w = 0;
            h = 0;
            if (start >= endExclusive) return false;
            if ((buf[start] & 0x1F) != 7) return false; // not an SPS NAL
            return H264Sps.TryParseDimensions(buf.AsSpan(start, endExclusive - start), out w, out h);
        }
    }

    /// <summary>Called from the decode thread, once per decoded frame — blit BGRA onto the swap chain.</summary>
    private void OnDecodedFrame(byte[] bgra, int w, int h, long ptsHns)
    {
        _lastDecodedUtc = DateTime.UtcNow;
        int decoded = Interlocked.Increment(ref _framesDecoded);
        if (_ended) return;

        try
        {
            lock (_renderGate)
            {
                CanvasSwapChain? chain = _swapChain;
                CanvasRenderTarget? target = _renderTarget;
                if (chain is null || target is null) return;

                if ((int)target.SizeInPixels.Width != w || (int)target.SizeInPixels.Height != h)
                {
                    // The decoder's frame size no longer matches the surface — this is
                    // also the path a rotation takes. Re-fit the WINDOW to the new aspect
                    // (not just the swap chain), then resize the surface, on the UI thread;
                    // skip this one frame.
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (w != _videoWidth || h != _videoHeight)
                            {
                                AppLog.Write($"  Output decoder {_videoWidth}x{_videoHeight} -> {w}x{h}: riadatto finestra e superficie");
                                _videoWidth = w;
                                _videoHeight = h;
                                ResizeToVideo(w, h);
                                (Content as FrameworkElement)?.UpdateLayout();
                            }
                            EnsureSwapChain(w, h);
                        }
                        catch (Exception ex) { AppLog.Write($"  resize surface fallito: {ex}"); }
                    });
                    return;
                }

                target.SetPixelBytes(bgra);
                using (CanvasDrawingSession ds = chain.CreateDrawingSession(Microsoft.UI.Colors.Black))
                    ds.DrawImage(target);
                chain.Present(0);
            }

            _lastFrameShownUtc = DateTime.UtcNow;
            long shown = Interlocked.Increment(ref _framesShown);
            if (shown <= 12 || shown % 600 == 0)
                AppLog.Write($"  frame mostrato #{shown} (decodificati {decoded}, {w}x{h}, pts {ptsHns / 10_000}ms)");
        }
        catch (Exception ex)
        {
            AppLog.Write($"  blit fallito: {ex}");
        }
    }

    private void TearDownDecoder()
    {
        try { _watchdog?.Stop(); } catch { }
        try { _decodeThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _decodeThread = null;
    }

    // ---- frame intake (receiver thread) ------------------------------------------

    private void OnFrameReceived(MirroringVideoFrame frame)
    {
        if (_ended) return;

        bool overCap;
        lock (_queueGate)
        {
            _firstTimestampNs ??= frame.TimestampNs;
            ulong deltaNs = frame.TimestampNs >= _firstTimestampNs.Value ? frame.TimestampNs - _firstTimestampNs.Value : 0;
            long ptsTicks = (long)(deltaNs / 100UL); // 100ns per tick

            // Guard a non-monotonic / glitched encoder timestamp.
            if (ptsTicks <= _lastPtsTicks || (_lastPtsTicks >= 0 && ptsTicks - _lastPtsTicks > 20_000_000))
                ptsTicks = _lastPtsTicks < 0 ? 0 : _lastPtsTicks + 166_667;
            long prevTicks = _lastPtsTicks;
            _lastPtsTicks = ptsTicks;

            _queue.Enqueue(new QueuedSample(frame.AnnexB, TimeSpan.FromTicks(ptsTicks), frame.IsKeyFrame));
            _framesAvailable.Release();

            int n = ++_framesIn;
            if (n <= 12)
                AppLog.Write($"  frame #{n} in ingresso: +{(prevTicks < 0 ? 0 : (ptsTicks - prevTicks) / 10_000.0):F1}ms, {frame.AnnexB.Length}B, key={frame.IsKeyFrame}");

            overCap = _queue.Count > HardCapFrames;
        }

        if (overCap && !_forcedClose)
        {
            _forcedClose = true;
            _ended = true;
            try { _closed.Cancel(); } catch { }
            AppLog.Write($"  coda oltre il limite ({HardCapFrames}) — la decodifica non tiene il passo e questo stream non ha altri IDR: chiudo la sessione");
            DispatcherQueue.TryEnqueue(Close);
        }
    }

    private int QueueCount() { lock (_queueGate) return _queue.Count; }

    // ---- watchdog: diagnostics --------------------------------------------------

    private DispatcherQueueTimer? _watchdog;

    private void StartWatchdog()
    {
        _watchdog?.Stop();
        _watchdog = DispatcherQueue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(1);
        _watchdog.Tick += Watchdog_Tick;
        _watchdog.Start();
    }

    private void Watchdog_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_ended) { sender.Stop(); return; }
        double sinceDecoded = (DateTime.UtcNow - _lastDecodedUtc).TotalSeconds;
        double sinceShown = (DateTime.UtcNow - _lastFrameShownUtc).TotalSeconds;
        AppLog.Write($"  [watchdog] in={_framesIn} queued={QueueCount()} decoded={_framesDecoded} shown={_framesShown} ultimaDecodifica={sinceDecoded:F1}s ultimoFrame={sinceShown:F1}s");
    }
}
