using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("AILimits")]
[assembly: System.Reflection.AssemblyDescription("Codex quota indicator for the Windows 11 taskbar")]
[assembly: System.Reflection.AssemblyProduct("AILimits")]
[assembly: System.Reflection.AssemblyVersion("0.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.3.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.3")]

static class Program
{
    internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AILimits");
    [STAThread] static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        if (args.Length > 0 && args[0] == "--auth-check") {
            try { SettingsChecks.Auth().GetAwaiter().GetResult(); }
            catch (Exception e) { File.WriteAllText(Path.Combine(DataDir, "auth-check.txt"), "FAIL: " + e.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length > 0 && args[0] == "--test") { Tests.Run(); return; }
        if (args.Length > 0 && args[0] == "--probe") {
            try { File.WriteAllText(Path.Combine(DataDir, "probe.json"), new JavaScriptSerializer().Serialize(Codex.Read().GetAwaiter().GetResult())); }
            catch (Exception e) { File.WriteAllText(Path.Combine(DataDir, "probe-error.txt"), e.Message); Environment.ExitCode = 1; }
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\AILimits.Taskbar", out created)) {
            bool openSettings = args.Length > 0 && args[0] == "--settings";
            if (!created) {
                if (openSettings) {
                    for (int attempt = 0; attempt < 30; attempt++) {
                        var existing = Native.FindWindowEx(Native.FindWindow("Shell_TrayWnd", null), IntPtr.Zero, null, "AILimits Taskbar");
                        if (existing != IntPtr.Zero) { Native.PostMessage(existing, 0x802A, IntPtr.Zero, IntPtr.Zero); break; }
                        Thread.Sleep(100);
                    }
                }
                return;
            }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.Run(new Indicator(openSettings));
        }
    }
}

static class Codex
{
    internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
    internal static object Get(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) ? value : null; }
    internal static async Task<Dictionary<string, object>> Read(string home = null)
    {
        using (var session = new CodexSession(home)) {
            await session.Initialize();
            return await session.Request("account/rateLimits/read", new { });
        }
    }
    internal static string Window(object value)
    {
        var window = Map(value);
        if (Get(window, "usedPercent") == null) return "—";
        double used = Convert.ToDouble(Get(window, "usedPercent"));
        int mins = Get(window, "windowDurationMins") == null ? 0 : Convert.ToInt32(Get(window, "windowDurationMins"));
        string label = mins == 10080 ? Ui.Text("7д", "7d") : mins > 0 && mins % 60 == 0 ? (mins / 60) + Ui.Text("г", "h") : mins > 0 ? mins + Ui.Text("хв", "min") : Ui.Text("Ліміт", "Limit");
        return label + ": " + Math.Max(0, Math.Min(100, 100 - used)).ToString("0") + "%";
    }
    internal static string Format(Dictionary<string, object> response)
    {
        var buckets = Map(Get(response, "rateLimitsByLimitId"));
        var bucket = Map(Get(buckets, "codex")) ?? Map(Get(response, "rateLimits"));
        if (bucket == null) return Ui.Text("Codex: немає даних", "Codex: no data");
        var primary = Get(bucket, "primary");
        var secondary = Get(bucket, "secondary");
        return "Codex   " + Window(primary) + (secondary == null ? "" : "   ·   " + Window(secondary));
    }
    internal static DateTimeOffset? FiveHourReset(Dictionary<string, object> response)
    {
        var buckets = Map(Get(response, "rateLimitsByLimitId"));
        var bucket = Map(Get(buckets, "codex")) ?? Map(Get(response, "rateLimits"));
        foreach (string name in new[] { "primary", "secondary" }) {
            var window = Map(Get(bucket, name));
            if (Get(window, "windowDurationMins") == null || Convert.ToInt32(Get(window, "windowDurationMins")) != 300) continue;
            long seconds;
            if (!long.TryParse(Convert.ToString(Get(window, "resetsAt")), out seconds)) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }
    internal static int BackgroundLevel(Dictionary<string, object> response)
    {
        var buckets = Map(Get(response, "rateLimitsByLimitId"));
        var bucket = Map(Get(buckets, "codex")) ?? Map(Get(response, "rateLimits"));
        foreach (string name in new[] { "primary", "secondary" }) {
            var window = Map(Get(bucket, name));
            if (Get(window, "windowDurationMins") == null || Convert.ToInt32(Get(window, "windowDurationMins")) != 300 || Get(window, "usedPercent") == null) continue;
            double remaining = 100 - Convert.ToDouble(Get(window, "usedPercent"));
            if (double.IsNaN(remaining) || double.IsInfinity(remaining)) return -1;
            return remaining >= 90 ? 2 : remaining >= 40 ? 1 : 0;
        }
        return -1;
    }
}

sealed class Indicator : Form
{
    readonly System.Windows.Forms.Timer layoutTimer = new System.Windows.Forms.Timer { Interval = 2000 };
    readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 60000 };
    readonly ToolTip tip = new ToolTip();
    readonly Bitmap codexDark = LoadIcon("codex-dark.png");
    readonly Bitmap codexLight = LoadIcon("codex-light.png");
    readonly Font updatedFont = new Font("Segoe UI", 7f);
    string caption;
    int backgroundLevel = -1;
    bool? updateSucceeded;
    bool fetching;
    bool refreshAgain;
    WidgetSettings settings = WidgetSettings.Load();
    SettingsForm settingsForm;
    IntPtr taskbar;
    AutomationElement widgetsElement;
    AutomationElement startElement;
    Rectangle? attachedBounds;
    string attachmentStatus;
    DateTime lastSuccess;
    DateTimeOffset? fiveHourReset;
    static Bitmap LoadIcon(string name)
    {
        using (var stream = typeof(Indicator).Assembly.GetManifestResourceStream(name)) {
            using (var image = Image.FromStream(stream)) { return new Bitmap(image); }
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
    }
    internal Indicator(bool openSettings = false)
    {
        Ui.Language = settings.Language;
        caption = Ui.Text("Codex: підключення…", "Codex: connecting…");
        Text = "AILimits Taskbar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10000, -10000);
        Size = new Size(310, 40);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;
        var menu = new ContextMenuStrip();
        menu.Items.Add(Ui.Text("Налаштування…", "Settings…"), null, delegate { OpenSettings(); });
        menu.Items.Add(Ui.Text("Оновити", "Refresh"), null, async delegate { await RefreshQuota(); });
        menu.Items.Add(Ui.Text("Закрити індикатор", "Exit widget"), null, delegate { Close(); });
        ContextMenuStrip = menu;
        layoutTimer.Tick += delegate { Attach(); };
        refreshTimer.Tick += async delegate { await RefreshQuota(); };
        refreshTimer.Interval = settings.RefreshSeconds * 1000;
        Shown += async delegate { Attach(); layoutTimer.Start(); refreshTimer.Start(); if (openSettings) BeginInvoke(new Action(OpenSettings)); await RefreshQuota(); };
        FormClosing += delegate { if (settingsForm != null) settingsForm.Close(); };
        FormClosed += delegate { layoutTimer.Dispose(); refreshTimer.Dispose(); tip.Dispose(); codexDark.Dispose(); codexLight.Dispose(); updatedFont.Dispose(); };
    }
    void OpenSettings()
    {
        if (settingsForm != null) { settingsForm.Show(); Native.SetForegroundWindow(settingsForm.Handle); return; }
        var dialog = new SettingsForm(settings);
        settingsForm = dialog;
        dialog.FormClosed += delegate {
            settingsForm = null;
            if (dialog.DialogResult == DialogResult.OK && !IsDisposed) {
                settings = dialog.Result;
                Ui.Language = settings.Language;
                ContextMenuStrip.Items[0].Text = Ui.Text("Налаштування…", "Settings…");
                ContextMenuStrip.Items[1].Text = Ui.Text("Оновити", "Refresh");
                ContextMenuStrip.Items[2].Text = Ui.Text("Закрити індикатор", "Exit widget");
                tip.SetToolTip(this, Ui.Text("Codex: підключення…", "Codex: connecting…"));
                refreshTimer.Interval = settings.RefreshSeconds * 1000;
                refreshTimer.Stop(); refreshTimer.Start();
                caption = Ui.Text("Codex: підключення…", "Codex: connecting…"); backgroundLevel = -1; updateSucceeded = null;
                fiveHourReset = null; lastSuccess = default(DateTime); Invalidate();
                BeginInvoke(new Action(async delegate { await RefreshQuota(); }));
            }
            dialog.Dispose();
        };
        dialog.Show();
        Native.SetForegroundWindow(dialog.Handle);
    }
    async Task RefreshQuota()
    {
        if (fetching) { refreshAgain = true; return; }
        fetching = true;
        var requestSettings = settings;
        try {
            var data = await Task.Run(() => Codex.Read(requestSettings.Home));
            if (IsDisposed || requestSettings != settings) return;
            if (Codex.BackgroundLevel(data) < 0) throw new IOException(Ui.Text("Не отримано актуальні дані п’ятигодинного ліміту Codex.", "Current five-hour Codex quota data is unavailable."));
            caption = Codex.Format(data);
            backgroundLevel = Codex.BackgroundLevel(data);
            fiveHourReset = Codex.FiveHourReset(data);
            lastSuccess = DateTime.Now;
            tip.SetToolTip(this, Ui.Text("Залишок квоти Codex. Оновлено ", "Remaining Codex quota. Updated ") + lastSuccess.ToString("HH:mm:ss"));
            File.WriteAllText(Path.Combine(Program.DataDir, "status.json"), new JavaScriptSerializer().Serialize(new { updatedAt = DateTime.UtcNow.ToString("o"), text = caption }));
            updateSucceeded = true;
        } catch {
            if (IsDisposed || requestSettings != settings) return;
            updateSucceeded = false;
            caption = "Codex: " + (lastSuccess == default(DateTime) ? Ui.Text("немає зв’язку", "offline") : Ui.Text("дані застаріли", "data is stale"));
            backgroundLevel = -1;
            fiveHourReset = null;
            tip.SetToolTip(this, Ui.Text("Не вдалося оновити квоту. Перевірте з’єднання та вхід у Codex.", "Could not refresh the quota. Check your connection and Codex sign-in."));
        } finally {
            fetching = false;
            if (!IsDisposed) {
                Invalidate();
                if (refreshAgain) { refreshAgain = false; BeginInvoke(new Action(async delegate { await RefreshQuota(); })); }
            }
        }
    }
    void Attach()
    {
        try {
            var bar = Native.FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero) { Native.ShowWindow(Handle, 0); return; }
            Native.RECT rect;
            Native.GetWindowRect(bar, out rect);
            if (taskbar != bar || widgetsElement == null || startElement == null) {
                var element = AutomationElement.FromHandle(bar);
                widgetsElement = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
                startElement = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            }
            if (widgetsElement == null || startElement == null) { Native.ShowWindow(Handle, 0); return; }
            var wr = widgetsElement.Current.BoundingRectangle;
            var sr = startElement.Current.BoundingRectangle;
            double scale = Native.GetDpiForWindow(bar) / 96.0;
            int gap = (int)(12 * scale), x = (int)(wr.Right - rect.Left) + gap;
            int width = Math.Min((int)(340 * scale), (int)(sr.Left - rect.Left) - x - gap);
            if (width < 230 * scale || rect.Bottom - rect.Top > 100 * scale) { Native.ShowWindow(Handle, 0); return; }
            if (taskbar != bar || Native.GetParent(Handle) != bar) {
                Native.SetWindowLongPtr(Handle, -16, new IntPtr((Native.GetWindowLongPtr(Handle, -16).ToInt64() & ~0x80000000L) | 0x40000000L));
                Native.SetParent(Handle, bar);
                if (Native.GetParent(Handle) != bar) throw new InvalidOperationException("Cannot attach to taskbar");
                taskbar = bar;
                attachedBounds = null;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                ForeColor = key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1 ? Color.FromArgb(25, 25, 25) : Color.FromArgb(240, 240, 240);
            }
            var bounds = new Rectangle(x, 0, width, rect.Bottom - rect.Top);
            if (attachedBounds != bounds || !Native.IsWindowVisible(Handle)) {
                Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
                attachedBounds = bounds;
            }
            string status = "Parent=" + Native.GetParent(Handle) + " Taskbar=" + bar + " Child=" + ((Native.GetWindowLongPtr(Handle, -16).ToInt64() & 0x40000000L) != 0) + " X=" + x + " Width=" + width;
            if (attachmentStatus != status) {
                File.WriteAllText(Path.Combine(Program.DataDir, "attachment.txt"), status);
                attachmentStatus = status;
            }
        } catch (Exception e) {
            widgetsElement = null;
            startElement = null;
            Native.ShowWindow(Handle, 0);
            string error = "Error: " + e.Message;
            if (attachmentStatus != error) {
                File.WriteAllText(Path.Combine(Program.DataDir, "attachment-error.txt"), e.Message);
                attachmentStatus = error;
            }
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = e.Graphics.DpiX / 96f;
        bool dark = ForeColor.GetBrightness() > 0.5f;
        Color panelColor = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(245, 245, 245);
        Color borderColor = dark ? Color.FromArgb(67, 67, 67) : Color.FromArgb(207, 207, 207);
        if (backgroundLevel == 2) {
            panelColor = dark ? Color.FromArgb(30, 85, 49) : Color.FromArgb(200, 237, 210);
            borderColor = dark ? Color.FromArgb(57, 133, 79) : Color.FromArgb(117, 181, 136);
        } else if (backgroundLevel == 1) {
            panelColor = dark ? Color.FromArgb(101, 80, 10) : Color.FromArgb(255, 235, 155);
            borderColor = dark ? Color.FromArgb(159, 128, 27) : Color.FromArgb(202, 170, 67);
        } else if (backgroundLevel == 0) {
            panelColor = dark ? Color.FromArgb(111, 39, 39) : Color.FromArgb(250, 205, 205);
            borderColor = dark ? Color.FromArgb(166, 65, 65) : Color.FromArgb(207, 129, 129);
        }
        int iconSize = (int)(20 * scale);
        int padding = (int)(10 * scale);
        int textLeft = padding + iconSize + (int)(8 * scale);
        string displayText = caption.StartsWith("Codex", StringComparison.Ordinal) ? caption.Substring(5).TrimStart(':', ' ') : caption;
        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int textWidth = TextRenderer.MeasureText(e.Graphics, displayText, Font, new Size(int.MaxValue, ClientSize.Height), textFlags).Width;
        int panelWidth = Math.Min(ClientSize.Width - 2, textLeft + textWidth + padding);
        int panelHeight = Math.Min(ClientSize.Height - 2, (int)(38 * scale));
        var panel = new RectangleF(0.5f, (ClientSize.Height - panelHeight) / 2f, panelWidth, panelHeight);
        float diameter = 16 * scale;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
            path.AddArc(panel.Left, panel.Top, diameter, diameter, 180, 90);
            path.AddArc(panel.Right - diameter, panel.Top, diameter, diameter, 270, 90);
            path.AddArc(panel.Right - diameter, panel.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(panel.Left, panel.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(panelColor)) e.Graphics.FillPath(brush, path);
            using (var pen = new Pen(borderColor, 1)) e.Graphics.DrawPath(pen, path);
        }
        int topRowY = (int)panel.Top + (int)(2 * scale);
        int topRowHeight = (int)(21 * scale);
        e.Graphics.DrawImage(dark ? codexDark : codexLight,
            new Rectangle(padding, (int)(panel.Top + (panel.Height - iconSize) / 2), iconSize, iconSize));
        var textBounds = new Rectangle(textLeft, topRowY, Math.Max(0, panelWidth - textLeft - padding), topRowHeight);
        TextRenderer.DrawText(e.Graphics, displayText, Font, textBounds, ForeColor, panelColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        string updatedText = fiveHourReset.HasValue
            ? Ui.Text("Скидання о ", "Resets at ") + fiveHourReset.Value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : Ui.Text("Скидання: —", "Reset: —");
        var updatedBounds = new Rectangle(padding, (int)panel.Top + (int)(23 * scale),
            Math.Max(0, panelWidth - 2 * padding), (int)(12 * scale));
        TextRenderer.DrawText(e.Graphics, updatedText, updatedFont, updatedBounds, ForeColor, panelColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        float dotSize = 6 * scale;
        var dot = new RectangleF(panel.Right - 9 * scale - dotSize,
            panel.Bottom - 6 * scale - dotSize, dotSize, dotSize);
        Color dotColor = !updateSucceeded.HasValue ? Color.FromArgb(160, 160, 160)
            : updateSucceeded.Value ? Color.FromArgb(55, 235, 120) : Color.FromArgb(255, 75, 75);
        using (var brush = new SolidBrush(dotColor)) e.Graphics.FillEllipse(brush, dot);
        using (var pen = new Pen(dark ? Color.FromArgb(25, 25, 25) : Color.White, 1)) e.Graphics.DrawEllipse(pen, dot);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x802A) { BeginInvoke(new Action(OpenSettings)); return; }
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }
}

static class Native
{
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] internal static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

static class Tests
{
    internal static void Run()
    {
        Ui.Language = "uk";
        SettingsChecks.Validate();
        var json = new JavaScriptSerializer();
        Action<string, string> check = delegate(string fixture, string expected) {
            string actual = Codex.Format(json.Deserialize<Dictionary<string, object>>(fixture));
            if (actual != expected) throw new Exception("Expected " + expected + "; got " + actual);
        };
        check("{}", "Codex: немає даних");
        check("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}", "Codex   —");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":16,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":3,\"windowDurationMins\":10080}}}", "Codex   5г: 84%   ·   7д: 97%");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":5}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":110,\"windowDurationMins\":60}}}}", "Codex   1г: 0%");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":-10,\"windowDurationMins\":15}}}", "Codex   15хв: 100%");
        check("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300}}}", "Codex   —");
        foreach (var test in new[] { new[] { 0, 2 }, new[] { 10, 2 }, new[] { 11, 1 }, new[] { 60, 1 }, new[] { 61, 0 }, new[] { 100, 0 } }) {
            var fixture = json.Deserialize<Dictionary<string, object>>("{\"rateLimits\":{\"primary\":{\"usedPercent\":" + test[0] + ",\"windowDurationMins\":300}}}");
            if (Codex.BackgroundLevel(fixture) != test[1]) throw new Exception("Wrong background at usedPercent=" + test[0]);
        }
        foreach (string fixture in new[] { "{}", "{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300}}}", "{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"windowDurationMins\":10080}}}" }) {
            if (Codex.BackgroundLevel(json.Deserialize<Dictionary<string, object>>(fixture)) != -1) throw new Exception("Expected neutral background");
        }
        var resetFixture = json.Deserialize<Dictionary<string, object>>("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080,\"resetsAt\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"secondary\":{\"windowDurationMins\":300,\"resetsAt\":1700000000}}}}");
        if (Codex.FiveHourReset(resetFixture) != DateTimeOffset.FromUnixTimeSeconds(1700000000)) throw new Exception("Wrong five-hour reset timestamp");
        foreach (string fixture in new[] { "{}", "{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300}}}", "{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080,\"resetsAt\":1700000000}}}" }) {
            if (Codex.FiveHourReset(json.Deserialize<Dictionary<string, object>>(fixture)) != null) throw new Exception("Expected unknown five-hour reset");
        }
        Ui.Language = "en";
        check("{}", "Codex: no data");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":16,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":3,\"windowDurationMins\":10080}}}", "Codex   5h: 84%   ·   7d: 97%");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":-10,\"windowDurationMins\":15}}}", "Codex   15min: 100%");
        check("{\"rateLimits\":{\"primary\":{\"usedPercent\":50}}}", "Codex   Limit: 50%");
        Ui.Language = "uk";
        check("{}", "Codex: немає даних");
        File.WriteAllText(Path.Combine(Program.DataDir, "tests.txt"), "PASS: 11 bilingual quota formatting; 9 background; 4 reset timestamp cases; settings validation, language roundtrip and legacy fallback");
    }
}
