using System.Diagnostics;
using System.Windows.Threading;

namespace Rewind;

sealed class Controller
{
    public static Controller Shared { get; } = new();

    public bool ReplayActive { get; private set; }
    public bool Recording { get; private set; }
    public DateTime? RecordingStart { get; private set; }
    public bool IsCapturing { get; private set; }
    public string CaptureInfo { get; private set; } = "";
    public event Action? StateChanged;

    readonly Settings settings = Settings.Shared;
    public MediaRouter Router { get; } = new();
    readonly CaptureEngine engine = new();
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    CaptureSignature? runningSignature;
    Task opChain = Task.CompletedTask;
    readonly DispatcherTimer retargetTimer;
    Action? retargetWork;
    readonly DispatcherTimer appWatch;
    bool appWasRunning;
    int retargetAttempts;
    Task? finishing;

    Controller()
    {
        engine.OnVideo = Router.Video;
        engine.OnAudio = Router.Audio;
        engine.OnStop = e => ui.BeginInvoke(() => CaptureDied(e));
        engine.OnTargetClosed = () => ui.BeginInvoke(() => { if (!Recording) ScheduleRetarget(0.5, force: true); });
        Router.Replay.Duration = settings.ReplaySeconds;

        retargetTimer = new DispatcherTimer(DispatcherPriority.Normal, ui);
        retargetTimer.Tick += (_, _) => { retargetTimer.Stop(); var w = retargetWork; retargetWork = null; w?.Invoke(); };

        // Retarget when the chosen app opens or quits (Windows has no launch notifications without admin rights).
        appWatch = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => WatchApp(), ui);
        appWatch.Start();
    }

    void Changed() => StateChanged?.Invoke();

    void WatchApp()
    {
        if (settings.CaptureAppId.Length == 0) { appWasRunning = false; return; }
        bool running = AppTarget.IsRunning(settings.CaptureAppId);
        if (running == appWasRunning) return;
        appWasRunning = running;
        // Give a launching app a moment to open its window.
        ScheduleRetarget(running ? 3 : 0.5, force: false);
    }

    void ScheduleRetarget(double seconds, bool force)
    {
        retargetTimer.Stop();
        retargetWork = () =>
        {
            if (force && IsCapturing && !Recording) runningSignature = null;
            SettingsChanged();
        };
        retargetTimer.Interval = TimeSpan.FromSeconds(seconds);
        retargetTimer.Start();
    }


    public void Perform(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleOverlay: OverlayController.Toggle(); break;
            case HotkeyAction.SaveReplay: SaveReplay(); break;
            case HotkeyAction.ToggleReplay: ToggleReplay(); break;
            case HotkeyAction.ToggleRecording: ToggleRecording(); break;
            case HotkeyAction.Screenshot: Screenshot(); break;
        }
    }

    public void ToggleReplay() { if (ReplayActive) StopReplay(); else StartReplay(); }
    public void ToggleRecording() { if (Recording) StopRecording(); else StartRecording(); }

    static string Joined(string first, string hint) => hint.Length == 0 ? first : $"{first} · {hint}";

    AudioTrack[][] ReplayAudioGroups()
    {
        var tracks = new List<AudioTrack>();
        if (settings.SystemAudio) tracks.Add(AudioTrack.System);
        if (settings.Microphone) tracks.Add(AudioTrack.Mic);
        return ClipWriter.AudioTracks(tracks, settings.MixMicrophone);
    }

    public void StartReplay()
    {
        if (ReplayActive) return;
        Router.Replay.Clear();
        Router.Replay.Duration = settings.ReplaySeconds;
        Router.Replay.SetAudioGroups(ReplayAudioGroups());
        Router.BufferReplay = true;
        ReplayActive = true;
        Changed();
        Reconcile(ok =>
        {
            if (!ok || !ReplayActive) return;
            var what = engine.CapturedApp is { } app ? $"{app} · " : "";
            Toast.Show("Instant Replay is on",
                Joined($"{what}Keeping the last {Format.Duration(settings.ReplaySeconds)}", settings.HotkeyHint(HotkeyAction.SaveReplay, "to save")),
                Sym.Replay, Theme.Accent);
        });
    }

    public void StopReplay()
    {
        if (!ReplayActive) return;
        Router.BufferReplay = false;
        Router.Replay.Clear();
        ReplayActive = false;
        Changed();
        Toast.Show("Instant Replay is off", null, Sym.ReplayOff, Theme.Gray);
        Reconcile();
    }

    public async void SaveReplay()
    {
        if (!ReplayActive)
        {
            var hint = settings.HotkeyHint(HotkeyAction.ToggleReplay, "to turn it on");
            Toast.Show("Instant Replay is off", hint.Length == 0 ? "Turn it on from the overlay or the tray menu." : "Press " + hint,
                Sym.Warning, Theme.Orange);
            return;
        }
        var snap = Router.Replay.Snapshot();
        if (snap.Video.Count <= 1)
        {
            Toast.Show("Nothing to save yet", "The replay buffer is still empty.", Sym.Hourglass, Theme.Orange);
            return;
        }
        if (MakeOutputPath(NamePrefix("Replay"), "mp4") is not { } path) return;
        try
        {
            double length = await ClipWriter.WriteAsync(snap, path);
            ClipLibrary.RefreshIfShown();
            Toast.Show("Replay saved", $"{Format.Duration(length)} · {Path.GetFileName(path)}", Sym.Check, Theme.Accent,
                onClick: () => OverlayController.Show(path));
        }
        catch (Exception e)
        {
            Log.Error("save replay", e);
            Toast.Error("Couldn't save replay", e);
        }
    }

    public void StartRecording()
    {
        if (Recording || MakeOutputPath(NamePrefix("Recording"), "mp4") is not { } path) return;
        var tracks = new List<AudioTrack>();
        if (settings.SystemAudio) tracks.Add(AudioTrack.System);
        if (settings.Microphone) tracks.Add(AudioTrack.Mic);
        var rec = new LiveRecorder(path, tracks, settings.MixMicrophone) { RequestKeyframe = engine.ForceKeyframe };
        Router.Recorder = rec;
        Recording = true;
        RecordingStart = DateTime.Now;
        Changed();
        engine.ForceKeyframe();
        Reconcile(ok =>
        {
            if (!ok || !Recording) return;
            engine.ForceKeyframe();
            var hint = settings.HotkeyHint(HotkeyAction.ToggleRecording, "to stop");
            Toast.Show("Recording started", hint.Length == 0 ? null : hint, Sym.RecordFill, Theme.Record);
        });
    }

    public Task StopRecording()
    {
        if (!Recording || Router.Recorder is not { } rec) return finishing ?? Task.CompletedTask;
        Router.Recorder = null;
        Recording = false;
        RecordingStart = null;
        Changed();
        finishing = Finish(rec);
        Reconcile();
        return finishing;

        static async Task Finish(LiveRecorder rec)
        {
            try
            {
                double length = await rec.FinishAsync();
                ClipLibrary.RefreshIfShown();
                Toast.Show("Recording saved", $"{Format.Duration(length)} · {Path.GetFileName(rec.Path)}", Sym.Check, Theme.Accent,
                    onClick: () => OverlayController.Show(rec.Path));
            }
            catch (Exception e)
            {
                Log.Error("recording", e);
                Toast.Error("Recording failed", e);
            }
        }
    }

    public async void Screenshot()
    {
        if (MakeOutputPath(NamePrefix("Screenshot"), "png") is not { } path) return;
        try
        {
            // While capturing, the screenshot is the newest frame from before the overlay opened. Otherwise a
            // fresh capture would see a visible overlay, so close it first.
            if (overlayVisible && settings.ShareRewindWindows && !IsCapturing)
            {
                OverlayController.Hide();
                await Task.Delay(300);
            }
            await engine.ScreenshotAsync(settings.DisplayId, settings.CaptureAppId, settings.ShowCursor, path);
            ClipLibrary.RefreshIfShown();
            Toast.Show("Screenshot saved", Path.GetFileName(path), Sym.Camera, Theme.Screenshot, onClick: () => OverlayController.Show(path));
        }
        catch (Exception e)
        {
            Log.Error("screenshot", e);
            Toast.Error("Screenshot failed", e);
        }
    }

    public void OpenClipsFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{settings.OutputDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Toast.Error("Couldn't open the clips folder", e); }
    }

    public void SettingsChanged()
    {
        Router.Replay.Duration = settings.ReplaySeconds;
        var cleanupSig = settings.AutoCleanup ? $"{settings.CleanupDays}|{settings.OutputDirectory}" : "";
        if (cleanupSig != lastCleanupSig) { lastCleanupSig = cleanupSig; CleanUpOldClips(); }
        // Mixing the mic in (or not) changes how replay audio is encoded; the buffered audio starts over.
        if (ReplayActive) Router.Replay.SetAudioGroups(ReplayAudioGroups());
        UpdatePause();
        if (IsCapturing && runningSignature != settings.CaptureSignature) Reconcile();
    }

    string lastCleanupSig = "";
    DispatcherTimer? cleanupTimer;

    /// Checks now, then every hour while Rewind runs.
    public void StartCleanupSchedule()
    {
        lastCleanupSig = settings.AutoCleanup ? $"{settings.CleanupDays}|{settings.OutputDirectory}" : "";
        CleanUpOldClips();
        cleanupTimer ??= new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => CleanUpOldClips(), ui);
        cleanupTimer.Start();
    }

    public void CleanUpOldClips()
    {
        if (!settings.AutoCleanup) return;
        string dir = settings.OutputDirectory;
        int days = settings.CleanupDays;
        var prefixes = settings.AppPrefixes.ToList();
        var keep = Router.Recorder is { } r ? new[] { r.Path } : Array.Empty<string>();
        Task.Run(() =>
        {
            var res = Cleanup.Run(dir, days, DateTime.Now, keep, prefixes);
            if (res.Count == 0) return;
            Toast.Show($"Moved {res.Count} old {(res.Count == 1 ? "file" : "files")} to the Recycle Bin",
                $"Older than {days} {(days == 1 ? "day" : "days")} · {SizeText(res.Bytes)} freed once the bin is emptied",
                Sym.Trash, Theme.Gray);
        });
    }

    public static string SizeText(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F0} MB" : $"{Math.Max(bytes / 1024, 1)} KB";

    /// "Roblox" while capturing just that app, otherwise the usual "Replay" / "Recording" / "Screenshot".
    string NamePrefix(string fallback)
    {
        string? app = IsCapturing ? engine.CapturedApp
            : settings.CaptureAppId.Length > 0 && settings.CaptureAppName.Length > 0 && AppTarget.IsRunning(settings.CaptureAppId)
                ? settings.CaptureAppName : null;
        return app != null && settings.ClipPrefix(app) is { } prefix ? prefix : fallback;
    }

    bool overlayVisible;

    /// The overlay opened or closed. When screen shares may see it, a whole-display capture would record it
    /// too, so the video holds while it's open (capturing one app's window never includes it).
    public void OverlayChanged(bool visible)
    {
        overlayVisible = visible;
        UpdatePause();
    }

    void UpdatePause() =>
        engine.Paused = overlayVisible && settings.ShareRewindWindows && IsCapturing && engine.CapturedApp == null;

    public double BufferedSeconds => Router.Replay.BufferedSeconds;
    public long BufferBytes => Router.Replay.Bytes;


    void Reconcile(Action<bool>? completion = null)
    {
        var prev = opChain;
        opChain = Run();

        async Task Run()
        {
            await prev;
            bool ok;
            try { ok = await ReconcileNow(); }
            catch (Exception e) { Log.Error("reconcile", e); ok = false; }
            completion?.Invoke(ok);
        }
    }

    async Task<bool> ReconcileNow()
    {
        bool want = ReplayActive || Recording;
        var sig = settings.CaptureSignature;
        if (!want)
        {
            if (IsCapturing) { await engine.StopAsync(); MarkStopped(); }
            return true;
        }
        if (IsCapturing)
        {
            // Don't restart mid-recording; changed settings apply once it stops.
            if (sig == runningSignature || Recording) return true;
            var previous = runningSignature;
            await engine.StopAsync();
            MarkStopped();
            Router.Replay.Clear();
            if (previous?.AppRunning == sig.AppRunning || sig.AppId.Length == 0)
                Toast.Show("Capture settings updated", "The replay buffer was restarted.", Sym.Gear, Theme.Accent);
        }
        try
        {
            await engine.StartAsync(sig);
            IsCapturing = true;
            runningSignature = sig;
            CaptureInfo = engine.Info;
            UpdatePause();
            Changed();
            Log.Info("capturing: " + CaptureInfo);
            AnnounceTarget(sig);
            ReportWarnings();
            return true;
        }
        catch (Exception e)
        {
            Log.Error("start capture", e);
            Toast.Error("Couldn't start capture", e);
            FailAll();
            return false;
        }
    }

    void AnnounceTarget(CaptureSignature sig)
    {
        if (sig.AppId.Length == 0) return;
        var name = sig.AppName.Length == 0 ? "The chosen app" : sig.AppName;
        if (engine.CapturedApp is { } app)
        {
            retargetAttempts = 0;
            Toast.Show($"Capturing {app}", $"Only {app}'s window and sound are recorded.", Sym.App, Theme.Accent);
        }
        else if (sig.AppRunning)
        {
            if (retargetAttempts++ < 10) ScheduleRetarget(3, force: true);
            else retargetAttempts = 0;
        }
        else
        {
            Toast.Show($"{name} isn't running", "Capturing the whole display until it opens.", Sym.Display, Theme.Orange);
        }
    }

    void ReportWarnings()
    {
        var w = engine.Warnings.FirstOrDefault();
        if (w == null) return;
        if (w.Contains("access is off"))
            Toast.Show("Microphone access is off", "Click to open Windows privacy settings, allow desktop apps, then try again.",
                Sym.Lock, Theme.Orange, duration: 8, onClick: () => Native.OpenUrl("ms-settings:privacy-microphone"));
        else
            Toast.Show("Recording without some audio", w, Sym.Warning, Theme.Orange, duration: 6);
    }

    void MarkStopped()
    {
        IsCapturing = false;
        runningSignature = null;
        CaptureInfo = "";
        UpdatePause();
        Changed();
    }

    void CaptureDied(Exception error)
    {
        MarkStopped();
        Toast.Error("Capture stopped", error);
        FailAll();
    }

    void FailAll()
    {
        Router.BufferReplay = false;
        Router.Replay.Clear();
        ReplayActive = false;
        Changed();
        if (Recording) StopRecording();
    }

    string? MakeOutputPath(string prefix, string ext)
    {
        var dir = settings.OutputDirectory;
        try { Directory.CreateDirectory(dir); }
        catch (Exception e)
        {
            Toast.Error($"Can't write to {dir}", e);
            return null;
        }
        var b = $"{prefix} {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}";
        var path = Path.Combine(dir, $"{b}.{ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{b} ({n}).{ext}");
        return path;
    }

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("reveal", e); }
    }

    public async Task ShutdownAsync()
    {
        if (Recording) await Task.WhenAny(StopRecording(), Task.Delay(8000));
        else if (finishing != null) await Task.WhenAny(finishing, Task.Delay(8000));
        Router.BufferReplay = false;
        ReplayActive = false;
        try { await Task.WhenAny(engine.StopAsync(), Task.Delay(3000)); } catch { }
    }
}

static class Format
{
    public static string Duration(double s)
    {
        int t = (int)Math.Round(s);
        return t < 60 ? $"{t}s" : $"{t / 60}:{t % 60:00}";
    }
}
