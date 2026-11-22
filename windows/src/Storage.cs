namespace Rewind;

/// How much room clips take (Settings → Saving), and "Free up space": re-encoding clips older than a week to HEVC
/// at a smaller size, the way the Mac app does. Originals go to the Recycle Bin, never deleted outright, so
/// nothing's lost until it's emptied. Renamed clips are never touched.
static class ClipStorage
{
    public const int ShrinkAfterDays = 7;

    public static long Bytes { get; private set; }
    public static int Count { get; private set; }
    public static bool Measured { get; private set; }
    /// Video clips Free up space would shrink, and how much room they take now.
    public static List<string> Candidates { get; private set; } = new();
    public static long CandidateBytes { get; private set; }
    /// While shrinking: how far along, and how much room it has freed so far.
    public static (int Done, int Total)? Progress { get; private set; }
    public static long Freed { get; private set; }
    /// Fires on the UI thread.
    public static event Action? Changed;
    static volatile bool stop;

    public static void Measure()
    {
        var dir = Settings.Shared.OutputDirectory;
        var prefixes = Settings.Shared.AppPrefixes.ToList();
        var busy = Controller.Shared.Router.Recorder?.Path;
        var ui = System.Windows.Application.Current.Dispatcher;
        Task.Run(() => Scan(dir, prefixes, busy, DateTime.Now)).ContinueWith(t => ui.BeginInvoke(() =>
        {
            if (t.Exception != null) { Log.Error("measuring clips", t.Exception); return; }
            (Bytes, Count, Candidates, CandidateBytes) = t.Result;
            Measured = true;
            Changed?.Invoke();
        }));
    }

    public static (long Bytes, int Count, List<string> Candidates, long CandidateBytes) Scan(string dir, List<string> prefixes, string? busy, DateTime now)
    {
        if (!Directory.Exists(dir)) return (0, 0, new(), 0);
        var cutoff = now.AddDays(-ShrinkAfterDays);
        long bytes = 0, candidateBytes = 0;
        int count = 0;
        var candidates = new List<(string, DateTime)>();
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
        {
            bool renamed = ClipTags.Get(f.FullName, ClipTags.Kind) != null;
            if ((f.Attributes & FileAttributes.Hidden) != 0 || !(Cleanup.IsRewindFile(f.Name, prefixes) || renamed)) continue;
            bytes += f.Length;
            count++;
            var date = f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;
            bool video = f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
            if (video && !renamed && date < cutoff && ClipTags.Get(f.FullName, ClipTags.Shrunk) == null &&
                !string.Equals(f.FullName, busy, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((f.FullName, date));
                candidateBytes += f.Length;
            }
        }
        return (bytes, count, candidates.OrderBy(c => c.Item2).Select(c => c.Item1).ToList(), candidateBytes); // oldest first
    }

    public static async void Shrink()
    {
        if (Progress != null || Candidates.Count == 0) return;
        var list = Candidates.ToList();
        stop = false;
        Freed = 0;
        Progress = (0, list.Count);
        Changed?.Invoke();
        int failures = 0;
        for (int i = 0; i < list.Count && !stop; i++)
        {
            var path = list[i];
            try { Freed += await Task.Run(() => ShrinkOne(path)); }
            catch (Exception e) { failures++; Log.Error($"shrinking {System.IO.Path.GetFileName(path)}", e); }
            Progress = (i + 1, list.Count);
            Changed?.Invoke();
        }
        Progress = null;
        var freed = Controller.SizeText(Freed);
        Measure();
        ClipLibrary.RefreshIfShown();
        Toast.Show(Freed > 0 ? $"Freed up {freed}" : "Nothing to shrink",
            Freed > 0 ? "The originals are in the Recycle Bin: empty it to get the room back."
                : failures > 0 ? $"{failures} clip{(failures == 1 ? "" : "s")} couldn't be shrunk." : "Those clips are already small.",
            Sym.Download, Theme.Accent, duration: 7);
    }

    public static void Stop() => stop = true;

    /// Re-encodes one clip to HEVC ("smaller file") beside it, and if that's under `worthIt` of its size, puts the
    /// original in the Recycle Bin and the new file in its place (same name, dates and game). Returns the bytes saved.
    public static long ShrinkOne(string path, double worthIt = 0.8)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var tmp = System.IO.Path.Combine(dir, $".shrinking-{Guid.NewGuid():N}{System.IO.Path.GetExtension(path)}");
        try
        {
            double length = Mp4Reader.Open(path).Video?.PresentedDuration ?? throw new RewindException("This clip has no video.");
            ClipConverter.Convert(path, 0, length, ExportFormat.Hevc, 25, tmp, _ => { });
            long before = new FileInfo(path).Length, after = new FileInfo(tmp).Length;
            double newLength = Mp4Reader.Open(tmp).Video?.PresentedDuration ?? 0;
            if (after <= 0 || after >= before * worthIt || Math.Abs(newLength - length) > 0.5)
            {
                ClipTags.Set(path, ClipTags.Shrunk, "kept"); // not worth it: don't try again
                return 0;
            }
            DateTime created = File.GetCreationTime(path), written = File.GetLastWriteTime(path);
            ClipTags.Set(tmp, ClipTags.Game, ClipTags.Get(path, ClipTags.Game));
            ClipTags.Set(tmp, ClipTags.Shrunk, "hevc");
            if (!Cleanup.Recycle(path)) throw new RewindException("Couldn't move the original to the Recycle Bin, so it was left as it was.");
            try { File.Move(tmp, path); }
            catch
            {
                // The original is in the Recycle Bin; keep the smaller copy beside where it was rather than lose it.
                var kept = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(path) + " (HEVC)" + System.IO.Path.GetExtension(path));
                File.Move(tmp, kept);
                throw;
            }
            File.SetCreationTime(path, created);
            File.SetLastWriteTime(path, written);
            return before - after;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
