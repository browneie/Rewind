using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Rewind;

/// Moves old Rewind clips and screenshots to the Recycle Bin. Only touches files Rewind named itself
/// ("Replay …", "Recording …", "Screenshot …", or "<app> <date> at <time>…"), never anything else in the folder.
static class Cleanup
{
    public static readonly int[] DayOptions = { 1, 3, 7, 14, 30, 60, 90 };
    public static readonly string[] BuiltInPrefixes = { "Replay", "Recording", "Screenshot" };
    static readonly Regex Stamp = new(@"^\d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}");
    static readonly Regex StampInside = new(@" \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}");

    public record struct Result(int Count, long Bytes);

    public static bool IsRewindFile(string name, IEnumerable<string>? appPrefixes = null)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".mp4" or ".mov" or ".png")) return false;
        if (BuiltInPrefixes.Any(p => name.StartsWith(p + " ", StringComparison.Ordinal))) return true;
        return (appPrefixes ?? Array.Empty<string>()).Any(p =>
            name.StartsWith(p + " ", StringComparison.Ordinal) && Stamp.IsMatch(name[(p.Length + 1)..]));
    }

    /// "Roblox" for "Roblox 2026-11-13 at 16.53.58.mp4"; null for built-in names or no date stamp.
    public static string? AppPrefix(string name)
    {
        if (BuiltInPrefixes.Any(p => name.StartsWith(p + " ", StringComparison.Ordinal))) return null;
        var m = StampInside.Match(name);
        return m.Success && m.Index > 0 ? name[..m.Index] : null;
    }

    /// Recycles Rewind files in `dir` saved more than `days` days before `now`, except `keep`.
    public static Result Run(string dir, int days, DateTime now, IEnumerable<string>? keep = null,
                             IEnumerable<string>? appPrefixes = null, Func<string, bool>? remove = null)
    {
        remove ??= Recycle;
        var prefixes = appPrefixes?.ToList() ?? new List<string>();
        var kept = new HashSet<string>((keep ?? Array.Empty<string>()).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var cutoff = now.AddDays(-days);
        var result = new Result();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.Hidden) != 0 || !IsRewindFile(info.Name, prefixes) || kept.Contains(info.FullName)) continue;
            // The later of the two dates, so a clip you edited recently isn't treated as old.
            var date = info.CreationTime > info.LastWriteTime ? info.CreationTime : info.LastWriteTime;
            if (date >= cutoff) continue;
            long size = info.Length;
            if (remove(path)) result = new Result(result.Count + 1, result.Bytes + size);
        }
        return result;
    }

    /// Sends a file to the Recycle Bin. Windows deletes files too big for the bin for good, so it's asked
    /// to warn first instead of doing that silently.
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
