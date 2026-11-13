using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Rewind;

sealed class Gpu : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IMFDXGIDeviceManager Manager { get; }
    public IDirect3DDevice WinRTDevice { get; }
    public string AdapterName { get; }

    Gpu(ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager manager, IDirect3DDevice winrt, string adapter)
    {
        Device = device; Context = context; Manager = manager; WinRTDevice = winrt; AdapterName = adapter;
    }

    public static Gpu Create()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out var device, out var context).CheckError();
        using (var mt = device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();

        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        string adapterName = adapter.Description.Description;
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        var winrt = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        return new Gpu(device, context!, manager, winrt, adapterName);
    }

    public void Dispose()
    {
        (WinRTDevice as IDisposable)?.Dispose();
        Manager.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }


    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    public static GraphicsCaptureItem ItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, forWindow: false);
    public static GraphicsCaptureItem ItemForWindow(IntPtr hwnd) => CreateItem(hwnd, forWindow: true);

    /// IGraphicsCaptureItemInterop::CreateForWindow / CreateForMonitor, called through the vtable.
    static unsafe GraphicsCaptureItem CreateItem(IntPtr handle, bool forWindow)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        IntPtr factory;
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, IID_IGraphicsCaptureItemInterop, out factory)); }
        finally { WindowsDeleteString(hs); }
        try
        {
            var vtbl = *(IntPtr**)factory;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[forWindow ? 3 : 4];
            Guid iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(fn(factory, handle, &iid, &item));
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }

    public static unsafe ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        var unk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            var vtbl = *(IntPtr**)access;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
            Guid iid = typeof(ID3D11Texture2D).GUID;
            IntPtr tex;
            Marshal.ThrowExceptionForHR(fn(access, &iid, &tex));
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }
}

sealed class FrameConverter : IDisposable
{
    readonly Gpu gpu;
    readonly ID3D11VideoDevice videoDevice;
    readonly ID3D11VideoContext1 videoContext;
    readonly IMFVideoSampleAllocatorEx allocator;
    readonly int outW, outH;
    readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorOutputView> outputViews = new();

    // The video processor reads the capture surfaces directly (no copy). Windows reuses a few surfaces, so
    // their input views are cached. If a driver won't make views on them, frames are copied into `copy` first.
    readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> inputViews = new();
    bool direct = true;
    ID3D11Texture2D? copy;
    ID3D11VideoProcessorInputView? copyView;
    ID3D11VideoProcessorEnumerator? vpEnum;
    ID3D11VideoProcessor? vp;
    int inW, inH;

    public FrameConverter(Gpu gpu, int width, int height, int fps)
    {
        this.gpu = gpu;
        outW = width; outH = height;
        videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        videoContext = gpu.Context.QueryInterface<ID3D11VideoContext1>();

        var ptr = MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID);
        allocator = new IMFVideoSampleAllocatorEx(ptr);
        allocator.SetDirectXManager(gpu.Manager);
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.RenderTarget | BindFlags.VideoEncoder));
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        type.Set(MediaTypeAttributeKeys.FrameSize, MF.Pack(width, height));
        type.Set(MediaTypeAttributeKeys.FrameRate, MF.Pack(fps, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        allocator.InitializeSampleAllocatorEx(4, 12, attrs, type);
    }

    void EnsureProcessor(int w, int h)
    {
        if (vp != null && w == inW && h == inH) return;
        DisposeViews();
        vp?.Dispose(); vpEnum?.Dispose();
        inW = w; inH = h;
        vpEnum = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)w, InputHeight = (uint)h,
            OutputWidth = (uint)outW, OutputHeight = (uint)outH,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        vp = videoDevice.CreateVideoProcessor(vpEnum, 0);
        videoContext.VideoProcessorSetStreamFrameFormat(vp, 0, VideoFrameFormat.Progressive);
        videoContext.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
        videoContext.VideoProcessorSetStreamColorSpace1(vp, 0, ColorSpaceType.RgbFullG22NoneP709);
        videoContext.VideoProcessorSetOutputColorSpace1(vp, ColorSpaceType.YcbcrStudioG22LeftP709);
        videoContext.VideoProcessorSetOutputBackgroundColor(vp, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
    }

    ID3D11VideoProcessorOutputView OutputView(ID3D11Texture2D tex, uint subresource)
    {
        var key = (tex.NativePointer, subresource);
        if (outputViews.TryGetValue(key, out var view)) return view;
        var desc = tex.Description;
        var vdesc = desc.ArraySize > 1
            ? new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayVideoProcessorOutputView { MipSlice = 0, FirstArraySlice = subresource, ArraySize = 1 },
            }
            : new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
        view = videoDevice.CreateVideoProcessorOutputView(tex, vpEnum!, vdesc);
        outputViews[key] = view;
        return view;
    }

    static readonly VideoProcessorInputViewDescription InputViewDesc = new()
    {
        FourCC = 0,
        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
    };

    public IMFSample? Convert(ID3D11Texture2D source, int contentW, int contentH)
    {
        var sd = source.Description;
        EnsureProcessor((int)sd.Width, (int)sd.Height);
        return ConvertInput(InputFor(source, sd), Math.Min(contentW, inW), Math.Min(contentH, inH));
    }

    ID3D11VideoProcessorInputView InputFor(ID3D11Texture2D source, Texture2DDescription sd)
    {
        if (direct)
        {
            if (inputViews.TryGetValue(source.NativePointer, out var cached)) return cached;
            try
            {
                if (inputViews.Count >= 16) DisposeInputViews();
                var view = videoDevice.CreateVideoProcessorInputView(source, vpEnum!, InputViewDesc);
                inputViews[source.NativePointer] = view;
                return view;
            }
            catch (Exception e)
            {
                Log.Info("video processor can't read capture surfaces directly, copying frames instead: " + e.Message);
                direct = false;
            }
        }
        if (copy == null)
        {
            copy = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = sd.Width, Height = sd.Height, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            copyView = videoDevice.CreateVideoProcessorInputView(copy, vpEnum!, InputViewDesc);
        }
        gpu.Context.CopyResource(copy, source);
        return copyView!;
    }

    public IMFSample? ConvertOwned(ID3D11Texture2D bgra) => Convert(bgra, (int)bgra.Description.Width, (int)bgra.Description.Height);

    void DisposeInputViews()
    {
        foreach (var v in inputViews.Values) v.Dispose();
        inputViews.Clear();
    }

    void DisposeViews()
    {
        DisposeInputViews();
        copyView?.Dispose(); copyView = null;
        copy?.Dispose(); copy = null;
        foreach (var v in outputViews.Values) v.Dispose();
        outputViews.Clear();
    }

    IMFSample? ConvertInput(ID3D11VideoProcessorInputView input, int cw, int ch)
    {
        IMFSample sample;
        try { sample = allocator.AllocateSample(); }
        catch (SharpGen.Runtime.SharpGenException) { return null; }
        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
            using var tex = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
            var view = OutputView(tex, dxgi.SubresourceIndex);

            double s = Math.Min((double)outW / cw, (double)outH / ch);
            int dw = Math.Min(outW, (int)Math.Round(cw * s)), dh = Math.Min(outH, (int)Math.Round(ch * s));
            int dx = (outW - dw) / 2, dy = (outH - dh) / 2;
            videoContext.VideoProcessorSetStreamSourceRect(vp!, 0, true, new RawRect(0, 0, cw, ch));
            videoContext.VideoProcessorSetStreamDestRect(vp!, 0, true, new RawRect(dx, dy, dx + dw, dy + dh));
            videoContext.VideoProcessorSetOutputTargetRect(vp!, true, new RawRect(0, 0, outW, outH));
            var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
            videoContext.VideoProcessorBlt(vp!, view, 0, 1, new[] { stream }).CheckError();
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        DisposeViews();
        vp?.Dispose(); vpEnum?.Dispose();
        allocator.UninitializeSampleAllocator();
        allocator.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
    }
}

static class MF
{
    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public const int E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    public const int E_SHUTDOWN = unchecked((int)0xC00D3E85);

    static int started;
    public static void Startup()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) MediaFactory.MFStartup(true).CheckError();
    }
}
