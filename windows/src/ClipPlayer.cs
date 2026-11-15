using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rewind;

/// Plays a clip with Media Foundation's Media Engine (what the Media Player app and Edge use) and copies its
/// frames into a WPF bitmap. WPF's own MediaElement needs the legacy Windows Media Player, which many
/// Windows 11 PCs don't have. Times count from `startOffset` into the file, where a trimmed clip really starts:
/// Media Engine ignores the edit list that says so, though it does take the length from it.
sealed class ClipPlayer : IDisposable
{
    public WriteableBitmap? Frame { get; private set; }
    public double Duration { get; private set; }
    public int FramesShown { get; private set; }
    public long ShownPts => shownPts;
    public event Action? Opened, Ended;
    public event Action<string>? Failed;

    readonly Gpu gpu;
    readonly IMFMediaEngine engine;
    readonly MediaEngineNotifyDelegate notify;
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    readonly int maxW, maxH;
    readonly double offset;
    readonly double? knownDuration;
    ID3D11Texture2D? target, staging;
    int w, h;
    long shownPts = -1;
    bool disposed;

    public ClipPlayer(string path, int maxWidth, int maxHeight, double startOffset = 0, double? duration = null, bool muted = false)
    {
        offset = Math.Max(startOffset, 0);
        knownDuration = duration;
        maxW = Math.Max(maxWidth, 2);
        maxH = Math.Max(maxHeight, 2);
        notify = OnEvent;
        gpu = Gpu.Create();
        try
        {
            using var attrs = MediaFactory.MFCreateAttributes(2);
            attrs.Set(MediaEngineAttributeKeys.DxgiManager.Guid, (SharpGen.Runtime.IUnknown)gpu.Manager);
            attrs.Set(MediaEngineAttributeKeys.VideoOutputFormat.Guid, (uint)Vortice.DXGI.Format.B8G8R8A8_UNorm);
            using var factory = new IMFMediaEngineClassFactory();
            engine = factory.CreateInstance(MediaEngineCreateFlags.None, attrs, notify);
            engine.Muted = muted;
            engine.SetSource(path).CheckError();
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    public bool Playing => !disposed && !engine.IsPaused && !engine.IsEnded;
    public double Position
    {
        get => disposed ? 0 : Math.Max(engine.CurrentTime - offset, 0);
        set { if (!disposed) engine.CurrentTime = Math.Clamp(value, 0, Math.Max(Duration, 0)) + offset; }
    }

    public void Play() { if (!disposed) engine.Play(); }
    public void Pause() { if (!disposed) engine.Pause(); }
    /// Moves `frames` frames from the current one. Media Engine's own FrameStep skips ahead after a seek and
    /// can't go back, so this seeks instead.
    public void Step(int frames, double fps)
    {
        if (disposed || fps <= 0) return;
        Position = (Math.Round(Position * fps) + frames) / fps;
    }

    // Media Engine calls this on its own threads.
    void OnEvent(MediaEngineEvent e, nuint param1, int param2)
    {
        if (e is MediaEngineEvent.LoadedMetadata or MediaEngineEvent.DurationChange or MediaEngineEvent.Ended or MediaEngineEvent.Error)
            ui.BeginInvoke(() => Handle(e, param1, param2));
    }

    void Handle(MediaEngineEvent e, nuint param1, int param2)
    {
        if (disposed) return;
        switch (e)
        {
            case MediaEngineEvent.LoadedMetadata:
                Duration = knownDuration ?? Finite(engine.Duration);
                if (offset > 0) engine.CurrentTime = offset;
                try { CreateTargets(); }
                catch (Exception x) { Log.Error("clip player", x); Failed?.Invoke("Couldn't set up video playback."); return; }
                Opened?.Invoke();
                break;
            case MediaEngineEvent.DurationChange:
                Duration = knownDuration ?? Finite(engine.Duration);
                break;
            case MediaEngineEvent.Ended:
                Ended?.Invoke();
                break;
            case MediaEngineEvent.Error:
                var err = (MediaEngineErr)param1;
                Log.Info($"clip player: {err} 0x{param2:X8}");
                Failed?.Invoke(err == MediaEngineErr.SrcNotSupported
                    ? "Windows can't play this clip. HEVC clips need the HEVC Video Extensions from the Microsoft Store."
                    : $"Couldn't play this clip ({err}).");
                break;
        }
    }

    static double Finite(double d) => double.IsFinite(d) ? d : 0;

    void CreateTargets()
    {
        var native = engine.NativeVideoSize;
        if (native.Width <= 0 || native.Height <= 0) return;
        double scale = Math.Min(1, Math.Min(maxW / (double)native.Width, maxH / (double)native.Height));
        w = Math.Max((int)Math.Round(native.Width * scale), 2);
        h = Math.Max((int)Math.Round(native.Height * scale), 2);
        var desc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        target = gpu.Device.CreateTexture2D(desc);
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        staging = gpu.Device.CreateTexture2D(desc);
        Frame = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
    }

    /// Copies a new frame into `Frame` if there is one; call on every screen refresh.
    public bool Update()
    {
        if (disposed || target == null || staging == null || Frame == null) return false;
        try
        {
            if (!engine.OnVideoStreamTick(out long pts) || pts == shownPts) return false;
            shownPts = pts;
            engine.TransferVideoFrame(target, null, new Vortice.RawRect(0, 0, w, h), null);
            gpu.Context.CopyResource(staging, target);
            var map = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try { Frame.WritePixels(new Int32Rect(0, 0, w, h), map.DataPointer, (int)(map.RowPitch * h), (int)map.RowPitch); }
            finally { gpu.Context.Unmap(staging, 0); }
            FramesShown++;
            return true;
        }
        catch (Exception e)
        {
            Log.Error("clip frame", e);
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { engine.Shutdown(); } catch { }
        engine.Dispose();
        target?.Dispose();
        staging?.Dispose();
        gpu.Dispose();
    }
}
