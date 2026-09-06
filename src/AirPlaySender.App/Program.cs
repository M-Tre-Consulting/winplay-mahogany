using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AirPlaySender.App;

/// <summary>
/// Custom process entry point — replaces the XAML-generated <c>Main</c> (see
/// <c>DISABLE_XAML_GENERATED_MAIN</c> in AirPlaySender.App.csproj) so the app can
/// enforce a single running instance.
///
/// Rule: while a WinPlay Mahogany process is alive for this Windows session —
/// visible in the foreground, minimised, or sitting in the tray with no window at
/// all (its process still shows in Task Manager) — a second launch must NOT start
/// its own copy. Two copies would fight over the same mirroring port (7000) and
/// the same mDNS name. Instead the second launch asks the running instance to
/// bring its window to the foreground, shows the user a short "already running"
/// notice, and exits.
///
/// Detection is a named <see cref="Mutex"/>: the kernel releases it the instant
/// the owning process ends (even on a hard crash), so there is never a stale lock
/// to clean up — more reliable than scanning the process list. The running
/// instance also owns a named <see cref="EventWaitHandle"/> that the second
/// instance signals, so instance one can un-hide itself from the tray.
/// </summary>
internal static class Program
{
    // Session-local (deliberately NOT "Global\"): "already running" means for THIS
    // desktop session/user, which is also the scope port 7000 + the tray icon live in.
    private const string InstanceMutexName = @"Local\WinPlayMahogany.SingleInstance";
    private const string ActivateEventName = @"Local\WinPlayMahogany.ActivateExistingInstance";

    // Kept alive for the whole process lifetime so a second launch sees the name is taken.
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;

    [STAThread]
    private static void Main(string[] args)
    {
        bool isFirstInstance = true;
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName, out isFirstInstance);
        }
        catch
        {
            // Fail open — a quirk in the single-instance plumbing must never be
            // what stops the app from starting at all.
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            AskExistingInstanceToSurface();
            NativeMethods.MessageBox(
                IntPtr.Zero,
                "WinPlay Mahogany è già in esecuzione.\n\n" +
                "Ne esiste già una copia attiva: cerca l'icona nell'area di notifica, " +
                "vicino all'orologio. La finestra esistente è stata riportata in primo piano.",
                "WinPlay Mahogany è già aperto",
                NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION | NativeMethods.MB_SETFOREGROUND);
            return;
        }

        // Create the "come to the foreground" signal now, before the UI exists, so a
        // second launch that happens during our own startup can still reach us.
        try { _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName); }
        catch { _activateEvent = null; }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        GC.KeepAlive(_instanceMutex);
        GC.KeepAlive(_activateEvent);
    }

    /// <summary>
    /// Runs a background thread that invokes <paramref name="onActivate"/> whenever another
    /// launch attempt asks this (the already-running) instance to show itself. Called once
    /// by <see cref="App"/> during startup.
    /// </summary>
    public static void ListenForActivationRequests(Action onActivate)
    {
        EventWaitHandle? ev = _activateEvent;
        if (ev is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try { ev.WaitOne(); }
                catch { return; }
                try { onActivate(); } catch { /* a failed surface attempt must not kill the listener */ }
            }
        })
        {
            IsBackground = true,
            Name = "WinPlay.SingleInstanceListener",
        };
        thread.Start();
    }

    private static void AskExistingInstanceToSurface()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch
        {
            // Best effort — the message box still tells the user what happened.
        }
    }

    private static class NativeMethods
    {
        public const uint MB_OK = 0x00000000;
        public const uint MB_ICONINFORMATION = 0x00000040;
        public const uint MB_SETFOREGROUND = 0x00010000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    }
}
