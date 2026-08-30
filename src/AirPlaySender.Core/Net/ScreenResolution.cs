using System.Runtime.InteropServices;

namespace AirPlaySender.Core.Net;

/// <summary>This PC's actual primary-display resolution, for the mirroring receiver's <c>GET /info</c> "displays" entry — real hardware, not a placeholder.</summary>
public static class ScreenResolution
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Falls back to 1920x1080 if the Win32 call somehow returns nothing sane (headless/RDP edge cases) — never worth failing a response over.</summary>
    public static (int Width, int Height) GetPrimary()
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        return (w > 0 && h > 0) ? (w, h) : (1920, 1080);
    }
}
