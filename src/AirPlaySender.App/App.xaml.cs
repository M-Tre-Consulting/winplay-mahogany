using AirPlaySender.Core.Receiving;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace AirPlaySender.App;

public partial class App : Application
{
    private const int MirroringPort = 7000;

    private Window? _window;

    // Owned here, not by MainWindow: these must keep running (mDNS
    // advertising + the RTSP receiver) for the whole process lifetime,
    // independent of whether any window is visible — that's the point of
    // "stays in the background, only closable from the tray icon".
    private AirPlayMirroringAdvertiser? _advertiser;
    private AirPlayReceiverServer? _receiverServer;
    private readonly List<MirrorWindow> _mirrorWindows = [];

    public App()
    {
        InitializeComponent();

        // A hard crash in a render window used to vanish with no trace in the log.
        // Catch every last-chance path and write it before the process (or window) dies.
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"!!! UnhandledException (XAML): {e.Message}\n{e.Exception}");
            // leave e.Handled=false — don't mask a real crash, just record it
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write($"!!! UnhandledException (AppDomain, terminating={e.IsTerminating}): {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"!!! UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // TEMPORARY, throwaway: live-verifies ScreenCapture actually captures
        // real desktop content, since it can't be checked against an oracle
        // encoder are wired into the real mirroring-sender flow.
        {
            return;
        }
        {
            return;
        }
        {
            string[] cliArgs = Environment.GetCommandLineArgs();
            if (mirrorTestIdx >= 0 && mirrorTestIdx + 1 < cliArgs.Length)
            {
                string nameFilter = cliArgs[mirrorTestIdx + 1];
                    ? cliArgs[mirrorTestIdx + 2] : null;
                return;
            }
        }

        StartupRegistration.EnsureRegistered();

        _window = new MainWindow();
        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized");
        if (startMinimized)
        {
            _window.Activate(); // still needed once to fully initialize the window/dispatcher
            (_window as MainWindow)?.HideToTray();
        }
        else
        {
            _window.Activate();
        }

        StartMirroringReceiver();

        // First CanvasDevice.GetSharedDevice() (Win2D/D3D init) can take a few hundred ms.
        // Warm it now on a background thread so it's ready before a mirroring session
        // starts — otherwise that cost lands on the UI thread mid-connection and the
        // render window's data receiver races ahead of it (confirmed live: window never
        // appeared, config+IDR fired before it was listening).
        _ = Task.Run(() => { try { CanvasDevice.GetSharedDevice(); } catch { } });
    }

    /// <summary>
    /// Starts advertising this PC as an AirPlay Mirroring target and the
    /// RTSP server that actually receives it — independent of
    /// <see cref="MainWindow"/>'s own AirPlay-sender feature (Phase 1),
    /// which stays exactly as it was. Best-effort: a failure here (e.g. the
    /// mirroring port already in use) is logged, not fatal to the rest of
    /// the app.
    /// </summary>
    private void StartMirroringReceiver()
    {
        try
        {
            ReceiverIdentity identity = ReceiverIdentity.LoadOrCreate();
            string deviceId = AirPlaySender.Core.Net.LocalMachineInfo.MacAddressOrPlaceholder();

            _advertiser = new AirPlayMirroringAdvertiser(MirroringPort, identity);
            _advertiser.Start();

            _receiverServer = new AirPlayReceiverServer(MirroringPort, identity, deviceId);
            // Forwards every RTSP-dispatch trace AND (already re-forwarded from
            // inside AirPlayReceiverServer's own SETUP handlers) every
            // MirroringDataReceiver.Diagnostics — the one place to see what a
            // live session actually did, now that this runs as a background
            // app with no attached console.
            _receiverServer.Diagnostics += AppLog.Write;
            _receiverServer.MirroringSessionStarted += OnMirroringSessionStarted;
            _receiverServer.MirrorAudioSessionStarted += OnMirrorAudioSessionStarted;
            _receiverServer.Start();
            AppLog.Write($"Ricevitore di mirroring avviato sulla porta {MirroringPort}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Ricevitore di mirroring non avviato: {ex}");
        }
    }

    /// <summary>Opens a new render window for each mirroring session — called on <see cref="AirPlayReceiverServer"/>'s own background accept loop, so hop back to the UI thread before touching any window.</summary>
    private void OnMirroringSessionStarted(MirroringDataReceiver receiver)
    {
        AppLog.Write("MirroringSessionStarted: apro una MirrorWindow");
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var mirrorWindow = new MirrorWindow();
                mirrorWindow.Attach(receiver);
                lock (_mirrorWindows) _mirrorWindows.Add(mirrorWindow);
                mirrorWindow.Closed += (_, _) =>
                {
                    bool userClose = mirrorWindow.ShouldRequestSessionClose;
                    AppLog.Write($"MirrorWindow.Closed (chiusura utente col pulsante X: {userClose})");
                    mirrorWindow.Detach(receiver);
                    lock (_mirrorWindows) _mirrorWindows.Remove(mirrorWindow);
                    // Only for a real, user-initiated close (the X button) — NOT when this
                    // window auto-closed because its own SessionEnded already fired. Getting
                    // this backwards took the real session down as collateral damage of the
                    // proactive-offer receiver's own routine cleanup (see MirrorWindow's doc
                    // comment on ShouldRequestSessionClose) — confirmed live, fixed here.
                    if (userClose)
                    {
                        AppLog.Write("  -> chiudo la sessione di mirroring sul telefono");
                        receiver.RequestSessionClose();
                    }
                };
                AppLog.Write("MirrorWindow creata e agganciata");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Creazione di MirrorWindow fallita: {ex}");
            }
        });
    }

    /// <summary>
    /// The client set up the mirroring audio stream: stand up an AAC-ELD decoder and an
    /// AudioGraph output, and pipe every decrypted frame through them. No window — audio
    /// just plays alongside whatever MirrorWindow is showing.
    /// </summary>
    private void OnMirrorAudioSessionStarted(MirrorAudioReceiver receiver)
    {
        AppLog.Write("MirrorAudioSessionStarted: avvio decoder AAC-ELD + AudioGraph");
        _ = Task.Run(async () =>
        {
            AacEldDecoder? decoder = null;
            MirrorAudioPlayer? player = null;
            try
            {
                decoder = new AacEldDecoder();
                player = new MirrorAudioPlayer(decoder.SampleRate, decoder.Channels) { Diagnostics = AppLog.Write };
                await player.StartAsync();

                int decoded = 0;
                void OnFrame(byte[] aac)
                {
                    try
                    {
                        short[]? pcm = decoder!.Decode(aac);
                        if (pcm is null) return;
                        player!.Enqueue(pcm);
                        if (++decoded is <= 5 or 500 || decoded % 2000 == 0)
                            AppLog.Write($"  audio: frame #{decoded} decodificato ({pcm.Length} campioni, {decoder.SampleRate}Hz {decoder.Channels}ch)");
                    }
                    catch (Exception ex) { AppLog.Write($"  audio decode/enqueue fallito: {ex.Message}"); }
                }

                AacEldDecoder d = decoder;
                MirrorAudioPlayer p = player;
                void OnVolume(double gain) { try { p.SetGain(gain); } catch { } }
                receiver.AudioFrameReceived += OnFrame;
                receiver.VolumeGainChanged += OnVolume;
                p.SetGain(receiver.VolumeGain); // whatever the client already set
                receiver.SessionEnded += () =>
                {
                    AppLog.Write("MirrorAudioReceiver: sessione audio terminata");
                    receiver.AudioFrameReceived -= OnFrame;
                    receiver.VolumeGainChanged -= OnVolume;
                    _ = p.DisposeAsync();
                    d.Dispose();
                };
                AppLog.Write("Audio mirroring pronto");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Avvio audio mirroring fallito: {ex}");
                if (player is not null) await player.DisposeAsync();
                decoder?.Dispose();
            }
        });
    }
}
