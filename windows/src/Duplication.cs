using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Rewind;

/// Captures a monitor with Desktop Duplication. Windows.Graphics.Capture makes Windows stop drawing the mouse pointer
/// over some fullscreen games; this doesn't, but it leaves the pointer out of the picture, so it's drawn in here.
sealed class Duplicator : IDisposable
{
    const int WaitTimeout = unchecked((int)0x887A0027), Unsupported = unchecked((int)0x887A0004);

    readonly Gpu gpu;
    readonly bool drawPointer;
    IDXGIOutput1? output;
    IDXGIOutputDuplication? dup;
    IDXGIResource? frame;
    ID3D11Texture2D? canvas;
    ID3D11RenderTargetView? canvasView;
    PointerPainter? painter;
    byte[] shape = Array.Empty<byte>();
    int pointerX, pointerY;
    bool pointerVisible;
    long retryAt;
    int quickVBlanks;

    public IntPtr Monitor { get; }
    /// The newest picture, with the pointer drawn in when asked for. Valid until the next Render.
    public ID3D11Texture2D? Canvas => canvas;
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// Set when this screen can't be duplicated at all (another GPU, rotated, unsupported).
    public bool Broken { get; private set; }

    Duplicator(Gpu gpu, IntPtr monitor, bool drawPointer)
    {
        this.gpu = gpu;
        Monitor = monitor;
        this.drawPointer = drawPointer;
    }

    public static Duplicator? TryCreate(Gpu gpu, IntPtr monitor, bool drawPointer, out string why)
    {
        var d = new Duplicator(gpu, monitor, drawPointer);
        why = d.Open();
        if (d.dup != null) return d;
        d.Dispose();
        return null;
    }

    string Open()
    {
        output?.Dispose();
        output = FindOutput();
        if (output == null) { Broken = true; return "the screen isn't driven by the GPU Rewind uses"; }
        if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
        {
            Broken = true;
            return "the screen is rotated";
        }
        try
        {
            dup = output.DuplicateOutput(gpu.Device);
            return "";
        }
        catch (SharpGen.Runtime.SharpGenException e)
        {
            if (e.ResultCode.Code == Unsupported) Broken = true;
            return e.Message;
        }
    }

    IDXGIOutput1? FindOutput()
    {
        using var dxgi = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        for (uint i = 0; adapter.EnumOutputs(i, out var o).Success; i++)
        {
            using (o)
                if (o.Description.Monitor == Monitor) return o.QueryInterface<IDXGIOutput1>();
        }
        return null;
    }

    /// Waits for the screen's next refresh and checks out a frame if it changed (Release it after). `changed` says
    /// whether the picture changed, counting pointer moves when the pointer is drawn in.
    public bool Acquire(out long pts, out bool changed)
    {
        pts = 0;
        changed = false;
        if (dup == null)
        {
            // Duplication stops on display mode changes (a game going fullscreen) and on the secure desktop.
            if (Stopwatch.GetTimestamp() < retryAt || Broken) { Thread.Sleep(20); return false; }
            Open();
            if (dup == null) { retryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4; return false; }
        }
        // Waiting inside AcquireNextFrame holds the GPU device's lock, which stalls the encoder that shares it, so
        // wait for the refresh (when a new frame can appear) and then only look.
        long t0 = Stopwatch.GetTimestamp();
        bool waited;
        try { output!.WaitForVBlank(); waited = Stopwatch.GetTimestamp() - t0 > Stopwatch.Frequency / 2000; }
        catch (SharpGen.Runtime.SharpGenException) { waited = false; }
        // A screen that's asleep doesn't wait at all.
        quickVBlanks = waited ? 0 : quickVBlanks + 1;
        if (quickVBlanks > 2) Thread.Sleep(10);
        var r = dup.AcquireNextFrame(0, out var info, out var res);
        if (r.Code == WaitTimeout) return false;
        if (r.Failure)
        {
            dup.Dispose();
            dup = null;
            return false;
        }
        frame = res;

        bool pointerChanged = false;
        if (drawPointer)
        {
            if (info.LastMouseUpdateTime != 0)
            {
                var p = info.PointerPosition;
                pointerChanged = pointerVisible || p.Visible;
                pointerVisible = p.Visible;
                pointerX = p.Position.X;
                pointerY = p.Position.Y;
            }
            if (info.PointerShapeBufferSize > 0 && ReadShape(info.PointerShapeBufferSize)) pointerChanged = true;
        }
        changed = info.LastPresentTime != 0 || pointerChanged;
        long qpc = info.LastPresentTime != 0 ? info.LastPresentTime
            : info.LastMouseUpdateTime != 0 ? info.LastMouseUpdateTime
            : Stopwatch.GetTimestamp();
        pts = Clock.FromQpc(qpc);
        return true;
    }

    unsafe bool ReadShape(uint size)
    {
        if (shape.Length < size) shape = new byte[size];
        OutduplPointerShapeInfo info;
        fixed (byte* p = shape)
            if (dup!.GetFramePointerShape(size, (IntPtr)p, out _, out info).Failure) return false;
        painter ??= new PointerPainter(gpu);
        painter.SetShape(shape, info);
        return true;
    }

    /// Copies the checked-out frame into the canvas and draws the pointer on it.
    public void Render()
    {
        using var tex = frame!.QueryInterface<ID3D11Texture2D>();
        var desc = tex.Description;
        if (canvas == null || desc.Width != Width || desc.Height != Height || desc.Format != canvas.Description.Format)
        {
            canvasView?.Dispose();
            canvas?.Dispose();
            canvas = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            canvasView = gpu.Device.CreateRenderTargetView(canvas);
            Width = (int)desc.Width;
            Height = (int)desc.Height;
        }
        gpu.Context.CopyResource(canvas, tex);
        if (drawPointer && pointerVisible) painter?.Draw(canvasView!, pointerX, pointerY);
    }

    public void Release()
    {
        if (frame == null) return;
        frame.Dispose();
        frame = null;
        dup?.ReleaseFrame();
    }

    public void Dispose()
    {
        Release();
        dup?.Dispose();
        output?.Dispose();
        painter?.Dispose();
        canvasView?.Dispose();
        canvas?.Dispose();
        dup = null; output = null; painter = null; canvasView = null; canvas = null;
    }
}

/// Draws the mouse pointer Desktop Duplication reports onto a frame: its colour part blended normally, and the part
/// that inverts the screen (the I-beam, monochrome pointers) inverted.
sealed class PointerPainter : IDisposable
{
    const string Shader = """
        Texture2D tex : register(t0);
        SamplerState smp : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID)
        {
            V o;
            o.uv = float2(id & 1, id >> 1);
            o.pos = float4(o.uv.x * 2 - 1, 1 - o.uv.y * 2, 0, 1);
            return o;
        }
        float4 ps(V i) : SV_Target { return tex.Sample(smp, i.uv); }
        """;

    readonly Gpu gpu;
    readonly ID3D11Multithread lockDevice;
    readonly ID3D11VertexShader vs;
    readonly ID3D11PixelShader ps;
    readonly ID3D11SamplerState sampler;
    readonly ID3D11BlendState blend, invertBlend;
    readonly ID3D11RasterizerState raster;
    ID3D11Texture2D? color, invert;
    ID3D11ShaderResourceView? colorView, invertView;
    int width, height;

    public PointerPainter(Gpu gpu)
    {
        this.gpu = gpu;
        var d = gpu.Device;
        lockDevice = gpu.Context.QueryInterface<ID3D11Multithread>();
        vs = d.CreateVertexShader(Compile("vs", "vs_4_0"));
        ps = d.CreatePixelShader(Compile("ps", "ps_4_0"));
        sampler = d.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        blend = d.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.Zero, Blend.One));
        // Result = src × (1 − dest) + dest × (1 − src): white inverts the screen, black leaves it alone.
        invertBlend = d.CreateBlendState(new BlendDescription(Blend.InverseDestinationColor, Blend.InverseSourceColor, Blend.Zero, Blend.One));
        raster = d.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    public void SetShape(byte[] buf, OutduplPointerShapeInfo s)
    {
        int w = (int)s.Width, h = (int)s.Height, pitch = (int)s.Pitch;
        int type = (int)s.Type;
        if (type == 1) h /= 2; // monochrome: an AND mask on top of an XOR mask
        if (w <= 0 || h <= 0) return;
        var col = new byte[w * h * 4];
        var inv = new byte[w * h * 4];
        bool inverts = false;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            switch (type)
            {
                case 2: // colour with alpha
                    Buffer.BlockCopy(buf, y * pitch + x * 4, col, o, 4);
                    break;
                case 1:
                {
                    int bit = 7 - (x & 7);
                    int and = (buf[y * pitch + (x >> 3)] >> bit) & 1, xor = (buf[(y + h) * pitch + (x >> 3)] >> bit) & 1;
                    if (and == 0) { byte v = (byte)(xor * 255); col[o] = col[o + 1] = col[o + 2] = v; col[o + 3] = 255; }
                    else if (xor == 1) { inv[o] = inv[o + 1] = inv[o + 2] = inv[o + 3] = 255; inverts = true; }
                    break;
                }
                case 4: // colour where the mask byte is 0, XORed with the screen where it's 0xFF
                {
                    int i = y * pitch + x * 4;
                    if (buf[i + 3] == 0) { Buffer.BlockCopy(buf, i, col, o, 3); col[o + 3] = 255; }
                    else if ((buf[i] | buf[i + 1] | buf[i + 2]) != 0) { Buffer.BlockCopy(buf, i, inv, o, 3); inv[o + 3] = 255; inverts = true; }
                    break;
                }
            }
        }
        DisposeTextures();
        width = w;
        height = h;
        (color, colorView) = Upload(col, w, h);
        if (inverts) (invert, invertView) = Upload(inv, w, h);
    }

    unsafe (ID3D11Texture2D, ID3D11ShaderResourceView) Upload(byte[] px, int w, int h)
    {
        fixed (byte* p = px)
        {
            var data = new SubresourceData(p, (uint)(w * 4), 0);
            var tex = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, data);
            return (tex, gpu.Device.CreateShaderResourceView(tex));
        }
    }

    /// Draws the pointer with its top-left corner at (x, y) on the target.
    public void Draw(ID3D11RenderTargetView target, int x, int y)
    {
        if (colorView == null) return;
        var c = gpu.Context;
        // The encoder shares this device; keep its calls from landing between these.
        lockDevice.Enter();
        try
        {
            c.OMSetRenderTargets(target);
            c.RSSetViewport(new Viewport(x, y, width, height));
            c.RSSetState(raster);
            c.IASetInputLayout(null);
            c.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            c.VSSetShader(vs);
            c.PSSetShader(ps);
            c.PSSetSampler(0, sampler);
            c.PSSetShaderResource(0, colorView);
            c.OMSetBlendState(blend);
            c.Draw(4, 0);
            if (invertView != null)
            {
                c.PSSetShaderResource(0, invertView);
                c.OMSetBlendState(invertBlend);
                c.Draw(4, 0);
            }
            c.PSSetShaderResource(0, null!);
            c.UnsetRenderTargets();
        }
        finally { lockDevice.Leave(); }
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    static extern int D3DCompile(byte[] src, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? name, IntPtr defines, IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2,
        out IntPtr code, out IntPtr errors);

    static byte[] Compile(string entry, string target)
    {
        var src = System.Text.Encoding.ASCII.GetBytes(Shader);
        int hr = D3DCompile(src, (nuint)src.Length, null, IntPtr.Zero, IntPtr.Zero, entry, target, 1 << 15 /* optimization level 3 */, 0, out var code, out var errors);
        string? message = null;
        if (errors != IntPtr.Zero)
        {
            using var e = new Blob(errors);
            message = Marshal.PtrToStringAnsi(e.BufferPointer, (int)(nuint)e.BufferSize);
        }
        if (hr < 0) throw new RewindException("Couldn't build the pointer shader: " + (message ?? $"0x{hr:X8}"));
        using var blob = new Blob(code);
        var bytes = new byte[(int)(nuint)blob.BufferSize];
        Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
        return bytes;
    }

    void DisposeTextures()
    {
        colorView?.Dispose(); color?.Dispose(); invertView?.Dispose(); invert?.Dispose();
        colorView = null; color = null; invertView = null; invert = null;
    }

    public void Dispose()
    {
        DisposeTextures();
        raster.Dispose(); invertBlend.Dispose(); blend.Dispose(); sampler.Dispose();
        ps.Dispose(); vs.Dispose(); lockDevice.Dispose();
    }
}
