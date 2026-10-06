using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

public record Update(string Name, string Id, string Version, string Available, string Source);

public static class Updates
{
    public static object Check()
    {
        var w = Run("winget", "upgrade --include-unknown --accept-source-agreements --disable-interactivity");
        var c = Run(ChocoExe(), "outdated -r --ignore-unfound");
        return new
        {
            winget = new { installed = w != null, version = w is null ? null : Run("winget", "--version")?.Trim(), items = w is null ? [] : ParseWinget(w) },
            choco = new { installed = c != null, version = c is null ? null : Run(ChocoExe(), "--version")?.Trim(), items = c is null ? [] : ParseChoco(c) },
        };
    }

    public static void Upgrade(string source, string? id)
    {
        if (id != null && !Regex.IsMatch(id, @"^[\w.\-+…]+$")) throw new ArgumentException("Invalid package id.");
        // Visible console so the user sees installer progress and prompts.
        if (source == "winget")
        {
            var target = id is null ? "--all --include-unknown" : id.EndsWith('…') ? $"\"{id.TrimEnd('…')}\"" : $"--id {id} -e";
            Start($"/k winget upgrade {target} --accept-source-agreements --accept-package-agreements", elevate: false);
        }
        else if (source == "choco") Start($"/k \"\"{ChocoExe()}\" upgrade {id ?? "all"} -y\"", elevate: true);
        else throw new ArgumentException("Unknown source.");
    }

    // Machine env is read from the registry, so a Chocolatey installed after AppDash started is still found.
    static string ChocoExe()
    {
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine);
        var exe = root is null ? null : Path.Combine(root, "bin", "choco.exe");
        return exe != null && File.Exists(exe) ? exe : "choco";
    }

    // Chocolatey publishes itself on winget; without winget, fall back to Chocolatey's official installer script.
    public static void InstallChoco()
    {
        if (Run("winget", "--version") != null)
            Start("/k winget install --id Chocolatey.Chocolatey -e --accept-source-agreements --accept-package-agreements", elevate: false);
        else
            Start("/k powershell -NoProfile -ExecutionPolicy Bypass -Command \"[Net.ServicePointManager]::SecurityProtocol = 3072; iex ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))\"", elevate: true);
    }

    // winget ships inside App Installer; re-registering it is Microsoft's documented fix when winget is missing.
    public static void InstallWinget() =>
        Start("/k powershell -NoProfile -Command \"Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe; winget --version\"", elevate: false);

    static void Start(string args, bool elevate) =>
        Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = elevate ? "runas" : "" });

    static string? Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180_000)) { p.Kill(true); return null; }
            return output.Result;
        }
        catch (Win32Exception) { return null; } // tool not installed
    }

    // winget prints a fixed-width table; column starts come from the header row.
    // ponytail: English header only; localized winget output yields an empty list
    public static List<Update> ParseWinget(string output)
    {
        var list = new List<Update>();
        int idI = -1, verI = -1, avI = -1, srcI = -1;
        bool inTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var cr = line.LastIndexOf('\r');
            if (cr >= 0) line = line[(cr + 1)..]; // drop spinner frames
            if (Regex.IsMatch(line, @"^Name\s+Id\s+Version\s+Available"))
            {
                idI = line.IndexOf(" Id ") + 1; verI = line.IndexOf("Version"); avI = line.IndexOf("Available"); srcI = line.IndexOf("Source");
                inTable = false;
                continue;
            }
            if (line.StartsWith("---")) { inTable = idI > 0; continue; }
            if (!inTable) continue;
            if (string.IsNullOrWhiteSpace(line) || line.Length <= avI) { inTable = false; continue; }
            string Col(int from, int to) => from >= line.Length ? "" : line[from..Math.Min(to < 0 ? line.Length : to, line.Length)].Trim();
            var id = Col(idI, verI);
            if (id.Length == 0 || id.Contains(' ')) continue;
            list.Add(new Update(Col(0, idI), id, Col(verI, avI), Col(avI, srcI), "winget"));
        }
        return list;
    }

    public static List<Update> ParseChoco(string output) =>
        output.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p.Length >= 3 && p[0].Length > 0)
            .Select(p => new Update(p[0], p[0], p[1], p[2], "choco")).ToList();

    public static void SelfTest()
    {
        const string sample = "\r-\r\\\rName                 Id                    Version   Available  Source\n" +
                              "--------------------------------------------------------------------------\n" +
                              "Mozilla Firefox (x6… Mozilla.Firefox       130.0     131.0.2    winget\n" +
                              "7-Zip 23.01 (x64)    7zip.7zip             23.01     24.08      winget\n" +
                              "2 upgrades available.\n";
        var w = ParseWinget(sample);
        Check(w.Count == 2, "winget row count");
        Check(w[0] == new Update("Mozilla Firefox (x6…", "Mozilla.Firefox", "130.0", "131.0.2", "winget"), "winget row 0");
        Check(w[1].Id == "7zip.7zip" && w[1].Available == "24.08", "winget row 1");
        var c = ParseChoco("git|2.40.0|2.47.1|false\r\nnodejs|20.1.0|22.11.0|false\r\n");
        Check(c.Count == 2 && c[1].Available == "22.11.0", "choco rows");
        Check(Scanner.ExePath("\"C:\\Windows\\notepad.exe\",0") == "C:\\Windows\\notepad.exe", "ExePath quoted");
        Check(Scanner.ExePath("C:\\Windows\\notepad.exe /S") == "C:\\Windows\\notepad.exe", "ExePath args");
        Check(Scanner.Tier(DateTime.UtcNow.AddDays(-1), 1) == "often" && Scanner.Tier(null, 0) == "rare"
              && Scanner.Tier(DateTime.UtcNow.AddDays(-30), 3) == "sometimes", "usage tiers");
        Check(Scanner.Inside(@"F:\Riot Games\VALORANT\live\x", @"F:\riot games\valorant\live")   // inside an owned folder
              && Scanner.Inside(@"D:\Client", @"D:\Client")                                        // the folder itself
              && !Scanner.Inside(@"F:\steam", @"F:\steam\steamapps\common\FC 26")                   // parent of an installed app stays portable
              && !Scanner.Inside(@"D:\Client2", @"D:\Client"), "owned folders");                    // sibling with a shared prefix
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Check(DiskScan.CategoryOf(@"D:\clips\a.MKV", FileAttributes.Normal) == "Videos"
              && DiskScan.CategoryOf(@"D:\x\b.zip", FileAttributes.Normal) == "Archives"
              && DiskScan.CategoryOf(Path.Combine(win, "x.zip"), FileAttributes.Normal) == "System"
              && DiskScan.CategoryOf(@"C:\pagefile.sys", FileAttributes.System) == "System"
              && DiskScan.CategoryOf(@"D:\x\c.qqq", FileAttributes.Normal) == "Other", "disk categories");
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception("selftest failed: " + what); }
}
