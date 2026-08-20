using System.ComponentModel;
using System.Runtime.CompilerServices;
using AirPlaySender.Core.Discovery;
using Microsoft.UI.Xaml;

namespace AirPlaySender.App;

/// <summary>
/// Thin, bindable wrapper around a discovered <see cref="AirPlayDevice"/>
/// plus this window's per-device UI state. Exposes ready-made
/// <see cref="Visibility"/>/<see cref="bool"/> properties instead of
/// leaning on XAML value converters: WinUI's XamlCompiler.exe crashes
/// (no diagnostic output) when a `Window.Resources` block instantiates ANY
/// type from this same not-yet-compiled project — even an empty class,
/// converter or not — so converters declared in this project's own XAML
/// aren't usable here. Deriving the display state in code instead sidesteps
/// that entirely, and arguably reads better at the call site anyway.
/// </summary>
public sealed class DeviceItem : INotifyPropertyChanged
{
    public AirPlayDevice Device { get; }
    public string Name => Device.Name;
    public string Subtitle => string.IsNullOrEmpty(Device.Model) ? (Device.IsAirPlay2 ? "AirPlay 2" : "AirPlay") : Device.Model;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (!Set(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ConnectedVisibility));
            OnPropertyChanged(nameof(NotConnectedVisibility));
        }
    }
    public Visibility ConnectedVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotConnectedVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(CanConnect));
        }
    }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanConnect => !IsBusy;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (!Set(ref _statusText, value)) return;
            OnPropertyChanged(nameof(StatusTextVisibility));
        }
    }
    public Visibility StatusTextVisibility => string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;

    public DeviceItem(AirPlayDevice device)
    {
        Device = device;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
