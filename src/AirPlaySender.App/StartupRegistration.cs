using Microsoft.Win32;

namespace AirPlaySender.App;

/// <summary>
/// Registers this app to launch (minimized to the tray) at Windows sign-in,
/// via the per-user <c>Run</c> registry key — no admin rights needed, and no
/// MSIX/Task-Scheduler startup-task machinery, since this is an unpackaged
/// app (<c>WindowsPackageType=None</c>, see AirPlaySender.App.csproj).
///
/// Re-writes the value on every launch (idempotent — a no-op write when it's
/// already correct) rather than only once behind a "first run" flag, so the
/// entry self-heals if the exe gets moved or reinstalled to a new path.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinPlay Mahogany";

    public static void EnsureRegistered()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return; // shouldn't happen for a real .exe launch, but never worth crashing startup over

            string command = $"\"{exePath}\" --minimized";

            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (key.GetValue(ValueName) as string != command)
                key.SetValue(ValueName, command);
        }
        catch
        {
            // Best-effort: not being registered for autostart isn't worth failing
            // launch over — the app still works normally, just not at sign-in.
        }
    }

    /// <summary>Removes the autostart entry, if present. Not currently wired to any UI — here for symmetry/completeness.</summary>
    public static void Unregister()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort, see EnsureRegistered */ }
    }
}
