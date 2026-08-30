using System.Collections.ObjectModel;
using AirPlaySender.Core;
using AirPlaySender.Core.Discovery;
using AirPlaySender.Core.Pairing;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace AirPlaySender.App;

public sealed partial class MainWindow : Window
{
    private readonly AirPlayDiscovery _discovery = new();
    private readonly CredentialStore _credentials = new();

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    private AirPlaySession? _activeSession;
    private DeviceItem? _activeItem;

    // Set only by ExitApplication (the tray menu's "Esci"), never by the
    // window's own X button — that's the whole point of "runs in the
    // background, closable only from the tray icon".
    private bool _reallyExiting;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinPlay Mahogany";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        SystemBackdrop = new MicaBackdrop();
        SetWindowIcon();
        SetupTrayIcon();
        Closed += OnWindowClosed;

        _ = ScanAsync();
    }

    /// <summary>
    /// The window's X button/Alt+F4 hides instead of exiting — the mirroring
    /// receiver (advertising + RTSP server, owned by <see cref="App"/>) has to
    /// keep running so "PC-NICO" stays discoverable while the app is in the
    /// background. Only <see cref="ExitApplication"/> (the tray menu's "Esci")
    /// actually ends the process.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_reallyExiting) return;
        args.Handled = true;
        HideToTray();
    }

    /// <summary>Hides the window without closing it — the tray icon (and the mirroring receiver behind it) keeps running.</summary>
    public void HideToTray() => GetAppWindow().Hide();

    private void ShowMainWindow()
    {
        AppWindow appWindow = GetAppWindow();
        appWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        // Application.Exit() (not just Close()) so it also tears down any open
        // MirrorWindow instances, not just this one — Close() would only ever
        // end the process if this happened to be the last open window.
        _reallyExiting = true;
        Application.Current.Exit();
    }

    private AppWindow GetAppWindow()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    // H.NotifyIcon.WinUI's default ContextMenuMode (PopupMenu) renders the
    // menu as a native win32 popup built from each MenuFlyoutItem's Command —
    // it never raises the item's Click event — so this has to be Command-based,
    // which is easiest to get right in code rather than via XAML/x:Bind.
    private void SetupTrayIcon()
    {
        var openItem = new MenuFlyoutItem { Text = "Apri", Command = new RelayCommand(ShowMainWindow) };
        var exitItem = new MenuFlyoutItem { Text = "Esci", Command = new RelayCommand(ExitApplication) };
        TrayIcon.ContextFlyout = new MenuFlyout { Items = { openItem, exitItem } };
        TrayIcon.LeftClickCommand = new RelayCommand(ShowMainWindow);

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath)) TrayIcon.Icon = new System.Drawing.Icon(iconPath);
        }
        catch { /* cosmetic only — the tray icon still works with the library's default */ }
    }

    /// <summary>
    /// Minimal parameterless ICommand — this app has no MVVM/command infrastructure
    /// elsewhere, and doesn't need one just for two tray menu items. Fully-qualified
    /// (rather than a `using System.Windows.Input;`) because that namespace's own
    /// InputScope/InputScopeName/InputScopeNameValue would otherwise collide with the
    /// WinUI ones this file already uses unqualified in HandlePinRequiredAsync.
    /// </summary>
    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    // App is unpackaged (WindowsPackageType=None), so unlike a packaged WinUI 3
    // app the titlebar/taskbar icon isn't picked up automatically from the .exe's
    // embedded resource (ApplicationIcon in the csproj) — it has to be set on the
    // AppWindow explicitly, from the .ico copied next to the .exe at build time.
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
        catch { /* cosmetic only — never worth failing startup over */ }
    }

    private async void OnRescanClicked(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        RescanButton.IsEnabled = false;
        ScanningRing.IsActive = true;
        EmptyStateText.Visibility = Visibility.Collapsed;
        try
        {
            IReadOnlyList<AirPlayDevice> found = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(4));

            // Preserve the connected item's live UI state; refresh everything else.
            string? connectedId = _activeItem?.Device.DeviceId;
            Devices.Clear();
            foreach (AirPlayDevice d in found)
            {
                var item = new DeviceItem(d);
                if (d.DeviceId == connectedId && _activeItem is not null)
                {
                    item.IsConnected = _activeItem.IsConnected;
                    item.StatusText = _activeItem.StatusText;
                    _activeItem = item;
                }
                Devices.Add(item);
            }
            EmptyStateText.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyStateText.Text = $"La ricerca dei dispositivi AirPlay non è riuscita: {ex.Message}";
            EmptyStateText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanningRing.IsActive = false;
            RescanButton.IsEnabled = true;
        }
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DeviceItem item) return;

        if (_activeSession is not null) await DisconnectActiveAsync();

        _activeItem = item;
        item.IsBusy = true;
        item.StatusText = "Connessione…";

        var session = new AirPlaySession(_credentials);
        _activeSession = session;
        session.PinRequired += deviceName => DispatcherQueue.TryEnqueue(async () => await HandlePinRequiredAsync(session, item, deviceName));
        session.Disconnected += () => DispatcherQueue.TryEnqueue(() => HandleDisconnected(item));

        try
        {
            await session.ConnectAsync(item.Device);
            item.IsConnected = true;
            item.StatusText = "In riproduzione";
            NowPlayingText.Text = $"In riproduzione su {item.Name}";
            NowPlayingBar.Visibility = Visibility.Visible;
            VolumeSlider.Value = 100;
        }
        catch (Exception ex)
        {
            item.StatusText = "";
            _activeSession = null;
            _activeItem = null;
            await ShowErrorAsync($"Impossibile connettersi a {item.Name}", ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async void OnDisconnectClicked(object sender, RoutedEventArgs e) => await DisconnectActiveAsync();

    private async Task DisconnectActiveAsync()
    {
        if (_activeSession is null) return;
        AirPlaySession session = _activeSession;
        _activeSession = null;
        await session.DisposeAsync();
    }

    private void HandleDisconnected(DeviceItem item)
    {
        item.IsConnected = false;
        item.StatusText = "";
        if (ReferenceEquals(_activeItem, item))
        {
            _activeItem = null;
            NowPlayingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task HandlePinRequiredAsync(AirPlaySession session, DeviceItem item, string deviceName)
    {
        var pinBox = new TextBox { PlaceholderText = "0000", MaxLength = 4, InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } } };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Codice per {deviceName}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Inserisci il codice a 4 cifre mostrato sullo schermo del dispositivo.", TextWrapping = TextWrapping.Wrap },
                    pinBox,
                },
            },
            PrimaryButtonText = "Connetti",
            CloseButtonText = "Annulla",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(pinBox.Text))
        {
            session.SubmitPin(pinBox.Text.Trim());
        }
        else
        {
            // User cancelled the PIN prompt — tear the in-flight connection down so ConnectAsync unblocks with a clear failure instead of hanging.
            await session.DisposeAsync();
        }
    }

    private async void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_activeSession is null) return;
        try { await _activeSession.SetVolumeAsync(e.NewValue); }
        catch { /* a missed volume update isn't worth surfacing to the user */ }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
