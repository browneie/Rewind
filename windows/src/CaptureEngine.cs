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

    sealed record Target(GraphicsCaptureItem Item, string? AppName, uint Pid);

    static Target ResolveTarget(string displayId, string appId)
    {
        if (appId.Length > 0 && AppTarget.FindWindow(appId) is { } w)
        {
            try { return new Target(Gpu.ItemForWindow(w.Hwnd), w.Name, w.Pid); }
            catch (Exception e) { Log.Error("capture item for window", e); }
        }
        var d = Displays.Find(displayId) ?? throw new RewindException("No display found to capture.");
        return new Target(Gpu.ItemForMonitor(d.Handle), null, 0);
    }

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

        public CaptureSignature Signature => sig;
        public long Arrived, Accepted, NoSurface;
        public long EncoderDropped => encoder?.Dropped ?? 0;

        public BitmapSource? LastFrame()
        {
            lock (frameLock)
            {
                if (disposed || failed || heldTexture == null) return null;
                return ReadBack(gpu!, heldTexture, lastContent);
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
            var target = ResolveTarget(sig.DisplayId, sig.AppId);
            item = target.Item;
            AppName = target.AppName;
            var native = (item.Size.Width, item.Size.Height);
            var size = CaptureMath.OutputSize(native, sig.Resolution, sig.Codec);
            int bitrate = CaptureMath.Bitrate(size, sig.Fps, sig.Quality, sig.Codec);
            interval = Clock.Second / sig.Fps;

            converter = new FrameConverter(gpu, size.W, size.H, sig.Fps);
            encoder = new VideoEncoder(gpu, size.W, size.H, sig.Fps, sig.Codec, bitrate, f => owner.OnVideo?.Invoke(f));

            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, poolSize);
            pool.FrameArrived += (p, _) => OnFrame(p);
            item.Closed += (_, _) => owner.TargetClosed(this);
            session = pool.CreateCaptureSession(item);
            Configure(session, sig.ShowCursor, sig.Fps);

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

            session.StartCapture();
            Info = (AppName != null ? $"{AppName} · " : "") +
                   $"{size.W}×{size.H} · {sig.Fps} fps · {sig.Codec.Short()} · {encoder.Bitrate / 1_000_000} Mbps" +
                   (encoder.IsHardware ? "" : " · software");
        }

        public void ForceKeyframe() => encoder?.ForceKeyframe();

        void OnFrame(Direct3D11CaptureFramePool p)
        {
            lock (frameLock)
            {
                if (disposed || failed) return;
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
            foreach (var a in audio) a.Dispose();
            audio.Clear();
            try { session?.Dispose(); } catch { }
            lock (frameLock) { disposed = true; Hold(null, null); }
            try { pool?.Dispose(); } catch { }
            encoder?.Dispose();
            converter?.Dispose();
            gpu?.Dispose();
            session = null; pool = null; encoder = null; converter = null; gpu = null;
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
