using System.Collections.Specialized;
using System.IO;

namespace Rewind;

/// Copies a clip to the clipboard so it can be pasted into Discord (or anywhere that takes files).
/// Clips over the size limit in Settings → Saving are re-encoded to fit first, into Rewind's local app data;
/// the original is never changed.
static class ClipSharer
{
    /// Settings' choices, in MB; 0 is "No limit".
    public static readonly int[] PresetsMB = { 0, 10, 50, 500, 1000 };

    public static string Label(int mb) => mb == 0 ? "No limit" : mb >= 1000 && mb % 1000 == 0 ? $"{mb / 1000} GB" : $"{mb} MB";

    /// The clip being shrunk, and how far along it is (0–1). Changed fires on the UI thread when either changes.
    public static string? Busy { get; private set; }
    public static double Progress { get; private set; }
    public static event Action? Changed;

    public static bool IsBusy(string path) => string.Equals(Busy, path, StringComparison.OrdinalIgnoreCase);

    /// "Copy", or "Fitting… 43%" while that clip is being shrunk.
    public static string ButtonTitle(string path) => IsBusy(path) ? $"Fitting… {(int)(Progress * 100)}%" : "Copy";

    public static void Copy(string path)
    {
        if (Busy != null) return;
        int mb = Settings.Shared.ShareLimitMB;
        long limit = mb * 1_000_000L;
        long size = FileSize(path);
        bool isVideo = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";
        if (limit <= 0 || size <= limit || !isVideo)
        {
            if (Put(path))
                Toast.Show("Copied to the clipboard", $"{Format.Bytes(size)} · paste it into Discord or anywhere", Sym.Copy, Theme.Accent);
            return;
        }
        var ui = System.Windows.Application.Current.Dispatcher;
        Busy = path;
        Progress = 0;
        Changed?.Invoke();
        Task.Run(() => Fit(path, limit, p => ui.BeginInvoke(() => { Progress = p; Changed?.Invoke(); })))
            .ContinueWith(t => ui.BeginInvoke(() =>
            {
                Busy = null;
                Changed?.Invoke();
                if (t.Exception is { } ex) { Toast.Error("Couldn't copy the clip", ex.InnerException ?? ex); return; }
                if (Put(t.Result))
                    Toast.Show("Copied to the clipboard",
                        $"Shrunk to {Format.Bytes(FileSize(t.Result))} (was {Format.Bytes(size)}) to fit {Label(mb)}. The original is kept.",
                        Sym.Copy, Theme.Accent, duration: 5);
            }));
    }

    /// Re-encodes `path` to H.264 (which plays everywhere) at a bitrate that fits `limit` bytes. Tries once
    /// more, lower, if the encoder overshoots. Runs off the UI thread.
    public static string Fit(string path, long limit, Action<double> progress)
    {
        var r = Mp4Reader.Open(path);
        var v = r.Video ?? throw new RewindException("This clip has no video.");
        double duration = v.PresentedDuration;
        if (duration <= 0) throw new RewindException("This clip is empty.");
        double audio = 0;
        foreach (var a in r.AudioTracks)
        {
            double rate = a.MediaDuration > 0 ? a.Sizes.Sum(x => (long)x) * 8 / a.MediaDuration : 0;
            audio += rate > 0 ? rate : 192_000;
        }
        // 4% for the MP4 container, and a little room for the encoder's average drifting.
        double video = limit * 8 * 0.92 / duration - audio;
        string label = Label((int)(limit / 1_000_000));
        if (video < 150_000)
            throw new RewindException($"It's too long to fit in {label}. Trim it first, or raise the limit in Settings → Saving.");

        var dest = System.IO.Path.Combine(CacheFolder(), System.IO.Path.GetFileName(path)); // same name, so it shows up nicely where it's pasted
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (File.Exists(dest)) File.Delete(dest);
            int at = attempt;
            ClipConverter.Convert(path, 0, duration, ExportFormat.H264, 0, dest,
                p => progress(at == 0 ? p * 0.95 : 0.95 + p * 0.05), bitrate: (int)video);
            long size = FileSize(dest);
            if (size <= limit) return dest;
            video *= (double)limit / size * 0.9;
        }
        throw new RewindException($"Couldn't get it under {label}.");
    }

    /// %LOCALAPPDATA%\Rewind\Shared, cleared of files from earlier days. Uninstall removes it with the rest.
    static string CacheFolder()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind", "Shared");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir))
            try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f); } catch { }
        return dir;
    }

    /// The file itself, the way Explorer's Copy puts it there, so apps that take files attach it on paste.
    static bool Put(string path)
    {
        var files = new StringCollection { System.IO.Path.GetFullPath(path) };
        for (int i = 0; ; i++)
        {
            try { System.Windows.Clipboard.SetFileDropList(files); return true; }
            catch (System.Runtime.InteropServices.COMException e) when (i < 3) { Log.Error("clipboard busy", e); Thread.Sleep(60); }
            catch (Exception e) { Toast.Error("Couldn't use the clipboard", e); return false; }
        }
    }

    static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
