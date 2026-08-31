namespace AirPlaySender.App;

/// <summary>
/// Minimal file logger for the packaged app. Once the app runs as a
/// background/tray app there's no attached console, so this is the only way
/// to see after the fact what <see cref="AirPlayReceiverServer.Diagnostics"/>
/// (which already forwards each <see cref="MirroringDataReceiver"/>'s own
/// Diagnostics too — see AirPlayReceiverServer's SETUP handlers) and this
/// project's own UI-side code actually said. One plain text file next to the
/// exe, appended to, never rotated — meant for debugging a live mirroring
/// session, not long-term production logging.
/// </summary>
internal static class AppLog
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "mirroring.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging itself must never be what breaks the app.
        }
    }
}
