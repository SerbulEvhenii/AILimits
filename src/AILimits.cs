using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("AILimits")]
[assembly: System.Reflection.AssemblyDescription("Codex quota indicator for the Windows 11 taskbar")]
[assembly: System.Reflection.AssemblyProduct("AILimits")]
[assembly: System.Reflection.AssemblyVersion("0.4.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.4.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.4.2")]

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
    internal static Dictionary<string, object> QuotaWindow(Dictionary<string, object> response, int minutes)
    {
        var buckets = Map(Get(response, "rateLimitsByLimitId"));
        var bucket = Map(Get(buckets, "codex")) ?? Map(Get(response, "rateLimits"));
        foreach (string name in new[] { "primary", "secondary" }) {
            var window = Map(Get(bucket, name));
            if (Get(window, "windowDurationMins") != null && Convert.ToInt32(Get(window, "windowDurationMins")) == minutes) return window;
        }
        return null;
    }
    internal static DateTimeOffset? Reset(Dictionary<string, object> response, int minutes)
    {
        long seconds;
        if (!long.TryParse(Convert.ToString(Get(QuotaWindow(response, minutes), "resetsAt")), out seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static DateTimeOffset? FiveHourReset(Dictionary<string, object> response) { return Reset(response, 300); }
    internal static int BackgroundLevel(Dictionary<string, object> response, int minutes = 300)
    {
        var window = QuotaWindow(response, minutes);
        if (Get(window, "usedPercent") == null) return -1;
        double remaining = 100 - Convert.ToDouble(Get(window, "usedPercent"));
        if (double.IsNaN(remaining) || double.IsInfinity(remaining)) return -1;
        return remaining >= 90 ? 2 : remaining >= 40 ? 1 : 0;
    }
    internal static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (!reset.HasValue) return Ui.Text("Скидання: —", "Reset: —");
        double minutes = (reset.Value - now).TotalMinutes;
        if (minutes <= 0) return Ui.Text("Очікуємо скидання", "Awaiting reset");
        if (minutes < 1) return Ui.Text("Скидання менш ніж за 1 хв", "Resets in <1 min");
        long total = (long)Math.Ceiling(minutes);
        string duration = total >= 1440
            ? total / 1440 + Ui.Text(" д ", "d ") + (total % 1440) / 60 + Ui.Text(" год", "h")
            : total >= 60 ? total / 60 + Ui.Text(" год ", "h ") + total % 60 + Ui.Text(" хв", "min")
            : total + Ui.Text(" хв", "min");
        return Ui.Text("Скидання через ", "Resets in ") + duration;
    }
    internal static Color LevelColor(int level, bool dark)
    {
        return level == 2 ? (dark ? Color.FromArgb(91, 211, 132) : Color.FromArgb(25, 115, 55))
            : level == 1 ? (dark ? Color.FromArgb(244, 200, 77) : Color.FromArgb(139, 99, 0))
            : level == 0 ? (dark ? Color.FromArgb(255, 125, 125) : Color.FromArgb(185, 40, 40))
            : (dark ? Color.Silver : Color.DimGray);
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
    Dictionary<string, object> quota;
    string countdown;
    bool? updateSucceeded;
    bool fetching;
    bool refreshAgain;
    WidgetSettings settings = WidgetSettings.Load();
    SettingsForm settingsForm;
    QuotaDetailsForm detailsForm;
    IntPtr taskbar;
    uint taskbarProcessId;
    TaskbarMonitor taskbarMonitor;
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
        layoutTimer.Tick += delegate { Attach(); UpdateCountdown(); UpdateDetails(); };
        MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenDetails(); };
        refreshTimer.Tick += async delegate { await RefreshQuota(); };
        refreshTimer.Interval = settings.RefreshSeconds * 1000;
        Shown += async delegate {
            if (taskbarMonitor == null) taskbarMonitor = new TaskbarMonitor(new TaskbarReader().Read);
            layoutTimer.Start(); refreshTimer.Start(); Attach();
            if (openSettings) BeginInvoke(new Action(OpenSettings));
            await RefreshQuota();
        };
        FormClosing += delegate { if (settingsForm != null) settingsForm.Close(); if (detailsForm != null) detailsForm.Close(); };
        FormClosed += delegate { layoutTimer.Dispose(); refreshTimer.Dispose(); tip.Dispose(); codexDark.Dispose(); codexLight.Dispose(); updatedFont.Dispose(); };
        Disposed += delegate { if (taskbarMonitor != null) taskbarMonitor.Stop(); };
    }
    void OpenSettings()
    {
        if (detailsForm != null) detailsForm.Close();
        if (settingsForm != null) { settingsForm.Show(); Native.SetForegroundWindow(settingsForm.Handle); return; }
        var dialog = new SettingsForm(settings);
        settingsForm = dialog;
        dialog.FormClosed += delegate {
            settingsForm = null;
            if (dialog.DialogResult == DialogResult.OK && !IsDisposed) {
                if (detailsForm != null) detailsForm.Close();
                settings = dialog.Result;
                Ui.Language = settings.Language;
                ContextMenuStrip.Items[0].Text = Ui.Text("Налаштування…", "Settings…");
                ContextMenuStrip.Items[1].Text = Ui.Text("Оновити", "Refresh");
                ContextMenuStrip.Items[2].Text = Ui.Text("Закрити індикатор", "Exit widget");
                tip.SetToolTip(this, Ui.Text("Codex: підключення…", "Codex: connecting…"));
                refreshTimer.Interval = settings.RefreshSeconds * 1000;
                refreshTimer.Stop(); refreshTimer.Start();
                caption = Ui.Text("Codex: підключення…", "Codex: connecting…"); quota = null; updateSucceeded = null;
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
        UpdateDetails();
        try {
            var data = await Task.Run(() => Codex.Read(requestSettings.Home));
            if (IsDisposed || requestSettings != settings) return;
            if (Codex.BackgroundLevel(data) < 0) throw new IOException(Ui.Text("Не отримано актуальні дані п’ятигодинного ліміту Codex.", "Current five-hour Codex quota data is unavailable."));
            caption = Codex.Format(data);
            quota = data;
            fiveHourReset = Codex.FiveHourReset(data);
            lastSuccess = DateTime.Now;
            tip.SetToolTip(this, Ui.Text("Залишок квоти Codex. Оновлено ", "Remaining Codex quota. Updated ") + lastSuccess.ToString("HH:mm:ss")
                + (fiveHourReset.HasValue ? "\n" + Ui.Text("Скидання 5г: ", "5h reset: ") + fiveHourReset.Value.ToLocalTime().ToString("g", Ui.Culture) : "")
                + "\n" + Ui.Text("Натисніть, щоб переглянути деталі.", "Click to view details."));
            File.WriteAllText(Path.Combine(Program.DataDir, "status.json"), new JavaScriptSerializer().Serialize(new { updatedAt = DateTime.UtcNow.ToString("o"), text = caption }));
            updateSucceeded = true;
        } catch {
            if (IsDisposed || requestSettings != settings) return;
            updateSucceeded = false;
            caption = "Codex: " + (lastSuccess == default(DateTime) ? Ui.Text("немає зв’язку", "offline") : Ui.Text("дані застаріли", "data is stale"));
            quota = null;
            fiveHourReset = null;
            tip.SetToolTip(this, Ui.Text("Не вдалося оновити квоту. Перевірте з’єднання та вхід у Codex.", "Could not refresh the quota. Check your connection and Codex sign-in."));
        } finally {
            fetching = false;
            if (!IsDisposed) {
                UpdateCountdown();
                UpdateDetails();
                Invalidate();
                if (refreshAgain) { refreshAgain = false; BeginInvoke(new Action(async delegate { await RefreshQuota(); })); }
            }
        }
    }
    void UpdateCountdown()
    {
        string next = Codex.Countdown(fiveHourReset, DateTimeOffset.Now);
        if (countdown == next) return;
        countdown = next;
        Invalidate();
    }
    void UpdateDetails()
    {
        if (detailsForm != null) detailsForm.UpdateQuota(quota, lastSuccess, fetching, caption);
    }
    void OpenDetails()
    {
        if (detailsForm != null) { detailsForm.Activate(); return; }
        var dialog = new QuotaDetailsForm(settings.Home, RefreshQuota);
        detailsForm = dialog;
        dialog.FormClosed += delegate { detailsForm = null; dialog.Dispose(); };
        UpdateDetails();
        var anchor = RectangleToScreen(ClientRectangle);
        dialog.Location = new Point(anchor.Left, anchor.Top - dialog.Height - 8);
        dialog.Show();
        var area = Screen.FromRectangle(anchor).WorkingArea;
        dialog.Location = new Point(Math.Max(area.Left, Math.Min(anchor.Left, area.Right - dialog.Width)),
            Math.Max(area.Top, Math.Min(anchor.Top - dialog.Height - 8, area.Bottom - dialog.Height)));
        Native.SetForegroundWindow(dialog.Handle);
    }
    void Attach()
    {
        try {
            var bar = Native.FindWindow("Shell_TrayWnd", null);
            uint processId;
            Native.GetWindowThreadProcessId(bar, out processId);
            if (bar == IntPtr.Zero) { HideIndicator("Taskbar unavailable", false); return; }
            if (taskbar != IntPtr.Zero && (taskbar != bar || taskbarProcessId != processId)) {
                taskbar = IntPtr.Zero; taskbarProcessId = 0; attachedBounds = null;
                if (IsHandleCreated) { Native.ShowWindow(Handle, 0); RecreateHandle(); }
            }
            var snapshot = taskbarMonitor == null ? null : taskbarMonitor.Current;
            if (snapshot != null && snapshot.IsError) { HideIndicator(snapshot.Status, true); return; }
            if (snapshot == null || !snapshot.IsCurrent(bar, processId, DateTime.UtcNow)) { HideIndicator("Waiting for current taskbar layout", false); return; }
            if (!snapshot.Bounds.HasValue) { HideIndicator(snapshot.Status, false); return; }
            if (!IsHandleCreated || !Native.IsWindow(Handle)) RecreateHandle();
            if (taskbar != bar || Native.GetParent(Handle) != bar) {
                Native.SetWindowLongPtr(Handle, -16, new IntPtr((Native.GetWindowLongPtr(Handle, -16).ToInt64() & ~0x80000000L) | 0x40000000L));
                Native.SetParent(Handle, bar);
                if (Native.GetParent(Handle) != bar) throw new InvalidOperationException("Cannot attach to taskbar");
                taskbar = bar; taskbarProcessId = processId;
                attachedBounds = null;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                ForeColor = key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1 ? Color.FromArgb(25, 25, 25) : Color.FromArgb(240, 240, 240);
            }
            var bounds = snapshot.Bounds.Value;
            if (attachedBounds != bounds || !Native.IsWindowVisible(Handle)) {
                Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
                attachedBounds = bounds;
            }
            RecordAttachment("Parent=" + Native.GetParent(Handle) + " Taskbar=" + bar + " Child=" + ((Native.GetWindowLongPtr(Handle, -16).ToInt64() & 0x40000000L) != 0) + " X=" + bounds.X + " Width=" + bounds.Width, false);
        } catch (Exception e) {
            HideIndicator(e.Message, true);
        }
    }
    void HideIndicator(string status, bool error)
    {
        if (IsHandleCreated) Native.ShowWindow(Handle, 0);
        attachedBounds = null;
        RecordAttachment(status, error);
    }
    void RecordAttachment(string status, bool error)
    {
        string state = (error ? "Error: " : "") + status;
        if (attachmentStatus == state) return;
        attachmentStatus = state;
        try { File.WriteAllText(Path.Combine(Program.DataDir, error ? "attachment-error.txt" : "attachment.txt"), status); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = e.Graphics.DpiX / 96f;
        bool dark = ForeColor.GetBrightness() > 0.5f;
        Color panelColor = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(245, 245, 245);
        Color borderColor = dark ? Color.FromArgb(67, 67, 67) : Color.FromArgb(207, 207, 207);
        int iconSize = (int)(20 * scale);
        int padding = (int)(10 * scale);
        int textLeft = padding + iconSize + (int)(8 * scale);
        string displayText = caption.StartsWith("Codex", StringComparison.Ordinal) ? caption.Substring(5).TrimStart(':', ' ') : caption;
        var textFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        string[] values = new string[2];
        int quotaWidth = 0;
        for (int i = 0; i < values.Length; i++) {
            int minutes = i == 0 ? 300 : 10080;
            values[i] = Codex.BackgroundLevel(quota, minutes) < 0
                ? (i == 0 ? Ui.Text("5г: —", "5h: —") : Ui.Text("7д: —", "7d: —"))
                : Codex.Window(Codex.QuotaWindow(quota, minutes));
            quotaWidth = Math.Max(quotaWidth, TextRenderer.MeasureText(e.Graphics, values[i], Font, Size.Empty, textFlags).Width);
        }
        int quotaGap = (int)(12 * scale);
        int textWidth = quota == null ? TextRenderer.MeasureText(e.Graphics, displayText, Font, Size.Empty, textFlags).Width
            : 2 * quotaWidth + quotaGap;
        string resetText = Codex.Countdown(fiveHourReset, DateTimeOffset.Now);
        int resetWidth = TextRenderer.MeasureText(e.Graphics, resetText, updatedFont, Size.Empty, textFlags).Width;
        int panelWidth = Math.Min(ClientSize.Width - 2,
            textLeft + Math.Max(textWidth + padding, resetWidth + (int)(25 * scale)));
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
        if (quota == null) {
            TextRenderer.DrawText(e.Graphics, displayText, Font, textBounds, ForeColor, panelColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        } else {
            int cellWidth = Math.Max(0, (textBounds.Width - quotaGap) / 2);
            for (int i = 0; i < values.Length; i++) {
                int minutes = i == 0 ? 300 : 10080;
                int left = textLeft + i * (cellWidth + quotaGap);
                Color color = Codex.LevelColor(Codex.BackgroundLevel(quota, minutes), dark);
                TextRenderer.DrawText(e.Graphics, values[i], Font,
                    new Rectangle(left, topRowY, cellWidth, topRowHeight), color, panelColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
        var updatedBounds = new Rectangle(textLeft, (int)panel.Top + (int)(23 * scale),
            Math.Max(0, panelWidth - textLeft - (int)(25 * scale)), (int)(12 * scale));
        TextRenderer.DrawText(e.Graphics, resetText, updatedFont, updatedBounds, ForeColor, panelColor,
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

sealed class QuotaDetailsForm : Form
{
    readonly Label account = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
    readonly Label fiveHour = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(3, 14, 3, 0) };
    readonly Label weekly = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(3, 14, 3, 0) };
    readonly Label updated = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(3, 14, 3, 12) };
    readonly Button refresh = new Button { AutoSize = true, MinimumSize = new Size(120, 32) };
    internal QuotaDetailsForm(string home, Func<Task> refreshQuota)
    {
        Text = Ui.Text("Залишок квоти Codex", "Remaining Codex quota");
        Font = new Font("Segoe UI", 10f);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false; TopMost = true; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(370, 340);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
        account.Text = Ui.Text("Перевірка акаунта…", "Checking account…");
        content.Controls.Add(account); content.Controls.Add(fiveHour); content.Controls.Add(weekly);
        content.Controls.Add(updated); content.Controls.Add(refresh); Controls.Add(content);
        refresh.Text = Ui.Text("Оновити", "Refresh");
        refresh.Click += async delegate { refresh.Enabled = false; await refreshQuota(); };
        KeyPreview = true;
        KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += delegate { Close(); };
        Shown += async delegate {
            try {
                string name = await Task.Run(async delegate {
                    using (var session = new CodexSession(home)) {
                        await session.Initialize();
                        return SettingsForm.AccountName(await session.Request("account/read", new { refreshToken = false }));
                    }
                });
                if (!IsDisposed) account.Text = name;
            } catch { if (!IsDisposed) account.Text = Ui.Text("Не вдалося перевірити акаунт.", "Could not check the account."); }
        };
    }
    internal void UpdateQuota(Dictionary<string, object> data, DateTime lastSuccess, bool fetching, string status)
    {
        var now = DateTimeOffset.Now;
        SetWindow(fiveHour, data, 300, now);
        SetWindow(weekly, data, 10080, now);
        updated.Text = (data == null ? status + "\n" : "") + Ui.Text("Оновлено: ", "Updated: ")
            + (lastSuccess == default(DateTime) ? "—" : lastSuccess.ToString("g", Ui.Culture));
        refresh.Enabled = !fetching;
        refresh.Text = fetching ? Ui.Text("Оновлення…", "Refreshing…") : Ui.Text("Оновити", "Refresh");
    }
    static void SetWindow(Label label, Dictionary<string, object> data, int minutes, DateTimeOffset now)
    {
        int level = Codex.BackgroundLevel(data, minutes);
        var reset = Codex.Reset(data, minutes);
        string title = minutes == 300 ? Ui.Text("5 годин", "5 hours") : Ui.Text("Тиждень", "Weekly");
        label.Text = title + " · " + (level < 0 ? Ui.Text("немає даних", "no data")
            : Codex.Window(Codex.QuotaWindow(data, minutes)).Split(':')[1].Trim() + Ui.Text(" залишилось", " remaining"))
            + "\n" + Codex.Countdown(reset, now)
            + (reset.HasValue ? "\n" + reset.Value.ToLocalTime().ToString("f", Ui.Culture) : "");
        label.ForeColor = Codex.LevelColor(level, false);
    }
}

static class Native
{
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
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
        TaskbarChecks.Run();
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
        var independent = json.Deserialize<Dictionary<string, object>>("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":10080,\"resetsAt\":1700600000},\"secondary\":{\"usedPercent\":5,\"windowDurationMins\":300,\"resetsAt\":1700000000}}}}");
        if (Codex.BackgroundLevel(independent, 300) != 2 || Codex.BackgroundLevel(independent, 10080) != 0) throw new Exception("Quota colors must be independent of each other and window order");
        if (Codex.Reset(independent, 10080) != DateTimeOffset.FromUnixTimeSeconds(1700600000)) throw new Exception("Wrong weekly reset");
        if (Codex.QuotaWindow(resetFixture, 10080) != null) throw new Exception("Do not mix quota buckets");
        var invalidReset = json.Deserialize<Dictionary<string, object>>("{\"rateLimits\":{\"secondary\":{\"windowDurationMins\":10080,\"resetsAt\":9223372036854775807}}}");
        if (Codex.Reset(invalidReset, 10080) != null) throw new Exception("Out-of-range reset must be unknown");
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        foreach (string language in new[] { "uk", "en" }) {
            Ui.Language = language;
            if (Codex.Countdown(null, now) != Ui.Text("Скидання: —", "Reset: —")) throw new Exception("Unknown countdown");
            foreach (int seconds in new[] { -60, 0 })
                if (Codex.Countdown(now.AddSeconds(seconds), now) != Ui.Text("Очікуємо скидання", "Awaiting reset")) throw new Exception("Expired reset must wait for fresh data");
            if (Codex.Countdown(now.AddSeconds(30), now) != Ui.Text("Скидання менш ніж за 1 хв", "Resets in <1 min")) throw new Exception("Sub-minute countdown");
            if (Codex.Countdown(now.AddMinutes(1), now) != Ui.Text("Скидання через 1 хв", "Resets in 1min")) throw new Exception("Minute countdown");
            if (Codex.Countdown(now.AddMinutes(84), now) != Ui.Text("Скидання через 1 год 24 хв", "Resets in 1h 24min")) throw new Exception("Hour countdown");
            if (Codex.Countdown(now.AddMinutes(84).AddSeconds(1), now) != Ui.Text("Скидання через 1 год 25 хв", "Resets in 1h 25min")) throw new Exception("Round countdown up");
            if (Codex.Countdown(now.AddDays(2).AddHours(3), now) != Ui.Text("Скидання через 2 д 3 год", "Resets in 2d 3h")) throw new Exception("Weekly countdown");
            if (Codex.Countdown(now.AddMinutes(84), now.ToOffset(TimeSpan.FromHours(5))) != Codex.Countdown(now.AddMinutes(84), now)) throw new Exception("Countdown must use absolute time");
        }
        Ui.Language = "uk";
        File.WriteAllText(Path.Combine(Program.DataDir, "tests.txt"), "PASS: bilingual quota formatting, independent colors and window selection, both reset timestamps, countdown boundaries and time zones; settings validation, language roundtrip and legacy fallback; taskbar MTA worker, nonblocking stop, stale layout rejection and provider recovery");
    }
}
