using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Rewind;

sealed class CaptureEngine
{
    public Action<EncodedFrame>? OnVideo;
    public Action<AudioChunk, AudioTrack>? OnAudio;
    public Action<Exception>? OnStop;
    public Action? OnTargetClosed;

    readonly object gate = new();
    Session? current;

    public string Info { get; private set; } = "";
    public volatile bool Paused;
    public string? CapturedApp { get; private set; }
    public List<string> Warnings { get; } = new();

    /// One app's window (Window set) or a whole display (Monitor set).
    sealed record Target(GraphicsCaptureItem Item, string? AppName, uint Pid, IntPtr Window, IntPtr Monitor);

    static Target ResolveTarget(string displayId, string appId)
    {
        if (appId.Length > 0 && AppTarget.FindWindow(appId) is { } w)
        {
            try { return new Target(Gpu.ItemForWindow(w.Hwnd), w.Name, w.Pid, w.Hwnd, IntPtr.Zero); }
            catch (Exception e) { Log.Error("capture item for window", e); }
        }
        var d = Displays.Find(displayId) ?? throw new RewindException("No display found to capture.");
        return new Target(Gpu.ItemForMonitor(d.Handle), null, 0, IntPtr.Zero, d.Handle);
    }

    /// Displays, and apps while they're fullscreen, are captured with Desktop Duplication; see Duplicator.
    static bool Duplicates(Target t) => t.Window == IntPtr.Zero || Native.FillsMonitor(t.Window);

    public Task StartAsync(CaptureSignature sig) => Task.Run(() =>
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new RewindException("Screen capture isn't supported on this PC (Windows 10 version 2004 or later is required).");
        MF.Startup();
        Warnings.Clear();
        var s = new Session(this, sig);
        try { s.Start(); }
        catch { s.Dispose(); throw; }
        lock (gate) current = s;
        CapturedApp = s.AppName;
        Info = s.Info;
    });

    public Task StopAsync() => Task.Run(() =>
    {
        Session? s;
        lock (gate) { s = current; current = null; }
        s?.Dispose();
        Info = "";
        CapturedApp = null;
    });

    public void ForceKeyframe()
    {
        lock (gate) current?.ForceKeyframe();
    }

    public (long Arrived, long Accepted, long NoSurface, long EncoderDropped) Stats
    {
        get
        {
            lock (gate)
                return current is { } s ? (s.Arrived, s.Accepted, s.NoSurface, s.EncoderDropped) : default;
        }
    }

    void Died(Session s, Exception e)
    {
        lock (gate) { if (current != s) return; current = null; }
        Task.Run(s.Dispose);
        OnStop?.Invoke(e);
    }

    void TargetClosed(Session s)
    {
        lock (gate) { if (current != s) return; }
        OnTargetClosed?.Invoke();
    }

    sealed class Session : IDisposable
    {
        readonly CaptureEngine owner;
        readonly CaptureSignature sig;
        readonly object frameLock = new();
        Gpu? gpu;
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        FrameConverter? converter;
        VideoEncoder? encoder;
        readonly List<AudioCapture> audio = new();
        SizeInt32 poolSize, lastContent;
        long nextFrame = long.MinValue, interval;
        bool disposed, failed;
        // The newest frame stays checked out of the pool (it costs no copy) so a screenshot can read it.
        Direct3D11CaptureFrame? heldFrame;
        ID3D11Texture2D? heldTexture;
        const int PoolBuffers = 3;

        Target? target;
        Duplicator? dup;
        Thread? dupThread;
        ObsGameCapture? game;
        Thread? gameThread;
        bool cantDuplicate, closedReported;
        Timer? watcher;
        readonly object switchLock = new();

        public CaptureSignature Signature => sig;
        public long Arrived, Accepted, NoSurface;
        public long EncoderDropped => encoder?.Dropped ?? 0;

        public BitmapSource? LastFrame()
        {
            lock (frameLock)
            {
                if (disposed || failed) return null;
                if (game != null) return game.Own == null ? null : ReadBack(gpu!, game.Own, new SizeInt32 { Width = game.Width, Height = game.Height });
                if (dup != null) return dup.Canvas == null ? null : ReadBack(gpu!, dup.Canvas, new SizeInt32 { Width = dup.Width, Height = dup.Height });
                return heldTexture == null ? null : ReadBack(gpu!, heldTexture, lastContent);
            }
        }

        void Hold(Direct3D11CaptureFrame? frame, ID3D11Texture2D? tex)
        {
            heldTexture?.Dispose();
            heldFrame?.Dispose();
            heldFrame = frame;
            heldTexture = tex;
        }

        public string? AppName { get; private set; }
        public string Info { get; private set; } = "";

        public Session(CaptureEngine owner, CaptureSignature sig) { this.owner = owner; this.sig = sig; }

        public void Start()
        {
            gpu = Gpu.Create();
            target = ResolveTarget(sig.DisplayId, sig.AppId);
            item = target.Item;
            AppName = target.AppName;
            var native = (item.Size.Width, item.Size.Height);
            var size = CaptureMath.OutputSize(native, sig.Resolution, sig.Codec);
            int bitrate = CaptureMath.Bitrate(size, sig.Fps, sig.Quality, sig.Codec, sig.CustomMbps);
            // A custom bitrate is used exactly as typed, without the size tuning the presets get.
            var tuning = sig.Quality == Quality.Custom ? EncoderTuning.For(sig.Codec) with { BitrateFactor = 1 } : null;
            interval = Clock.Second / sig.Fps;

            converter = new FrameConverter(gpu, size.W, size.H, sig.Fps);
            encoder = new VideoEncoder(gpu, size.W, size.H, sig.Fps, sig.Codec, bitrate, f => owner.OnVideo?.Invoke(f), tuning);

            item.Closed += (_, _) => ReportClosed();

            // Audio. When capturing one app, record only that app's sound (and its child processes').
            if (sig.SystemAudio)
            {
                if (target.Pid != 0)
                {
                    try { audio.Add(AudioCapture.Start(AudioCapture.Source.Process, target.Pid, c => owner.OnAudio?.Invoke(c, AudioTrack.System))); }
                    catch (Exception e)
                    {
                        Log.Error("app audio", e);
                        owner.Warnings.Add("Couldn't record only the app's sound, so all system sound is recorded.");
                    }
                }
                if (audio.Count == 0)
                {
                    try { audio.Add(AudioCapture.Start(AudioCapture.Source.SystemLoopback, 0, c => owner.OnAudio?.Invoke(c, AudioTrack.System))); }
                    catch (Exception e) { Log.Error("system audio", e); owner.Warnings.Add("System audio: " + e.Message); }
                }
            }
            if (sig.Microphone)
            {
                try { audio.Add(AudioCapture.Start(AudioCapture.Source.Microphone, 0, c => owner.OnAudio?.Invoke(c, AudioTrack.Mic))); }
                catch (Exception e) { Log.Error("microphone", e); owner.Warnings.Add("Microphone: " + e.Message); }
            }

            if (!(Duplicates(target) && StartDuplication())) StartWgc();
            Log.Info(dup != null ? "capture: Desktop Duplication" : "capture: Windows.Graphics.Capture");
            if (sig.GameCapture && target.Window != IntPtr.Zero && target.Pid != 0) StartGameCapture();
            // An app can go fullscreen or back to a window at any time.
            if (target.Window != IntPtr.Zero) watcher = new Timer(_ => Watch(), null, 250, 250);
            var source = AppName ?? Displays.All().FirstOrDefault(d => d.Handle == target.Monitor)?.Short ?? "Display";
            Info = $"{source} · {size.W}×{size.H} · {sig.Fps} fps · {sig.Codec.Short()} · {encoder.Bitrate / 1_000_000} Mbps" +
                   (encoder.IsHardware ? "" : " · software");
        }

        public void ForceKeyframe() => encoder?.ForceKeyframe();

        void ReportClosed()
        {
            lock (switchLock)
            {
                if (closedReported) return;
                closedReported = true;
            }
            owner.TargetClosed(this);
        }

        void Watch()
        {
            if (!Monitor.TryEnter(switchLock)) return;
            try
            {
                if (disposed || failed || closedReported) return;
                var t = target!;
                if (!Native.IsWindow(t.Window)) { ReportClosed(); return; }
                if (game != null) return; // game capture doesn't care whether the game is fullscreen
                bool want = !cantDuplicate && Duplicates(t);
                if (want && dup == null)
                {
                    StopWgc();
                    if (StartDuplication()) Log.Info("capture: the app went fullscreen, switched to Desktop Duplication");
                    else StartWgc();
                }
                else if (want && dup != null && dup.Monitor != Native.MonitorOf(t.Window))
                {
                    StopDuplication();
                    if (!StartDuplication()) StartWgc();
                }
                else if (!want && dup != null)
                {
                    StartWgc();
                    StopDuplication();
                    Log.Info("capture: the app left fullscreen, switched to window capture");
                }
            }
            catch (Exception e)
            {
                Log.Error("capture switch", e);
            }
            finally { Monitor.Exit(switchLock); }
        }

        void StartWgc()
        {
            var size = item!.Size;
            var p = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu!.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
            p.FrameArrived += (fp, _) => OnFrame(fp, p);
            var s = p.CreateCaptureSession(item);
            Configure(s, sig.ShowCursor, sig.Fps);
            lock (frameLock) { poolSize = size; pool = p; session = s; }
            s.StartCapture();
        }

        void StopWgc()
        {
            GraphicsCaptureSession? s;
            Direct3D11CaptureFramePool? p;
            lock (frameLock) { s = session; p = pool; session = null; pool = null; Hold(null, null); }
            try { s?.Dispose(); } catch { }
            try { p?.Dispose(); } catch { }
        }

        bool StartDuplication()
        {
            if (cantDuplicate) return false;
            var t = target!;
            var monitor = t.Window != IntPtr.Zero ? Native.MonitorOf(t.Window) : t.Monitor;
            var d = Duplicator.TryCreate(gpu!, monitor, sig.ShowCursor, out var why);
            if (d == null)
            {
                cantDuplicate = true;
                Log.Info("Desktop Duplication isn't available (" + why + "), using Windows.Graphics.Capture");
                return false;
            }
            lock (frameLock) dup = d;
            var th = new Thread(() => DuplicationLoop(d)) { IsBackground = true, Name = "Desktop Duplication", Priority = ThreadPriority.AboveNormal };
            dupThread = th;
            th.Start();
            return true;
        }

        void StopDuplication()
        {
            Duplicator? d;
            lock (frameLock) { d = dup; dup = null; }
            var th = dupThread;
            dupThread = null;
            if (th != null && th != Thread.CurrentThread) th.Join(2000);
            lock (frameLock) d?.Dispose();
        }

        void DuplicationLoop(Duplicator d)
        {
            // Only the app's own picture is wanted: while it's in the background, other windows are on its screen.
            uint appPid = target!.Window != IntPtr.Zero ? target.Pid : 0;
            while (true)
            {
                bool got;
                long pts;
                bool changed;
                try { got = d.Acquire(out pts, out changed); }
                catch (Exception e) { Fail(e); return; }
                lock (frameLock)
                {
                    if (disposed || failed || dup != d) { d.Release(); return; }
                    if (!got)
                    {
                        if (d.Broken) { d.Release(); ThreadPool.QueueUserWorkItem(_ => FallBackToWgc(d)); return; }
                        continue;
                    }
                    try
                    {
                        Arrived++;
                        bool skip = !changed || owner.Paused || (appPid != 0 && !Native.ProcessIsForeground(appPid))
                                    || (nextFrame != long.MinValue && pts < nextFrame - interval / 8);
                        if (skip) { d.Release(); continue; }
                        nextFrame = nextFrame == long.MinValue || pts - nextFrame > interval ? pts + interval : nextFrame + interval;

                        Accepted++;
                        d.Render();
                        d.Release();
                        var sample = converter!.Convert(d.Canvas!, d.Width, d.Height);
                        if (sample != null) encoder!.Encode(sample, pts);
                        else NoSurface++;
                    }
                    catch (Exception e)
                    {
                        d.Release();
                        Fail(e);
                        return;
                    }
                }
            }
        }

        /// Game capture (opt-in) for a chosen app, through OBS's hook. The usual capture keeps recording until the hook
        /// has shared its first frame, so trying it never leaves a gap in the replay; if the game can't be hooked,
        /// nothing changes.
        void StartGameCapture()
        {
            if (!ObsGameCapture.Available)
            {
                owner.Warnings.Add("Game capture needs OBS Studio installed, so window capture is used.");
                return;
            }
            var t = target!;
            var th = new Thread(() => TryGame(t)) { IsBackground = true, Name = "Game capture", Priority = ThreadPriority.AboveNormal };
            gameThread = th;
            th.Start();
        }

        void TryGame(Target t)
        {
            string name = AppName ?? "this app";
            ObsGameCapture? g;
            string why;
            try { g = ObsGameCapture.TryStart(gpu!, t.Pid, t.Window, sig.Fps, out why); }
            catch (Exception e) { Log.Error("game capture", e); g = null; why = e.Message; }
            if (g == null)
            {
                if (disposed || failed) return;
                Log.Info("game capture: " + why);
                Toast.Show($"Game capture can't hook {name}", why + " Rewind keeps recording its window instead.", Sym.Warning, Theme.Orange, duration: 7);
                return;
            }
            lock (switchLock)
            {
                if (disposed || failed) { g.Dispose(); return; }
                lock (frameLock) game = g;
                StopDuplication();
                StopWgc();
            }
            Log.Info($"capture: switched to game capture ({g.Width}×{g.Height})");
            GameLoop(g);
        }

        /// OBS's hook keeps the shared texture up to date; like OBS, Rewind reads it once per recorded frame.
        void GameLoop(ObsGameCapture g)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long next = 0;
            while (true)
            {
                long wait = next - clock.Elapsed.Ticks;
                if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(Math.Min(wait, Clock.Second / 100)));
                if (clock.Elapsed.Ticks < next) continue;
                next = clock.Elapsed.Ticks + interval;
                lock (frameLock)
                {
                    if (disposed || failed || game != g) return;
                    if (g.GameGone)
                    {
                        ThreadPool.QueueUserWorkItem(_ => FallBackFromGame(g, "The game stopped its capture."));
                        return;
                    }
                    if (owner.Paused) continue;
                    try
                    {
                        Arrived++;
                        var tex = g.Copy();
                        if (tex == null) { NoSurface++; continue; }
                        Accepted++;
                        var sample = converter!.Convert(tex, g.Width, g.Height);
                        if (sample != null) encoder!.Encode(sample, Clock.Now);
                        else NoSurface++;
                    }
                    catch (Exception e)
                    {
                        Log.Error("game capture frame", e);
                        ThreadPool.QueueUserWorkItem(_ => FallBackFromGame(g, e.Message));
                        return;
                    }
                }
            }
        }

        void FallBackFromGame(ObsGameCapture g, string why)
        {
            lock (switchLock)
            {
                if (disposed || failed || game != g) return;
                Log.Info("game capture stopped: " + why);
                StopGame();
                try { if (!(Duplicates(target!) && StartDuplication())) StartWgc(); }
                catch (Exception e) { Fail(e); return; }
                if (Native.IsWindow(target!.Window))
                    Toast.Show($"Game capture stopped for {AppName ?? "this app"}", why + " Recording its window instead.", Sym.Warning, Theme.Orange, duration: 7);
            }
        }

        void StopGame()
        {
            ObsGameCapture? g;
            lock (frameLock) { g = game; game = null; }
            var th = gameThread;
            gameThread = null;
            if (th != null && th != Thread.CurrentThread) th.Join(1000);
            lock (frameLock) g?.Dispose();
        }

        void FallBackToWgc(Duplicator d)
        {
            lock (switchLock)
            {
                if (disposed || failed || dup != d) return;
                cantDuplicate = true;
                Log.Info("Desktop Duplication stopped working, using Windows.Graphics.Capture");
                StopDuplication();
                try { StartWgc(); }
                catch (Exception e) { Fail(e); }
            }
        }

        void Fail(Exception e)
        {
            lock (frameLock)
            {
                if (failed) return;
                failed = true;
            }
            Log.Error("capture frame", e);
            owner.Died(this, e);
        }

        void OnFrame(Direct3D11CaptureFramePool p, Direct3D11CaptureFramePool mine)
        {
            lock (frameLock)
            {
                if (disposed || failed || !ReferenceEquals(pool, mine)) return;
                try
                {
                    var frame = p.TryGetNextFrame();
                    if (frame == null) return;
                    Arrived++;
                    if (owner.Paused) { frame.Dispose(); return; }
                    long pts = frame.SystemRelativeTime.Ticks;
                    var content = frame.ContentSize;
                    if (content.Width != poolSize.Width || content.Height != poolSize.Height)
                    {
                        // The window was resized; grow/shrink the pool. The output size stays fixed (letterboxed).
                        poolSize = new SizeInt32 { Width = Math.Max(content.Width, 1), Height = Math.Max(content.Height, 1) };
                        Hold(null, null);
                        p.Recreate(gpu!.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, poolSize);
                    }
                    if (nextFrame != long.MinValue && pts < nextFrame - interval / 8) { frame.Dispose(); return; }
                    nextFrame = nextFrame == long.MinValue || pts - nextFrame > interval ? pts + interval : nextFrame + interval;

                    Accepted++;
                    var tex = Gpu.TextureFromSurface(frame.Surface);
                    var sample = converter!.Convert(tex, content.Width, content.Height);
                    lastContent = content;
                    Hold(frame, tex);
                    if (sample != null) encoder!.Encode(sample, pts);
                    else NoSurface++;
                }
                catch (Exception e)
                {
                    failed = true;
                    Log.Error("capture frame", e);
                    owner.Died(this, e);
                }
            }
        }

        public void Dispose()
        {
            watcher?.Dispose();
            lock (switchLock)
            {
                foreach (var a in audio) a.Dispose();
                audio.Clear();
                lock (frameLock) disposed = true;
                StopGame();
                StopDuplication();
                StopWgc();
                encoder?.Dispose();
                converter?.Dispose();
                gpu?.Dispose();
                encoder = null; converter = null; gpu = null;
            }
        }
    }

    static void Configure(GraphicsCaptureSession session, bool showCursor, int fps = 0)
    {
        try { session.IsCursorCaptureEnabled = showCursor; } catch { }
        // Windows 11 24H2: have Windows skip frames beyond the chosen rate (e.g. on 144 Hz screens) instead of
        // making them only for Rewind to drop. A little under the frame time, so a 60 Hz screen still gives 60.
        if (fps > 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            try { session.MinUpdateInterval = TimeSpan.FromTicks(Clock.Second / fps * 3 / 4); } catch { }
        }
        // Windows 11: no yellow capture border.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try { session.IsBorderRequired = false; } catch { }
        }
        // Windows 11 24H2: window capture also gets the app's menus and pop-ups.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            try { session.IncludeSecondaryWindows = true; } catch { }
        }
    }

    static int borderlessRequested;

    public static async Task RequestBorderlessAsync()
    {
        if (Interlocked.Exchange(ref borderlessRequested, 1) != 0) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); }
        catch (Exception e) { Log.Info("borderless capture access: " + e.Message); }
    }


    public Task ScreenshotAsync(string displayId, string appId, bool showCursor, string path) => Task.Run(async () =>
    {
        Session? live;
        lock (gate) live = current;
        var bmp = live != null && live.Signature.DisplayId == displayId && live.Signature.AppId == appId && live.Signature.ShowCursor == showCursor
            ? live.LastFrame()
            : null;
        bmp ??= await CaptureOnce(displayId, appId, showCursor);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var tmp = path + ".part";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, overwrite: true);
    });

    static async Task<BitmapSource> CaptureOnce(string displayId, string appId, bool showCursor)
    {
        MF.Startup();
        using var gpu = Gpu.Create();
        var target = ResolveTarget(displayId, appId);
        bool app = target.Window != IntPtr.Zero;
        if (Duplicates(target) && (!app || Native.ProcessIsForeground(target.Pid))
            && DuplicateOnce(gpu, app ? Native.MonitorOf(target.Window) : target.Monitor, showCursor) is { } shot)
            return shot;
        var size = target.Item.Size;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            try
            {
                using var frame = p.TryGetNextFrame();
                if (frame == null) return;
                using var tex = Gpu.TextureFromSurface(frame.Surface);
                tcs.TrySetResult(ReadBack(gpu, tex, frame.ContentSize));
            }
            catch (Exception e) { tcs.TrySetException(e); }
        };
        using var session = pool.CreateCaptureSession(target.Item);
        Configure(session, showCursor);
        session.StartCapture();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
        if (done != tcs.Task) throw new RewindException("The screen didn't send a frame. Try again, or turn on Instant Replay.");
        return await tcs.Task;
    }

    static BitmapSource? DuplicateOnce(Gpu gpu, IntPtr monitor, bool showCursor)
    {
        using var d = Duplicator.TryCreate(gpu, monitor, showCursor, out var why);
        if (d == null) { Log.Info("screenshot: Desktop Duplication isn't available (" + why + ")"); return null; }
        // The first frame after starting has the whole screen.
        for (var end = DateTime.UtcNow.AddSeconds(2); DateTime.UtcNow < end;)
        {
            if (!d.Acquire(out _, out _)) continue;
            try
            {
                d.Render();
                return ReadBack(gpu, d.Canvas!, new SizeInt32 { Width = d.Width, Height = d.Height });
            }
            finally { d.Release(); }
        }
        return null;
    }

    static BitmapSource ReadBack(Gpu gpu, ID3D11Texture2D tex, SizeInt32 content)
    {
        var desc = tex.Description;
        int w = Math.Min(content.Width, (int)desc.Width), h = Math.Min(content.Height, (int)desc.Height);
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        gpu.Context.CopyResource(staging, tex);
        var map = gpu.Context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, map.DataPointer,
                (int)(map.RowPitch * (h - 1) + w * 4), (int)map.RowPitch);
            bmp.Freeze();
            return bmp;
        }
        finally { gpu.Context.Unmap(staging, 0); }
    }
}
