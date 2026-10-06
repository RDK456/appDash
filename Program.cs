using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run(); // handles install/update/uninstall hooks, must run first
        if (args.Contains("--selftest")) { Updates.SelfTest(); return; } // throws -> non-zero exit code
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

class MainForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    CancellationTokenSource? scanCts, diskCts;
    static string ThemeFile => Path.Combine(Scanner.DataDir, "theme.txt");

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm()
    {
        Text = "AppDash";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); // app.ico, embedded by the csproj
        Size = new Size(1440, 920);
        MinimumSize = new Size(760, 560); // fits a half-screen snap on 1600px+ displays
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 246, 241);
        Controls.Add(web);
        Load += async (_, _) =>
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Scanner.DataDir, "webview"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.WebMessageReceived += OnMessage;
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("index.html")!;
            var html = new StreamReader(s).ReadToEnd();
            var theme = File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile).Trim() : null;
            if (theme is "dark" or "light")
            {
                html = html.Replace("<html lang=\"en\">", $"<html lang=\"en\" data-theme=\"{theme}\">");
                SetTitleBar(theme);
            }
            web.NavigateToString(html);
        };
    }

    // JS sends {id, cmd, arg}; we reply {id, data, error}. Push events carry {event, ...} instead of id.
    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var r = doc.RootElement;
        var id = r.GetProperty("id").GetInt32();
        var arg = r.TryGetProperty("arg", out var a) ? a.Clone() : default;
        object? data = null; string? error = null;
        try { data = await Dispatch(r.GetProperty("cmd").GetString(), arg); }
        catch (Exception ex) { error = ex.Message; }
        Post(new { id, data, error });
    }

    void Post(object o)
    {
        var s = JsonSerializer.Serialize(o, Json);
        if (InvokeRequired) BeginInvoke(() => web.CoreWebView2.PostWebMessageAsJson(s));
        else web.CoreWebView2.PostWebMessageAsJson(s);
    }

    Task<object?> Dispatch(string? cmd, JsonElement arg) => cmd switch
    {
        "apps" => Task.Run<object?>(Scanner.LoadApps),
        "disks" => Task.FromResult<object?>(Scanner.Disks()),
        "updates" => Task.Run<object?>(Updates.Check),
        "upgrade" => Done(() => Updates.Upgrade(Str(arg, "source"), Opt(arg, "id"))),
        "chocoInstall" => Done(Updates.InstallChoco),
        "wingetInstall" => Done(Updates.InstallWinget),
        "appUpdateCheck" => AppUpdater.Check(),
        "appUpdateInstall" => InstallAppUpdate(),
        "scan" => Scan(Opt(arg, "path")),
        "cancelScan" => Done(() => scanCts?.Cancel()),
        "pickFolder" => Task.FromResult<object?>(PickFolder()),
        "reveal" => Done(() => Reveal(App(arg))),
        "launch" => Done(() => Launch(App(arg))),
        "uninstall" => Done(() => RunCmd(App(arg).UninstallCmd, "This app has no uninstaller.")),
        "uninstallMany" => UninstallMany(arg),
        "repair" => Done(() => RunCmd(App(arg).RepairCmd, "This app doesn't offer a repair option.")),
        "removePortable" => Task.FromResult<object?>(Scanner.RemovePortable(Str(arg, "id"))),
        "diskScan" => DiskScanAsync(Str(arg, "drive"), Flag(arg, "refresh")),
        "cancelDiskScan" => Done(() => diskCts?.Cancel()),
        "openFile" => Done(() => Shell(DiskScan.Known(Str(arg, "path")))),
        "revealFile" => Done(() => Process.Start("explorer.exe", $"/select,\"{DiskScan.Known(Str(arg, "path"))}\"")),
        "deleteFile" => Task.FromResult<object?>(DiskScan.Delete(Str(arg, "path"), Flag(arg, "permanent"))),
        "theme" => Done(() => SaveTheme(Str(arg, "theme"))),
        _ => throw new ArgumentException($"Unknown command: {cmd}")
    };

    async Task<object?> Scan(string? root)
    {
        scanCts?.Cancel();
        scanCts = new();
        var token = scanCts.Token;
        try
        {
            return await Task.Run(() => Scanner.ScanPortable(root,
                (dirs, found, current) => Post(new { @event = "scan", dirs, found, current }), token));
        }
        catch (OperationCanceledException) { return null; }
    }

    async Task<object?> InstallAppUpdate()
    {
        await AppUpdater.Install(percent => Post(new { @event = "appUpdate", percent }));
        return true; // not reached when the update applies: the app restarts
    }

    async Task<object?> DiskScanAsync(string drive, bool refresh)
    {
        diskCts?.Cancel();
        diskCts = new();
        var token = diskCts.Token;
        try
        {
            return await Task.Run(() => DiskScan.Scan(drive, refresh,
                (files, size, current) => Post(new { @event = "disk", drive, files, size, current }), token));
        }
        catch (OperationCanceledException) { return null; }
    }

    // One at a time: Windows Installer refuses a second uninstall while one is running.
    async Task<object?> UninstallMany(JsonElement arg)
    {
        var apps = arg.GetProperty("ids").EnumerateArray().Select(i => Scanner.Find(i.GetString()!)).OfType<AppInfo>()
                      .Where(a => !string.IsNullOrWhiteSpace(a.UninstallCmd)).ToList();
        for (int i = 0; i < apps.Count; i++)
        {
            Post(new { @event = "uninstall", done = i, total = apps.Count, name = apps[i].Name });
            // ponytail: some uninstallers relaunch themselves from %TEMP% and exit early, so those can overlap
            using var p = StartCmd(apps[i].UninstallCmd!);
            await p.WaitForExitAsync();
        }
        return apps.Count;
    }

    void SaveTheme(string theme)
    {
        if (theme is not ("dark" or "light")) throw new ArgumentException("Unknown theme.");
        Directory.CreateDirectory(Scanner.DataDir);
        File.WriteAllText(ThemeFile, theme);
        SetTitleBar(theme);
    }

    void SetTitleBar(string theme)
    {
        int dark = theme == "dark" ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        BackColor = dark == 1 ? Color.FromArgb(21, 24, 27) : Color.FromArgb(248, 246, 241);
    }

    string? PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder to scan for portable apps", UseDescriptionForTitle = true };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    // Actions take an app id, never a raw path or command from the page.
    static AppInfo App(JsonElement arg) => Scanner.Find(Str(arg, "id")) ?? throw new ArgumentException("App not found. Refresh and try again.");

    static void Reveal(AppInfo a)
    {
        if (a.Exe != null && File.Exists(a.Exe)) Process.Start("explorer.exe", $"/select,\"{a.Exe}\"");
        else if (a.Location != null && Directory.Exists(a.Location)) Process.Start("explorer.exe", $"\"{a.Location}\"");
        else throw new InvalidOperationException("This app has no install folder on disk.");
    }

    static void Launch(AppInfo a)
    {
        if (a.Exe == null || !File.Exists(a.Exe)) throw new InvalidOperationException("No program file found for this app.");
        Process.Start(new ProcessStartInfo(a.Exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(a.Exe) });
    }

    static void RunCmd(string? cmd, string missing)
    {
        if (string.IsNullOrWhiteSpace(cmd)) throw new InvalidOperationException(missing);
        StartCmd(cmd).Dispose();
    }

    static Process StartCmd(string cmd) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { CreateNoWindow = true, UseShellExecute = false })!;

    static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    static Task<object?> Done(Action act) { act(); return Task.FromResult<object?>(true); }
    static bool Flag(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string Str(JsonElement a, string k) => Opt(a, k) ?? throw new ArgumentException($"Missing {k}");
    static string? Opt(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
