using System.Runtime.InteropServices.WindowsRuntime;
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
/// handed to the pipeline.
/// </summary>
public sealed partial class MirrorWindow : Window
{
    private static readonly byte[] AnnexBStartCode = [0x00, 0x00, 0x00, 0x01];

    private readonly Channel<byte[]> _samples = Channel.CreateUnbounded<byte[]>();
    private readonly DateTime _streamStart = DateTime.UtcNow;
    private MediaStreamSource? _mss;
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
    }

    private void OnConfigReceived(byte[] sps, byte[] pps)
    {
        if (_configured) return; // one MediaStreamSource per window/session — a re-SETUP mid-session isn't handled here
        _configured = true;

        if (!H264Sps.TryParseDimensions(sps, out int width, out int height))
        {
            // Common-case parser (see H264Sps's doc comment) didn't recognize this SPS shape —
            // fall back to a size that still lets the pipeline negotiate the real one from the stream itself.
            width = 1920;
            height = 1080;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ResizeToVideo(width, height);
            StartPipeline(width, height, sps, pps);
        });
    }

    private void ResizeToVideo(int width, int height)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            // AppWindow.Resize wants physical pixels including the non-client
            // frame; a few extra px of border/titlebar is harmless here.
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch { /* non-fatal — window just keeps its default size */ }
    }

    private void StartPipeline(int width, int height, byte[] sps, byte[] pps)
    {
        VideoEncodingProperties videoProps = VideoEncodingProperties.CreateH264();
        videoProps.Width = (uint)width;
        videoProps.Height = (uint)height;

        var descriptor = new VideoStreamDescriptor(videoProps);
        var mss = new MediaStreamSource(descriptor)
        {
            BufferTime = TimeSpan.Zero, // real-time mirroring: never intentionally add latency
            IsLive = true,
        };
        mss.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += OnSampleRequested;
        _mss = mss;

        Player.SetMediaPlayer(new MediaPlayer { Source = MediaSource.CreateFromMediaStreamSource(mss) });

        // The very first samples the pipeline sees: SPS then PPS, each its own Annex-B sample.
        EnqueueSample(sps);
        EnqueueSample(pps);

        Activate();
    }

    private void OnNalReceived(byte[] nal, bool isIdr) => EnqueueSample(nal);

    private void EnqueueSample(byte[] nal)
    {
        var buf = new byte[AnnexBStartCode.Length + nal.Length];
        AnnexBStartCode.CopyTo(buf, 0);
        nal.CopyTo(buf, AnnexBStartCode.Length);
        _samples.Writer.TryWrite(buf);
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequestDeferral deferral = args.Request.GetDeferral();
        _ = Task.Run(async () =>
        {
            try
            {
                byte[] nalWithStartCode = await _samples.Reader.ReadAsync().AsTask().ConfigureAwait(false);
                IBuffer buffer = nalWithStartCode.AsBuffer();
                TimeSpan timestamp = DateTime.UtcNow - _streamStart;
                args.Request.Sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);
            }
            catch (Exception)
            {
                // Channel closed (window disposed) or a transient buffer failure — the pipeline
                // just waits for the next SampleRequested rather than tearing down the whole player.
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>Detaches from the receiver's events and stops feeding the pipeline — call when the mirroring session ends.</summary>
    public void Detach(MirroringDataReceiver receiver)
    {
        receiver.ConfigReceived -= OnConfigReceived;
        receiver.NalReceived -= OnNalReceived;
        _samples.Writer.TryComplete();
    }
}
