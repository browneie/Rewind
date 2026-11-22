using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;

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
        // Shrunk before (and the clip hasn't changed since): that copy is ready.
        if (Shrunk(path, limit) is { } ready)
        {
            if (Put(ready))
                Toast.Show("Copied to the clipboard",
                    $"Shrunk to {Format.Bytes(FileSize(ready))} (was {Format.Bytes(size)}) to fit {Label(mb)}. The original is kept.",
                    Sym.Copy, Theme.Accent, duration: 5);
            return;
        }
        // Nothing older on the clipboard may be pasted while this one is being made.
        try { System.Windows.Clipboard.Clear(); } catch { }
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
        // Only the clip's own sound (the first track) goes along: what's pasted plays that one anyway.
        double audio = 0;
        foreach (var a in r.AudioTracks.Take(1))
        {
            double rate = a.MediaDuration > 0 ? a.Sizes.Sum(x => (long)x) * 8 / a.MediaDuration : 0;
            audio += rate > 0 ? rate : 192_000;
        }
        // 4% for the MP4 container, and a little room for the encoder's average drifting.
        double video = limit * 8 * 0.92 / duration - audio;
        string label = Label((int)(limit / 1_000_000));
        if (video < 150_000)
            throw new RewindException($"It's too long to fit in {label}. Trim it first, or raise the limit in Settings → Saving.");

        var dest = Destination(path); // same name, so it shows up nicely where it's pasted
        // Made under another name and renamed when done, so the clip's name never stands for a half-written file.
        var work = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, "making " + System.IO.Path.GetFileName(dest));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (File.Exists(work)) File.Delete(work);
                int at = attempt;
                ClipConverter.Convert(path, 0, duration, ExportFormat.H264, 0, work,
                    p => progress(at == 0 ? p * 0.95 : 0.95 + p * 0.05), bitrate: (int)video, audio: r.AudioTracks.Any() ? new[] { 0 } : null);
                long size = FileSize(work);
                if (size > 0 && size <= limit)
                {
                    File.Move(work, dest, overwrite: true);
                    return dest;
                }
                if (size == 0) throw new RewindException("Shrinking the clip didn't produce any video.");
                video *= (double)limit / size * 0.9;
            }
        }
        finally { try { File.Delete(work); } catch { } }
        throw new RewindException($"Couldn't get it under {label}.");
    }

    static string Destination(string path) => System.IO.Path.Combine(CacheFolder(), System.IO.Path.GetFileName(path));

    /// A finished shrunk copy of `path` that fits `limit` and is newer than the clip, or null.
    static string? Shrunk(string path, long limit)
    {
        var dest = Destination(path);
        long size = FileSize(dest);
        return size > 0 && size <= limit && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(path) ? dest : null;
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

    /// The file itself, exactly the way Explorer's Copy puts it there (Windows' own data object for the file, with
    /// the shell's file list, file contents and "copy" as the drop effect), so Discord and other apps attach it on
    /// paste. A bare file list (WPF's SetFileDropList) gets pasted into Discord as an empty file.
    static bool Put(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        for (int i = 0; ; i++)
        {
            try { PutShellItem(full); return true; }
            catch (Exception e) when (i < 3) { Log.Error("clipboard", e); Thread.Sleep(60); }
            catch (Exception e)
            {
                // Without the shell's data object, the plain file list still works in most apps.
                try { System.Windows.Clipboard.SetFileDropList(new StringCollection { full }); return true; }
                catch { Toast.Error("Couldn't use the clipboard", e); return false; }
            }
        }
    }

    static void PutShellItem(string path)
    {
        var shellItem = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItem, out var item);
        try
        {
            var bhid = BhidDataObject;
            var iid = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var dataPtr));
            try
            {
                var data = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(dataPtr);
                SetDropEffectCopy(data);
                Marshal.ThrowExceptionForHR(OleSetClipboard(dataPtr));
                // Renders everything onto the clipboard now, so it stays after Rewind lets go of the object.
                Marshal.ThrowExceptionForHR(OleFlushClipboard());
            }
            finally { Marshal.Release(dataPtr); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// "Preferred DropEffect" = copy, as Explorer's Copy sets it (Cut would be move).
    static void SetDropEffectCopy(System.Runtime.InteropServices.ComTypes.IDataObject data)
    {
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat("Preferred DropEffect"), dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };
        var memory = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(memory, 1); // DROPEFFECT_COPY
        var medium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM { tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL, unionmember = memory };
        try { data.SetData(ref format, ref medium, true); }
        catch { Marshal.FreeHGlobal(memory); } // optional: the file list alone already pastes
    }

    static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("ole32.dll")] static extern int OleSetClipboard(IntPtr dataObject);
    [DllImport("ole32.dll")] static extern int OleFlushClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);

    static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
