using Microsoft.VisualBasic.FileIO;

public record FileEntry(string Path, long Size, DateTime Modified);

public class DiskCat
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public int Count { get; set; }
    public List<FileEntry> Files { get; set; } = [];
}

public class DiskResult
{
    public string Drive { get; set; } = "";
    public long Total { get; set; }
    public long FileCount { get; set; }
    public DateTime ScannedAt { get; set; }
    public List<DiskCat> Categories { get; set; } = [];
}

// Walks a whole drive once, totals bytes per file type, keeps the largest files per type.
public static class DiskScan
{
    const int TopN = 100;
    const string SystemCat = "System";
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
    static readonly string[] Order = ["Videos", "Images", "Audio", "Archives", "Documents", "Programs", "Code", SystemCat, "Other"];
    static readonly Dictionary<string, string> ByExt = BuildExtMap();
    static readonly Dictionary<string, DiskResult> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    static Dictionary<string, string> BuildExtMap()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) m["." + e] = cat; }
        Add("Videos", "mp4 mkv avi mov wmv flv webm m4v ts mpg mpeg 3gp vob");
        Add("Images", "jpg jpeg png gif bmp webp heic heif tif tiff raw cr2 nef arw dng psd svg ico");
        Add("Audio", "mp3 wav flac aac ogg m4a wma opus aiff");
        Add("Archives", "zip rar 7z tar gz tgz bz2 xz zst iso img cab");
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt md rtf odt ods odp csv epub");
        Add("Programs", "exe msi msix appx dll sys bat cmd ps1 jar apk");
        Add("Code", "cs js mjs ts tsx jsx py java c cpp h hpp go rs rb php json xml yaml yml html css scss sql ipynb");
        return m;
    }

    internal static string CategoryOf(string path, FileAttributes attr) =>
        path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) || attr.HasFlag(FileAttributes.System)
            ? SystemCat
            : ByExt.GetValueOrDefault(Path.GetExtension(path), "Other");

    public static DiskResult Scan(string drive, bool refresh, Action<long, long, string> progress, CancellationToken ct)
    {
        if (!DriveInfo.GetDrives().Any(d => d.Name.Equals(drive, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown drive.");
        lock (Gate) if (!refresh && Cache.TryGetValue(drive, out var cached)) return cached;

        var acc = Order.ToDictionary(n => n, n => (cat: new DiskCat { Name = n }, top: new PriorityQueue<FileEntry, long>()));
        long count = 0, total = 0;
        // ponytail: hard links (WinSxS) are counted once per link, so System can read higher than real usage
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(drive).EnumerateFiles("*", opts))
        {
            if ((++count & 0x3FFF) == 0) { ct.ThrowIfCancellationRequested(); progress(count, total, f.DirectoryName ?? ""); }
            long len = f.Length;
            total += len;
            var (cat, top) = acc[CategoryOf(f.FullName, f.Attributes)];
            cat.Size += len;
            cat.Count++;
            if (top.Count < TopN) top.Enqueue(new FileEntry(f.FullName, len, f.LastWriteTimeUtc), len);
            else if (top.TryPeek(out _, out var min) && len > min) top.EnqueueDequeue(new FileEntry(f.FullName, len, f.LastWriteTimeUtc), len);
        }

        var result = new DiskResult
        {
            Drive = drive, Total = total, FileCount = count, ScannedAt = DateTime.UtcNow,
            Categories = Order.Select(n => acc[n]).Where(x => x.cat.Count > 0).Select(x =>
            {
                x.cat.Files = x.top.UnorderedItems.Select(i => i.Element).OrderByDescending(e => e.Size).ToList();
                return x.cat;
            }).OrderByDescending(c => c.Size).ToList(),
        };
        lock (Gate) Cache[drive] = result;
        return result;
    }

    // Only paths a scan returned are actionable; the page can't name arbitrary files.
    static (DiskResult res, DiskCat cat, FileEntry file) Lookup(string path)
    {
        lock (Gate)
            foreach (var r in Cache.Values)
                foreach (var c in r.Categories)
                    if (c.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } f)
                        return (r, c, f);
        throw new ArgumentException("That file isn't in the latest disk scan. Rescan and try again.");
    }

    public static string Known(string path) => Lookup(path).file.Path;

    public static DiskResult Delete(string path, bool permanent)
    {
        var (res, cat, file) = Lookup(path);
        if (cat.Name == SystemCat) throw new InvalidOperationException("System files can't be deleted from AppDash.");
        try
        {
            if (permanent) File.Delete(file.Path);
            // AllDialogs keeps Windows' "too big for the Recycle Bin" warning instead of silently nuking it.
            else FileSystem.DeleteFile(file.Path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Delete cancelled."); }
        lock (Gate)
        {
            cat.Files.Remove(file);
            cat.Size -= file.Size;
            cat.Count--;
            res.Total -= file.Size;
        }
        return res;
    }
}
