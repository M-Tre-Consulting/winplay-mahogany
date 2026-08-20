using System.ComponentModel;
using System.Runtime.CompilerServices;
using AirPlaySender.Core.Discovery;

namespace AirPlaySender.App;

/// <summary>Thin, bindable wrapper around a discovered <see cref="AirPlayDevice"/> plus this window's per-device UI state.</summary>
public sealed class DeviceItem(AirPlayDevice device) : INotifyPropertyChanged
{
    public AirPlayDevice Device { get; } = device;
    public string Name => Device.Name;
    public string Subtitle => string.IsNullOrEmpty(Device.Model) ? (Device.IsAirPlay2 ? "AirPlay 2" : "AirPlay") : Device.Model;

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
