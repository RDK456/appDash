using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

public class AppInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Location { get; set; }
    public string? Exe { get; set; }
    public string Category { get; set; } = "Software";
    public string Kind { get; set; } = "installed";
    public long Size { get; set; }
    public string? InstallDate { get; set; }
    public int RunCount { get; set; }
    public long FocusMs { get; set; }
    public DateTime? LastUsed { get; set; }
    public string Usage { get; set; } = "rare";
    public string? Icon { get; set; }
    [JsonIgnore] public string? UninstallCmd { get; set; }
    [JsonIgnore] public string? RepairCmd { get; set; }
    public bool CanRepair => RepairCmd != null;
}

public static class Scanner
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppDash");
    static string PortableFile => Path.Combine(DataDir, "portable.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static List<AppInfo> installed = [];
    static List<AppInfo> portable = [];

    // Helper/installer exes share names across apps, so they never count as "using" an app.
    static readonly Regex Generic = new(@"^(unins\w*|uninstall\w*|setup\w*|install\w*|update\w*|updater|crashpad_handler|crashreporter|elevate|helper)\.exe$", RegexOptions.IgnoreCase);
    static readonly Regex Installer = new(@"setup|install|unins|redist|update|patch|crash|oalinst|dxsetup", RegexOptions.IgnoreCase);

    public static AppInfo? Find(string id) => installed.Concat(portable).FirstOrDefault(a => a.Id == id);

    public static List<AppInfo> LoadApps()
    {
        var usage = UsageReader.Read();
        installed = ReadInstalled();
        // Re-apply today's rules to saved results: drop anything now known installed or filtered as clutter.
        portable = LoadPortable().Where(p => !IsOwned(p.Id[2..]) && !p.Id.Split('\\').Any(SkipDirs.Contains) && (p.Exe is null || IsRunnable(p.Exe))).ToList();
        foreach (var a in installed.Concat(portable)) ApplyUsage(a, usage);
        return [.. installed, .. portable];
    }

    public static object Disks() => DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
    {
        name = d.Name, label = d.VolumeLabel, format = d.DriveFormat, type = d.DriveType.ToString(),
        total = d.TotalSize, free = d.TotalFreeSpace
    }).ToList();

    // ---------- Installed apps (registry) ----------

    static List<AppInfo> ReadInstalled()
    {
        var list = new List<AppInfo>();
        var icons = new Dictionary<AppInfo, string?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (un is null) continue;
            foreach (var sub in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(sub);
                var name = (k?.GetValue("DisplayName") as string)?.Trim();
                if (k is null || string.IsNullOrEmpty(name) || k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") != null) continue;
                if (k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update" || Regex.IsMatch(name, @"^KB\d+")) continue;
                var version = k.GetValue("DisplayVersion") as string;
                if (!seen.Add(name + "|" + version)) continue;

                var icon = ExePath(k.GetValue("DisplayIcon") as string);
                var uninst = k.GetValue("UninstallString") as string;
                var loc = Dir(k.GetValue("InstallLocation") as string) ?? AppDir(icon) ?? AppDir(ExePath(uninst));
                foreach (var d in new[] { loc, AppDir(icon), AppDir(ExePath(uninst)) })
                    if (d != null && !Broad.Contains(d)) ownedSet.Add(d);
                var publisher = k.GetValue("Publisher") as string;
                var date = k.GetValue("InstallDate") as string;
                var a = new AppInfo
                {
                    Id = $"i:{hive}:{view}:{sub}",
                    Name = name,
                    Publisher = publisher,
                    Version = version,
                    Location = loc,
                    // DisplayIcon often points at a cached installer (Package Cache, Windows\Installer); that's not the app.
                    Exe = icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && AppDir(icon) != null ? icon : null,
                    Size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                    InstallDate = date is { Length: 8 } ? $"{date[..4]}-{date[4..6]}-{date[6..]}" : null,
                    UninstallCmd = uninst,
                    RepairCmd = RepairFor(k, sub),
                    Category = Categorize($"{name} {publisher} {loc} {sub}"),
                };
                list.Add(a);
                icons[a] = icon;
            }
        }
        // ponytail: walks the whole folder for apps that don't report a size; cache by location if load gets slow
        Parallel.ForEach(list.Where(a => a.Size == 0 && a.Location != null), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            a => a.Size = DirSize(a.Location!));
        foreach (var a in list) a.Icon = IconData(icons[a] ?? a.Exe);
        owned = [.. ownedSet];
        return list;
    }

    // MSI products repair in place; others may expose a Modify/Repair screen via ModifyPath.
    static string? RepairFor(RegistryKey k, string sub)
    {
        if (k.GetValue("NoRepair") is 1) return null;
        if (k.GetValue("WindowsInstaller") is 1 && Guid.TryParse(sub, out _)) return $"msiexec /fa {sub}";
        return k.GetValue("NoModify") is 1 ? null : k.GetValue("ModifyPath") as string;
    }

    // Portable apps have no uninstaller: send the folder (or single file) to the Recycle Bin.
    public static bool RemovePortable(string id)
    {
        var a = portable.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("App not found. Refresh and try again.");
        var path = a.Id[2..];
        try
        {
            if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            else FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Delete cancelled."); }
        portable.Remove(a);
        File.WriteAllText(PortableFile, JsonSerializer.Serialize(portable, Json));
        return true;
    }

    // Some installers (Riot) write forward slashes; normalize so path comparisons match.
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')).Replace('/', '\\').TrimEnd('\\');

    static string? Dir(string? s)
    {
        s = Clean(s);
        if (s is null || !Directory.Exists(s) || Path.GetPathRoot(s)?.TrimEnd('\\') == s) return null;
        return s.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    static string? AppDir(string? exe)
    {
        var d = exe is null ? null : Path.GetDirectoryName(exe);
        if (d is null || d.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) || d.Contains("Package Cache", StringComparison.OrdinalIgnoreCase)) return null;
        return Dir(d);
    }

    // ---------- Installed vs portable ----------
    // Settings > Apps and Control Panel both list the registry Uninstall entries. Anything inside a folder
    // one of those entries owns (install folder, icon folder, uninstaller folder) is installed, not portable.
    static List<string> owned = [];

    // Shared parents are never "owned", or one entry with a sloppy path would hide everything beneath it.
    static readonly HashSet<string> Broad = new[]
    {
        Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
    }.Select(f => Environment.GetFolderPath(f)).Where(p => p.Length > 0)
     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
     .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static bool Inside(string path, string dir) =>
        path.Equals(dir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool IsOwned(string path)
    {
        var p = Clean(path) ?? "";
        return owned.Any(d => Inside(p, d));
    }

    // "C:\x\app.exe",0  |  C:\x\app.exe /S  |  C:\x\icon.ico  ->  file path if it exists
    internal static string? ExePath(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string p;
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); p = end > 0 ? s[1..end] : s[1..]; }
        else
        {
            var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            p = i > 0 ? s[..(i + 4)] : s.Split(',')[0];
        }
        p = Environment.ExpandEnvironmentVariables(p);
        return File.Exists(p) ? p : null;
    }

    // ---------- Categories ----------

    // ponytail: keyword heuristic, first match wins; misses show as "Software"
    static readonly (string Cat, Regex Rx)[] Rules =
    [
        ("Games", new(@"steamapps|steam app|\bsteam\b|epic games|gog galaxy|gog\.com|riot games|ubisoft|ea games|electronic arts|xboxgames|battle\.net|blizzard|rockstar games|bethesda|minecraft|\bgames?\b", RegexOptions.IgnoreCase)),
        ("System", new(@"driver|redistributable|runtime|\.net\b|visual c\+\+|directx|nvidia|amd software|radeon|intel\(r\)|intel®|realtek|chipset|firmware|bios|webview2|update health|windows sdk|vulkan|physx|windows kit", RegexOptions.IgnoreCase)),
        ("Development", new(@"visual studio|vs ?code|\bgit\b|github|python|node\.?js|jetbrains|intellij|pycharm|webstorm|rider|android studio|docker|postman|cmake|\bsdk\b|\bwsl\b|powershell|sql server|mysql|postgres|unity hub|unreal|notepad\+\+|sublime|cursor|windsurf|\bjava\b|\bjdk\b", RegexOptions.IgnoreCase)),
        ("Productivity", new(@"\boffice\b|microsoft 365|\bword\b|excel|powerpoint|outlook|onenote|\bteams\b|notion|obsidian|slack|\bzoom\b|acrobat|\bpdf\b|libreoffice|evernote|todoist|trello|clickup|grammarly", RegexOptions.IgnoreCase)),
        ("Internet", new(@"chrome|firefox|\bedge\b|brave|\bopera\b|vivaldi|discord|telegram|whatsapp|\bsignal\b|qbittorrent|utorrent|internet download|thunderbird", RegexOptions.IgnoreCase)),
        ("Media", new(@"\bvlc\b|spotify|\bobs\b|audacity|photoshop|lightroom|premiere|after effects|illustrator|\bgimp\b|blender|davinci|handbrake|foobar|itunes|k-lite|paint\.net|krita|inkscape|\bmedia\b|player|capcut|figma", RegexOptions.IgnoreCase)),
        ("Utilities", new(@"7-zip|winrar|everything|powertoys|ccleaner|revo|anydesk|teamviewer|keepass|bitwarden|1password|sharex|greenshot|rufus|cpu-z|gpu-z|hwinfo|crystaldisk|treesize|windirstat|wiztree|autohotkey|\bvpn\b|backup|onedrive|dropbox|google drive|antivirus|malwarebytes", RegexOptions.IgnoreCase)),
    ];

    static string Categorize(string text) => Rules.FirstOrDefault(r => r.Rx.IsMatch(text)).Cat ?? "Software";

    // ---------- Usage ----------

    static void ApplyUsage(AppInfo a, Dictionary<string, UsageReader.Entry> usage)
    {
        a.RunCount = 0; a.FocusMs = 0; a.LastUsed = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (a.Exe != null) names.Add(Path.GetFileName(a.Exe));
        bool singleFile = a.Kind == "portable" && File.Exists(a.Id[2..]);
        if (!singleFile && a.Location != null && Directory.Exists(a.Location))
            foreach (var f in SafeFiles(a.Location, "*.exe", 1).Take(60)) names.Add(Path.GetFileName(f));
        // ponytail: matched by exe file name, so two apps shipping "app.exe" share history; match full paths if that bites
        foreach (var n in names)
        {
            if (Generic.IsMatch(n) || !usage.TryGetValue(n, out var u)) continue;
            a.RunCount += u.Count;
            a.FocusMs += u.FocusMs;
            if (u.Last is { } l && (a.LastUsed is null || l > a.LastUsed)) a.LastUsed = l;
        }
        a.Usage = Tier(a.LastUsed, a.RunCount);
    }

    internal static string Tier(DateTime? last, int count)
    {
        if (last is null) return "rare";
        var days = (DateTime.UtcNow - last.Value).TotalDays;
        if (days <= 2 || (days <= 7 && count >= 5)) return "often";
        return days > 60 ? "rare" : "sometimes";
    }

    // ---------- Portable apps ----------

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Program Files", "Program Files (x86)",
        "AppData", "node_modules", "WindowsApps", "Recovery", "PerfLogs", "MSOCache", "Config.Msi", "site-packages",
        "__pycache__", "obj", "packages", "steamapps", "_CommonRedist", "CommonRedist", "Redist", "_Redist", "Redistributables",
    };
    static readonly string[] ProjectMarkers = ["package.json", ".sln", ".csproj", "Cargo.toml", "go.mod", "pom.xml", "pyproject.toml"];

    public static List<AppInfo> ScanPortable(string? root, Action<int, int, string> progress, CancellationToken ct)
    {
        if (installed.Count == 0) installed = ReadInstalled();
        var roots = root != null ? [root] : DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name).ToArray();

        // Loose folders hold unrelated files, so each exe/bat there is its own entry instead of the folder being one app.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var loose = new HashSet<string>(roots.Select(r => r.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)
        {
            profile, Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"), Path.Combine(profile, "Desktop"),
        };
        foreach (var r in roots) loose.Add(Path.GetPathRoot(r)!.TrimEnd('\\'));

        var found = new List<AppInfo>();
        var stack = new Stack<string>(roots);
        int dirs = 0;
        while (stack.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            if (++dirs % 250 == 0) progress(dirs, found.Count, dir);
            if (IsOwned(dir)) continue; // belongs to an app listed in Settings > Apps
            string[] files, subs;
            try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            // Source checkouts are build output, not apps.
            if (subs.Any(s => Path.GetFileName(s) == ".git") || files.Any(f => ProjectMarkers.Any(m => f.EndsWith(m, StringComparison.OrdinalIgnoreCase)))) continue;

            var runnable = files.Where(IsRunnable).ToList();
            var exes = runnable.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            var key = dir.TrimEnd('\\');
            if (exes.Count > 0 && !loose.Contains(key)) { found.Add(FolderApp(key, exes)); continue; }
            found.AddRange(runnable.Select(FileApp));

            foreach (var s in subs)
            {
                var n = Path.GetFileName(s);
                if (SkipDirs.Contains(n) || n.StartsWith('.') || n.StartsWith('$')) continue;
                try { if (File.GetAttributes(s).HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                stack.Push(s);
            }
        }
        progress(dirs, found.Count, "");

        var usage = UsageReader.Read();
        foreach (var a in found) ApplyUsage(a, usage);
        var keep = root == null ? [] : portable.Where(p => !Inside(p.Id[2..], root.TrimEnd('\\')) && !IsOwned(p.Id[2..]));
        portable = [.. keep, .. found];
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(PortableFile, JsonSerializer.Serialize(portable, Json));
        return portable;
    }

    static bool IsRunnable(string f)
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" && !Installer.IsMatch(Path.GetFileNameWithoutExtension(f));
    }

    static AppInfo FolderApp(string dir, List<string> exes)
    {
        var folder = Path.GetFileName(dir);
        // Main exe: one named like the folder, else the largest.
        var main = exes.FirstOrDefault(e => folder.Contains(Path.GetFileNameWithoutExtension(e), StringComparison.OrdinalIgnoreCase))
                   ?? exes.MaxBy(e => new FileInfo(e).Length)!;
        var vi = Info(main);
        var name = Pick(vi?.ProductName, vi?.FileDescription) ?? folder;
        return new AppInfo
        {
            Id = "p:" + dir, Name = name, Publisher = Pick(vi?.CompanyName), Version = Pick(vi?.ProductVersion),
            Location = dir, Exe = main, Kind = "portable", Size = DirSize(dir), Icon = IconData(main),
            Category = Categorize($"{name} {vi?.CompanyName} {dir}"),
        };
    }

    static AppInfo FileApp(string file)
    {
        var vi = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Info(file) : null;
        var name = Pick(vi?.ProductName, vi?.FileDescription) ?? Path.GetFileName(file);
        return new AppInfo
        {
            Id = "p:" + file, Name = name, Publisher = Pick(vi?.CompanyName), Version = Pick(vi?.ProductVersion),
            Location = Path.GetDirectoryName(file), Exe = file, Kind = "portable", Size = new FileInfo(file).Length, Icon = IconData(file),
            Category = Categorize($"{name} {vi?.CompanyName} {file}"),
        };
    }

    static List<AppInfo> LoadPortable()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<AppInfo>>(File.ReadAllText(PortableFile), Json) ?? [];
            return list.Where(a => File.Exists(a.Id[2..]) || Directory.Exists(a.Id[2..])).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    // ---------- Helpers ----------

    static FileVersionInfo? Info(string f) { try { return FileVersionInfo.GetVersionInfo(f); } catch { return null; } }
    static string? Pick(params string?[] xs) => xs.Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));

    static IEnumerable<string> SafeFiles(string dir, string pattern, int depth)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions
            { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch { return []; }
    }

    public static long DirSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    static string? IconData(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico is null) return null;
            using var bmp = ico.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}

// Run history: UserAssist (per-user launches, no admin) + Prefetch (last run, needs admin).
static class UsageReader
{
    public record Entry(int Count, long FocusMs, DateTime? Last);

    public static Dictionary<string, Entry> Read()
    {
        var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        void Add(string exe, int count, long focus, DateTime? last) =>
            d[exe] = d.TryGetValue(exe, out var e)
                ? new(e.Count + count, e.FocusMs + focus, e.Last is null || last > e.Last ? last ?? e.Last : e.Last)
                : new(count, focus, last);

        using (var ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
            foreach (var g in ua?.GetSubKeyNames() ?? [])
            {
                using var key = ua!.OpenSubKey(g + @"\Count");
                if (key is null) continue;
                foreach (var v in key.GetValueNames())
                {
                    var path = Rot13(v);
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || key.GetValue(v) is not byte[] { Length: >= 68 } b) continue;
                    long ft = BitConverter.ToInt64(b, 60);
                    DateTime? last = ft > 0 && ft < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(ft) : null;
                    Add(Path.GetFileName(path), BitConverter.ToInt32(b, 4), BitConverter.ToUInt32(b, 12), last);
                }
            }

        try
        {
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            foreach (var f in Directory.EnumerateFiles(pf, "*.pf"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                var i = n.LastIndexOf('-');
                if (i > 0) Add(n[..i], 0, 0, File.GetLastWriteTimeUtc(f));
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        return d;
    }

    static string Rot13(string s) => string.Concat(s.Select(c =>
        c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26) :
        c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26) : c));
}
