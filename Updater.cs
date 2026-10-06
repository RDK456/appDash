using System.Reflection;
using Velopack;

// Self-update via Velopack. The feed (folder or URL) is stamped in at build time by build-release.ps1;
// Velopack downloads delta packages when the feed has them and falls back to the full package otherwise.
public static class AppUpdater
{
    static readonly string? Feed = typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateFeed")?.Value is { Length: > 0 } f ? f : null;
    static readonly string BuildVersion = typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "dev";
    static UpdateInfo? pending;

    public static async Task<object?> Check()
    {
        if (Feed is null) return new { managed = false, version = BuildVersion, reason = "This copy wasn't built with build-release.ps1, so it has no update source." };
        var m = new UpdateManager(Feed);
        if (!m.IsInstalled) return new { managed = false, version = BuildVersion, reason = "Install AppDash with Setup.exe to get automatic updates." };
        pending = await m.CheckForUpdatesAsync();
        var deltas = pending?.DeltasToTarget ?? [];
        return new
        {
            managed = true,
            version = m.CurrentVersion?.ToString(),
            available = pending?.TargetFullRelease.Version.ToString(),
            delta = deltas.Length > 0,
            size = deltas.Length > 0 ? deltas.Sum(d => d.Size) : pending?.TargetFullRelease.Size ?? 0,
        };
    }

    public static async Task Install(Action<int> progress)
    {
        var m = new UpdateManager(Feed ?? throw new InvalidOperationException("No update source configured."));
        var u = pending ?? await m.CheckForUpdatesAsync() ?? throw new InvalidOperationException("AppDash is already up to date.");
        await m.DownloadUpdatesAsync(u, progress);
        m.ApplyUpdatesAndRestart(u.TargetFullRelease);
    }
}
