using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

sealed class WidgetSettings
{
    public int RefreshSeconds { get; set; }
    public string ProfileId { get; set; }
    public string AccountLabel { get; set; }
    internal string Home { get { return string.IsNullOrEmpty(ProfileId) ? null : Path.Combine(Program.DataDir, "accounts", ProfileId); } }
    public WidgetSettings() { RefreshSeconds = 60; }
    internal void Validate()
    {
        if (RefreshSeconds < 15 || RefreshSeconds > 3600) throw new ArgumentException("Інтервал має бути від 15 до 3600 секунд.");
        Guid id;
        if (!string.IsNullOrEmpty(ProfileId) && !Guid.TryParseExact(ProfileId, "N", out id)) throw new ArgumentException("Некоректний профіль акаунта.");
    }
    internal static WidgetSettings Load()
    {
        try {
            var value = new JavaScriptSerializer().Deserialize<WidgetSettings>(File.ReadAllText(Path.Combine(Program.DataDir, "settings.json")));
            value.Validate(); return value;
        } catch { return new WidgetSettings(); }
    }
    internal void Save()
    {
        Validate();
        string file = Path.Combine(Program.DataDir, "settings.json");
        string temp = file + ".tmp";
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
        if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
    }
}

// One sequential JSON-RPC connection. Disposed after reads or a login attempt.
sealed class CodexSession : IDisposable
{
    readonly Process process = new Process();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    int nextId;
    internal CodexSession(string home)
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (!File.Exists(executable)) executable = "codex.exe";
        process.StartInfo = new ProcessStartInfo(executable, "app-server --listen stdio://" + (home == null ? "" : " -c cli_auth_credentials_store=file")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Program.DataDir
        };
        if (home != null) {
            Directory.CreateDirectory(home);
            process.StartInfo.EnvironmentVariables["CODEX_HOME"] = home;
            process.StartInfo.EnvironmentVariables.Remove("OPENAI_API_KEY");
            process.StartInfo.EnvironmentVariables.Remove("CODEX_API_KEY");
        }
        process.Start();
        process.ErrorDataReceived += delegate { };
        process.BeginErrorReadLine();
    }
    internal async Task Initialize()
    {
        await Request("initialize", new { clientInfo = new { name = "ai_limits_taskbar", title = "AI Limits Taskbar", version = "0.1.0" } });
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
    }
    async Task<Dictionary<string, object>> Next(Task deadline)
    {
        var read = process.StandardOutput.ReadLineAsync();
        if (await Task.WhenAny(read, deadline) != read) throw new TimeoutException("Час очікування Codex вичерпано.");
        string line = await read;
        if (line == null) throw new IOException("Codex закрив з’єднання.");
        return json.Deserialize<Dictionary<string, object>>(line);
    }
    internal async Task<Dictionary<string, object>> Request(string method, object args)
    {
        int id = ++nextId;
        await process.StandardInput.WriteLineAsync(json.Serialize(new { id = id, method = method, @params = args }));
        using (var timeout = new CancellationTokenSource()) {
            var deadline = Task.Delay(20000, timeout.Token);
            try {
                while (true) {
                    var msg = await Next(deadline);
                    if (Convert.ToString(Codex.Get(msg, "id")) != id.ToString()) continue;
                    if (Codex.Get(msg, "error") != null) throw new IOException(Convert.ToString(Codex.Get(Codex.Map(Codex.Get(msg, "error")), "message")));
                    return Codex.Map(Codex.Get(msg, "result"));
                }
            } finally { timeout.Cancel(); }
        }
    }
    internal async Task WaitLogin(string loginId, CancellationToken cancel)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
            var deadline = Task.Delay(TimeSpan.FromMinutes(5), timeout.Token);
            try {
                while (true) {
                    cancel.ThrowIfCancellationRequested();
                    var msg = await Next(deadline);
                    if (Convert.ToString(Codex.Get(msg, "method")) != "account/login/completed") continue;
                    var args = Codex.Map(Codex.Get(msg, "params"));
                    if (Convert.ToString(Codex.Get(args, "loginId")) != loginId) continue;
                    if (!object.Equals(Codex.Get(args, "success"), true)) throw new IOException("Вхід не завершено. Спробуйте ще раз.");
                    return;
                }
            } finally { timeout.Cancel(); }
        }
    }
    public void Dispose()
    {
        try { process.StandardInput.Close(); if (!process.WaitForExit(1000)) process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}

sealed class SettingsForm : Form
{
    readonly NumericUpDown interval = new NumericUpDown { Minimum = 15, Maximum = 3600, Increment = 15, Width = 110 };
    readonly RadioButton shared = new RadioButton { Text = "Акаунт із застосунку Codex", AutoSize = true };
    readonly RadioButton separate = new RadioButton { Text = "Окремий акаунт для віджета", AutoSize = true };
    readonly Label account = new Label { AutoSize = false, Height = 38, Width = 440 };
    readonly Label status = new Label { AutoSize = false, Height = 48, Width = 440 };
    readonly Button login = new Button { Text = "Увійти в інший акаунт…", Width = 230, Height = 32 };
    readonly Button save = new Button { Text = "Зберегти", Width = 110, Height = 32 };
    readonly Button cancelLogin = new Button { Text = "Скасувати вхід", Width = 140, Height = 32, Visible = false };
    CancellationTokenSource loginCancel;
    string profileId;
    string accountLabel;
    int accountReadVersion;
    internal WidgetSettings Result { get; private set; }
    internal SettingsForm(WidgetSettings current)
    {
        Text = "Налаштування AILimits";
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(490, 425);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        using (var iconStream = typeof(SettingsForm).Assembly.GetManifestResourceStream("ailimits.ico")) {
            Icon = new Icon(iconStream);
        }
        profileId = current.ProfileId; accountLabel = current.AccountLabel;
        interval.Value = current.RefreshSeconds;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        content.Controls.Add(new Label { Text = "Частота оновлення квоти", AutoSize = true, Margin = new Padding(3, 0, 3, 8) });
        var frequency = new FlowLayoutPanel { Width = 440, Height = 40 };
        frequency.Controls.Add(interval);
        frequency.Controls.Add(new Label { Text = "секунд (15–3600)", AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        content.Controls.Add(frequency);
        content.Controls.Add(shared); content.Controls.Add(separate); content.Controls.Add(account);
        var authButtons = new FlowLayoutPanel { Width = 440, Height = 42 };
        authButtons.Controls.Add(login); authButtons.Controls.Add(cancelLogin); content.Controls.Add(authButtons);
        content.Controls.Add(new Label { Text = "Окремий вхід відкривається у браузері та діє лише для віджета. Поточний акаунт Codex не зміниться.", Width = 440, Height = 48 });
        content.Controls.Add(status);
        var buttons = new FlowLayoutPanel { Width = 440, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Скасувати", Width = 110, Height = 32, DialogResult = DialogResult.Cancel };
        close.Click += delegate { Close(); };
        buttons.Controls.Add(close); buttons.Controls.Add(save); content.Controls.Add(buttons);
        Controls.Add(content); CancelButton = close; AcceptButton = save;
        shared.Checked = string.IsNullOrEmpty(profileId); separate.Checked = !shared.Checked;
        shared.CheckedChanged += async delegate { await ReadAccount(); };
        Shown += async delegate { await ReadAccount(); };
        login.Click += async delegate { await Login(); };
        cancelLogin.Click += delegate { if (loginCancel != null) loginCancel.Cancel(); };
        save.Click += delegate {
            if (separate.Checked && string.IsNullOrEmpty(profileId)) { status.Text = "Спочатку увійдіть в окремий акаунт."; return; }
            try {
                Result = new WidgetSettings { RefreshSeconds = (int)interval.Value, ProfileId = shared.Checked ? null : profileId, AccountLabel = shared.Checked ? null : accountLabel };
                Result.Save(); DialogResult = DialogResult.OK; Close();
            } catch (Exception e) { status.Text = "Не вдалося зберегти: " + e.Message; }
        };
        FormClosing += delegate { if (loginCancel != null) loginCancel.Cancel(); accountReadVersion++; };
    }
    async Task ReadAccount()
    {
        int version = ++accountReadVersion;
        if (separate.Checked && string.IsNullOrEmpty(profileId)) { account.Text = "Окремий акаунт ще не підключений."; return; }
        string home = shared.Checked ? null : Path.Combine(Program.DataDir, "accounts", profileId);
        account.Text = "Перевірка акаунта…";
        try {
            string label = await Task.Run(async delegate {
                using (var session = new CodexSession(home)) { await session.Initialize(); return AccountName(await session.Request("account/read", new { refreshToken = false })); }
            });
            if (!IsDisposed && version == accountReadVersion) account.Text = label;
        } catch { if (!IsDisposed && version == accountReadVersion) account.Text = "Не вдалося перевірити акаунт. Можна повторити вхід."; }
    }
    static string AccountName(Dictionary<string, object> result)
    {
        var info = Codex.Map(Codex.Get(result, "account"));
        if (info == null) return "Вхід не виконано";
        return Convert.ToString(Codex.Get(info, "email")) + "  ·  " + Convert.ToString(Codex.Get(info, "planType"));
    }
    async Task Login()
    {
        loginCancel = new CancellationTokenSource();
        login.Enabled = save.Enabled = shared.Enabled = separate.Enabled = false;
        cancelLogin.Visible = true; status.Text = "Відкриваємо браузер для входу…";
        string candidate = Guid.NewGuid().ToString("N");
        string home = Path.Combine(Program.DataDir, "accounts", candidate);
        try {
            using (var session = new CodexSession(home)) {
                await session.Initialize();
                var result = await session.Request("account/login/start", new { type = "chatgpt" });
                loginCancel.Token.ThrowIfCancellationRequested();
                Uri url;
                if (!Uri.TryCreate(Convert.ToString(Codex.Get(result, "authUrl")), UriKind.Absolute, out url) || url.Scheme != "https" || !(url.Host == "auth.openai.com" || url.Host == "chatgpt.com")) throw new IOException("Неочікувана адреса входу.");
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                status.Text = "Завершіть вхід у браузері. Очікування — до 5 хвилин.";
                await session.WaitLogin(Convert.ToString(Codex.Get(result, "loginId")), loginCancel.Token);
                var info = await session.Request("account/read", new { refreshToken = false });
                if (IsDisposed) return;
                profileId = candidate; accountLabel = AccountName(info);
                separate.Checked = true; account.Text = accountLabel;
                status.Text = "Вхід виконано. Натисніть «Зберегти», щоб застосувати акаунт.";
            }
        } catch (Exception e) { if (!IsDisposed) status.Text = loginCancel.IsCancellationRequested ? "Вхід скасовано." : "Помилка входу: " + e.Message; }
        finally {
            loginCancel.Dispose(); loginCancel = null;
            if (!IsDisposed) { login.Enabled = save.Enabled = shared.Enabled = separate.Enabled = true; cancelLogin.Visible = false; }
        }
    }
}

static class SettingsChecks
{
    internal static void Validate()
    {
        foreach (int seconds in new[] { 15, 60, 3600 }) new WidgetSettings { RefreshSeconds = seconds }.Validate();
        foreach (int seconds in new[] { 0, 14, 3601, int.MaxValue }) {
            bool rejected = false;
            try { new WidgetSettings { RefreshSeconds = seconds }.Validate(); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid refresh interval accepted");
        }
        bool invalidProfile = false;
        try { new WidgetSettings { ProfileId = "..\\.." }.Validate(); } catch (ArgumentException) { invalidProfile = true; }
        if (!invalidProfile) throw new Exception("Invalid profile path accepted");
        var value = new WidgetSettings { RefreshSeconds = 120, ProfileId = Guid.NewGuid().ToString("N"), AccountLabel = "test" };
        var json = new JavaScriptSerializer();
        var restored = json.Deserialize<WidgetSettings>(json.Serialize(value));
        restored.Validate();
        if (restored.RefreshSeconds != 120 || restored.Home != value.Home) throw new Exception("Settings roundtrip failed");
    }
    internal static async Task Auth()
    {
        string home = Path.Combine(Program.DataDir, "accounts", Guid.NewGuid().ToString("N"));
        using (var session = new CodexSession(home)) {
            await session.Initialize();
            var account = await session.Request("account/read", new { refreshToken = false });
            if (Codex.Get(account, "account") != null) throw new Exception("Isolated profile inherited an account");
            var start = await session.Request("account/login/start", new { type = "chatgpt" });
            if (string.IsNullOrEmpty(Convert.ToString(Codex.Get(start, "authUrl")))) throw new Exception("No browser login URL");
            await session.Request("account/login/cancel", new { loginId = Codex.Get(start, "loginId") });
        }
        File.WriteAllText(Path.Combine(Program.DataDir, "auth-check.txt"), "PASS: isolated account is empty; browser login starts; pending login cancels. No browser opened or account changed.");
    }
}
