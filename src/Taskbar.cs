using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;

sealed class TaskbarSnapshot
{
    internal readonly IntPtr Window;
    internal readonly uint ProcessId;
    internal readonly Rectangle? Bounds;
    internal readonly DateTime CapturedAt;
    internal readonly string Status;
    internal readonly bool IsError;
    internal TaskbarSnapshot(IntPtr window, uint processId, Rectangle? bounds, DateTime capturedAt, string status, bool isError = false)
    {
        Window = window; ProcessId = processId; Bounds = bounds;
        CapturedAt = capturedAt; Status = status; IsError = isError;
    }
    internal bool IsCurrent(IntPtr window, uint processId, DateTime now)
    {
        var age = now - CapturedAt;
        return Window == window && ProcessId == processId && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(10);
    }
}

// All UI Automation objects stay on this reader's dedicated MTA thread.
sealed class TaskbarReader
{
    IntPtr cachedWindow;
    uint cachedProcessId;
    AutomationElement widgets;
    AutomationElement start;
    internal TaskbarSnapshot Read()
    {
        try {
            var window = Native.FindWindow("Shell_TrayWnd", null);
            uint processId;
            Native.GetWindowThreadProcessId(window, out processId);
            if (window != cachedWindow || processId != cachedProcessId) {
                widgets = start = null;
                cachedWindow = window; cachedProcessId = processId;
            }
            if (window == IntPtr.Zero) return new TaskbarSnapshot(window, processId, null, DateTime.UtcNow, "Taskbar unavailable");
            if (widgets == null || start == null) {
                var element = AutomationElement.FromHandle(window);
                widgets = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
                start = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            }
            if (widgets == null || start == null) return new TaskbarSnapshot(window, processId, null, DateTime.UtcNow, "Widgets or Start unavailable");
            var widgetsBounds = widgets.Current.BoundingRectangle;
            var startBounds = start.Current.BoundingRectangle;
            if (widgetsBounds.IsEmpty || startBounds.IsEmpty || widgetsBounds.Width <= 0 || startBounds.Width <= 0)
                return new TaskbarSnapshot(window, processId, null, DateTime.UtcNow, "Widgets or Start geometry unavailable");
            Native.RECT rect;
            if (!Native.GetWindowRect(window, out rect)) throw new InvalidOperationException("Taskbar disappeared during layout read");
            double scale = Native.GetDpiForWindow(window) / 96.0;
            if (scale <= 0) throw new InvalidOperationException("Taskbar DPI unavailable");
            int gap = (int)(12 * scale), x = (int)(widgetsBounds.Right - rect.Left) + gap;
            int width = Math.Min((int)(340 * scale), (int)(startBounds.Left - rect.Left) - x - gap);
            if (width < 230 * scale || rect.Bottom - rect.Top > 100 * scale)
                return new TaskbarSnapshot(window, processId, null, DateTime.UtcNow, "Unsupported taskbar layout or insufficient space");
            return new TaskbarSnapshot(window, processId, new Rectangle(x, 0, width, rect.Bottom - rect.Top), DateTime.UtcNow, "Layout ready");
        } catch {
            widgets = start = null;
            cachedWindow = IntPtr.Zero; cachedProcessId = 0;
            throw;
        }
    }
}

sealed class TaskbarMonitor
{
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    int stopped;
    internal volatile TaskbarSnapshot Current;
    internal TaskbarMonitor(Func<TaskbarSnapshot> read)
    {
        var thread = new Thread(delegate() {
            try {
                while (!stop.WaitOne(0)) {
                    try { Current = read(); }
                    catch (Exception e) { Current = new TaskbarSnapshot(IntPtr.Zero, 0, null, DateTime.UtcNow, e.Message, true); }
                    if (stop.WaitOne(2000)) break;
                }
            } finally { stop.Dispose(); }
        });
        thread.Name = "AILimits taskbar UI Automation";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    internal void Stop()
    {
        // Never join a worker that may be waiting for an unresponsive provider.
        if (Interlocked.Exchange(ref stopped, 1) == 0) stop.Set();
    }
}

static class TaskbarChecks
{
    internal static void Run()
    {
        var now = DateTime.UtcNow;
        var snapshot = new TaskbarSnapshot(new IntPtr(10), 20, new Rectangle(0, 0, 300, 48), now, "ready");
        if (!snapshot.IsCurrent(new IntPtr(10), 20, now)) throw new Exception("Current taskbar layout rejected");
        if (snapshot.IsCurrent(new IntPtr(11), 20, now) || snapshot.IsCurrent(new IntPtr(10), 21, now)) throw new Exception("Old Explorer layout accepted");
        if (snapshot.IsCurrent(new IntPtr(10), 20, now.AddSeconds(10)) || snapshot.IsCurrent(new IntPtr(10), 20, now.AddSeconds(-1))) throw new Exception("Stale layout accepted");
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var returned = new ManualResetEvent(false)) {
            int uiThread = Thread.CurrentThread.ManagedThreadId;
            bool correctThread = false;
            var monitor = new TaskbarMonitor(delegate {
                correctThread = Thread.CurrentThread.ManagedThreadId != uiThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
                entered.Set(); release.WaitOne(); returned.Set(); return snapshot;
            });
            try {
                if (!entered.WaitOne(3000) || !correctThread) throw new Exception("UI Automation worker must run on a separate MTA thread");
                int ticks = 0;
                using (var timer = new System.Windows.Forms.Timer { Interval = 20 }) {
                    timer.Tick += delegate { ticks++; };
                    timer.Start();
                    var pumping = Stopwatch.StartNew();
                    while (pumping.ElapsedMilliseconds < 250) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
                    if (ticks < 2) throw new Exception("UI timer blocked by UI Automation read");
                }
                var elapsed = Stopwatch.StartNew();
                if (monitor.Current != null) throw new Exception("Blocked read must not publish a layout");
                monitor.Stop(); monitor.Stop();
                if (elapsed.ElapsedMilliseconds > 1000) throw new Exception("Stopping blocked UI Automation must not block the UI thread");
            } finally {
                release.Set();
                if (!returned.WaitOne(3000)) throw new Exception("Test worker did not return");
                monitor.Stop();
            }
        }
        int reads = 0;
        var retry = new TaskbarMonitor(delegate {
            if (Interlocked.Increment(ref reads) == 1) throw new InvalidOperationException("Simulated Explorer restart");
            return snapshot;
        });
        try {
            var elapsed = Stopwatch.StartNew();
            while (retry.Current != snapshot && elapsed.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            if (retry.Current != snapshot) throw new Exception("UI Automation must recover after a provider error");
        } finally { retry.Stop(); }
    }
}
