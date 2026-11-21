using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace Rewind;

/// Game capture (Settings → Capture, off by default), through OBS Studio's game capture hook, the way OBS's own
/// Game Capture source drives it. Rewind runs OBS's injector to load graphics-hook64.dll (or 32) from the OBS
/// install into the game, hands it the Present offsets OBS's get-graphics-offsets helper finds, and copies the
/// texture the hook shares. OBS's hook copies Direct3D 9/10/11/12, OpenGL and Vulkan games.
///
/// OBS's hook and the protocol it speaks are GPL-2.0 (OBS Studio, github.com/obsproject/obs-studio). This copy of
/// Rewind is for local use; one that's shared with anyone has to follow the GPL for this file.
sealed class ObsGameCapture : IDisposable
{
    // hook_info in OBS's shared/obs-hook-config/graphics-hook-info.h (pack 8, 648 bytes).
    const int OffType = 8, OffWindow = 12, OffFormat = 16, OffCx = 20, OffCy = 24, OffMapId = 40, OffFlip = 48,
              OffFrameInterval = 56, OffForceShmem = 65, OffCaptureOverlay = 66, OffAllowSrgbAlias = 67, OffOffsets = 68,
              HookInfoSize = 648;
    const uint CaptureTypeTexture = 1;

    readonly Gpu gpu;
    readonly uint pid;
    readonly Mutex keepalive;
    readonly MemoryMappedFile infoMap;
    readonly MemoryMappedViewAccessor info;
    readonly EventWaitHandle ready, exit, stop;
    ID3D11Texture2D? shared, staging;
    Flipper? flipper;
    bool flip;

    /// Rewind's own copy of the newest frame (on Rewind's device), for the encoder and screenshots.
    public ID3D11Texture2D? Own { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool GameGone => exit.WaitOne(0);

    ObsGameCapture(Gpu gpu, uint pid, Mutex keepalive, MemoryMappedFile map, EventWaitHandle ready, EventWaitHandle exit, EventWaitHandle stop)
    {
        this.gpu = gpu;
        this.pid = pid;
        this.keepalive = keepalive;
        infoMap = map;
        info = map.CreateViewAccessor(0, HookInfoSize);
        this.ready = ready;
        this.exit = exit;
        this.stop = stop;
    }

    // MARK: OBS's files

    /// OBS's win-capture folder with the hook, its injectors and the offsets helpers, or null without OBS.
    public static string? ObsDir
    {
        get
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var dir = Path.Combine(root, "obs-studio", "data", "obs-plugins", "win-capture");
                if (new[] { "graphics-hook64.dll", "inject-helper64.exe", "get-graphics-offsets64.exe" }.All(f => File.Exists(Path.Combine(dir, f))))
                    return dir;
            }
            return null;
        }
    }

    /// OBS is installed and this is x64 Rewind (ARM64 Rewind can't hook x64 or x86 games).
    public static bool Available => RuntimeInformation.ProcessArchitecture == Architecture.X64 && ObsDir != null;

    // MARK: Starting

    /// Hooks the game (or reuses OBS's hook if it's already in there) and waits for its first shared frame.
    /// Takes a few seconds; null with the reason when it can't.
    public static ObsGameCapture? TryStart(Gpu gpu, uint pid, IntPtr window, int fps, out string why)
    {
        why = "";
        if (Blocked(pid) is { } blocked) { why = blocked; return null; }
        var dir = ObsDir;
        if (dir == null) { why = "OBS Studio isn't installed."; return null; }
        bool is32;
        try { is32 = Is32Bit(pid); }
        catch (Exception e) { why = "Rewind can't open it (" + e.Message + ")."; return null; }
        string bits = is32 ? "32" : "64";

        // The hook stops capturing when this mutex is gone, i.e. when Rewind is.
        var keepalive = new Mutex(false, $"CaptureHook_KeepAlive{pid}");
        try
        {
            var offsets = GraphicsOffsets(Path.Combine(dir, $"get-graphics-offsets{bits}.exe"));
            if (offsets == null) { why = "OBS's offsets helper didn't work."; keepalive.Dispose(); return null; }
            // A hook that's already in the game (from Rewind or OBS earlier) is told to start over instead.
            bool existing = TryOpenEvent($"CaptureHook_Restart{pid}") is { } restart && Signal(restart);
            if (!existing && Inject(dir, bits, pid, window) is { } failed) { why = failed; keepalive.Dispose(); return null; }
            var c = Connect(gpu, pid, keepalive, offsets, fps, out why);
            if (c == null) keepalive.Dispose();
            return c;
        }
        catch (Exception e)
        {
            Log.Error("game capture", e);
            keepalive.Dispose();
            why = e.Message;
            return null;
        }
    }

    static bool Signal(EventWaitHandle e) { using (e) e.Set(); return true; }

    static EventWaitHandle? TryOpenEvent(string name) =>
        EventWaitHandle.TryOpenExisting(name, out var e) ? e : null;

    /// Opens what the hook made in the game, gives it the offsets and waits for its first frame.
    static ObsGameCapture? Connect(Gpu gpu, uint pid, Mutex keepalive, uint[] offsets, int fps, out string why)
    {
        why = "";
        MemoryMappedFile? map = null;
        EventWaitHandle? init = null, ready = null, exit = null, stop = null;
        var sw = Stopwatch.StartNew();
        // The hook makes these a moment after it loads.
        while (sw.Elapsed.TotalSeconds < 8)
        {
            try { map ??= MemoryMappedFile.OpenExisting($"CaptureHook_HookInfo{pid}", MemoryMappedFileRights.ReadWrite); } catch { }
            init ??= TryOpenEvent($"CaptureHook_Initialize{pid}");
            ready ??= TryOpenEvent($"CaptureHook_HookReady{pid}");
            exit ??= TryOpenEvent($"CaptureHook_Exit{pid}");
            stop ??= TryOpenEvent($"CaptureHook_Stop{pid}");
            if (map != null && init != null && ready != null && exit != null && stop != null) break;
            Thread.Sleep(50);
        }
        if (map == null || init == null || ready == null || exit == null || stop == null)
        {
            map?.Dispose(); init?.Dispose(); ready?.Dispose(); exit?.Dispose(); stop?.Dispose();
            why = "OBS's hook didn't start in the game (it may block it).";
            return null;
        }

        var c = new ObsGameCapture(gpu, pid, keepalive, map, ready, exit, stop);
        // Like OBS's Game Capture with "Limit capture framerate" on: the hook copies at most Rewind's frame rate
        // (nanoseconds between copies). Copying every frame of a game running far above it costs the game a lot.
        c.info.Write(OffFrameInterval, (ulong)(1_000_000_000L / Math.Max(fps, 1)));
        // Shared textures, no overlays, sRGB as plain, as OBS sets them.
        c.info.Write(OffForceShmem, (byte)0);
        c.info.Write(OffCaptureOverlay, (byte)0);
        c.info.Write(OffAllowSrgbAlias, (byte)1);
        for (int i = 0; i < offsets.Length; i++) c.info.Write(OffOffsets + i * 4, offsets[i]);
        init.Set();
        init.Dispose();

        sw.Restart();
        while (!ready.WaitOne(100))
        {
            if (c.GameGone || sw.Elapsed.TotalSeconds > 10)
            {
                why = c.GameGone ? "The game closed." : "OBS's hook didn't send a frame (the game may not have drawn one yet).";
                c.Dispose();
                return null;
            }
        }
        if (!c.OpenTexture(out why)) { c.Dispose(); return null; }
        return c;
    }

    /// After a "ready" from the hook: the frame's size and the texture it's shared through.
    bool OpenTexture(out string why)
    {
        why = "";
        ReleaseTextures();
        if (info.ReadUInt32(OffType) != CaptureTypeTexture)
        {
            why = "OBS's hook can only copy this game through memory, which Rewind doesn't support yet.";
            return false;
        }
        uint window = info.ReadUInt32(OffWindow), mapId = info.ReadUInt32(OffMapId);
        uint handle;
        try
        {
            using var texMap = MemoryMappedFile.OpenExisting($"CaptureHook_Texture_{window}_{mapId}", MemoryMappedFileRights.Read);
            using var v = texMap.CreateViewAccessor(0, 4, MemoryMappedFileAccess.Read);
            handle = v.ReadUInt32(0);
        }
        catch (Exception e) { why = "Rewind couldn't find OBS's shared frame (" + e.Message + ")."; return false; }

        try { shared = gpu.Device.OpenSharedResource<ID3D11Texture2D>((IntPtr)handle); }
        catch (Exception e)
        {
            why = "Rewind can't open the game's frames. The game may be running on a different graphics card than Rewind. " + e.Message;
            return false;
        }
        var d = shared.Description;
        var format = Plain(d.Format);
        if (format == DxgiFormat.Unknown)
        {
            why = $"The game draws in a format Rewind can't record yet (DXGI format {(int)d.Format}, probably HDR).";
            return false;
        }
        Width = (int)d.Width;
        Height = (int)d.Height;
        // OpenGL frames come bottom row first.
        flip = info.ReadByte(OffFlip) != 0;
        var desc = new Texture2DDescription
        {
            Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };
        Own = gpu.Device.CreateTexture2D(desc);
        if (flip)
        {
            staging = gpu.Device.CreateTexture2D(desc);
            flipper ??= new Flipper(gpu);
        }
        Log.Info($"game capture: OBS's hook shares {Width}×{Height} {d.Format}{(flip ? ", upside down" : "")}");
        return true;
    }

    // MARK: Frames

    /// Copies the game's current frame into `Own` (on Rewind's device); null when there's none. OBS's hook copies
    /// each frame the game draws into the shared texture; like OBS, Rewind just reads it at its own frame rate.
    /// Call on the thread that uses Rewind's device context.
    public ID3D11Texture2D? Copy()
    {
        // The hook starts over (and signals ready again) when the game's size or device changes.
        if (ready.WaitOne(0) && !OpenTexture(out var why)) throw new RewindException(why);
        if (shared == null || Own == null) return null;
        if (flip)
        {
            gpu.Context.CopyResource(staging!, shared);
            flipper!.Draw(staging!, Own);
        }
        else gpu.Context.CopyResource(Own, shared);
        return Own;
    }

    // The 8-bit formats Rewind's video processor reads. sRGB and typeless frames are copied into the plain format.
    static DxgiFormat Plain(DxgiFormat f) => f switch
    {
        DxgiFormat.B8G8R8A8_UNorm or DxgiFormat.B8G8R8A8_UNorm_SRgb or DxgiFormat.B8G8R8A8_Typeless
            or DxgiFormat.B8G8R8X8_UNorm or DxgiFormat.B8G8R8X8_UNorm_SRgb or DxgiFormat.B8G8R8X8_Typeless => DxgiFormat.B8G8R8A8_UNorm,
        DxgiFormat.R8G8B8A8_UNorm or DxgiFormat.R8G8B8A8_UNorm_SRgb or DxgiFormat.R8G8B8A8_Typeless => DxgiFormat.R8G8B8A8_UNorm,
        _ => DxgiFormat.Unknown,
    };

    void ReleaseTextures()
    {
        shared?.Dispose(); shared = null;
        staging?.Dispose(); staging = null;
        Own?.Dispose(); Own = null;
    }

    public void Dispose()
    {
        // The hook stays in the game (like with OBS) but stops copying.
        try { stop.Set(); } catch { }
        ReleaseTextures();
        flipper?.Dispose();
        info.Dispose();
        infoMap.Dispose();
        ready.Dispose();
        exit.Dispose();
        stop.Dispose();
        keepalive.Dispose();
    }

    /// Draws a texture upside down into another of the same size.
    sealed class Flipper : IDisposable
    {
        const string Shader = """
            Texture2D tex : register(t0);
            SamplerState smp : register(s0);
            struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
            V vs(uint id : SV_VertexID)
            {
                V o;
                float2 corner = float2(id & 1, id >> 1);
                o.pos = float4(corner.x * 2 - 1, 1 - corner.y * 2, 0, 1);
                o.uv = float2(corner.x, 1 - corner.y);
                return o;
            }
            float4 ps(V i) : SV_Target { return tex.Sample(smp, i.uv); }
            """;

        readonly Gpu gpu;
        readonly ID3D11Multithread lockDevice;
        readonly ID3D11VertexShader vs;
        readonly ID3D11PixelShader ps;
        readonly ID3D11SamplerState sampler;
        readonly ID3D11RasterizerState raster;

        public Flipper(Gpu gpu)
        {
            this.gpu = gpu;
            var d = gpu.Device;
            lockDevice = gpu.Context.QueryInterface<ID3D11Multithread>();
            vs = d.CreateVertexShader(PointerPainter.Compile(Shader, "vs", "vs_4_0"));
            ps = d.CreatePixelShader(PointerPainter.Compile(Shader, "ps", "ps_4_0"));
            sampler = d.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            raster = d.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        }

        public void Draw(ID3D11Texture2D source, ID3D11Texture2D target)
        {
            var c = gpu.Context;
            var desc = target.Description;
            using var view = gpu.Device.CreateShaderResourceView(source);
            using var rtv = gpu.Device.CreateRenderTargetView(target);
            // The encoder shares this device; keep its calls from landing between these.
            lockDevice.Enter();
            try
            {
                c.OMSetRenderTargets(rtv);
                c.RSSetViewport(new Viewport(0, 0, desc.Width, desc.Height));
                c.RSSetState(raster);
                c.IASetInputLayout(null);
                c.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                c.VSSetShader(vs);
                c.PSSetShader(ps);
                c.PSSetSampler(0, sampler);
                c.PSSetShaderResource(0, view);
                c.OMSetBlendState(null);
                c.Draw(4, 0);
                c.PSSetShaderResource(0, null!);
                c.UnsetRenderTargets();
            }
            finally { lockDevice.Leave(); }
        }

        public void Dispose()
        {
            raster.Dispose(); sampler.Dispose(); ps.Dispose(); vs.Dispose(); lockDevice.Dispose();
        }
    }

    // MARK: OBS's helpers

    /// The Present offsets OBS's hook needs, in graphics_offsets order (19 values), from get-graphics-offsets.
    static uint[]? GraphicsOffsets(string helper)
    {
        string text;
        try
        {
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                                                      WorkingDirectory = Path.GetDirectoryName(helper)! };
            using var p = Process.Start(psi)!;
            text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
        }
        catch (Exception e) { Log.Error("game capture: offsets helper", e); return null; }

        var values = new Dictionary<string, uint>();
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var v = line[(eq + 1)..].Trim();
            // OBS prints them as hex (0x84c0).
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out var n) : uint.TryParse(v, out n))
                values[section + "." + line[..eq].Trim()] = n;
        }
        uint Get(string k) => values.GetValueOrDefault(k);
        var offsets = new uint[]
        {
            Get("d3d8.present"),
            Get("d3d9.present"), Get("d3d9.present_ex"), Get("d3d9.present_swap"), Get("d3d9.d3d9_clsoff"), Get("d3d9.is_d3d9ex_clsoff"),
            Get("dxgi.present"), Get("dxgi.resize"), Get("dxgi.present1"),
            Get("ddraw.surface_create"), Get("ddraw.surface_restore"), Get("ddraw.surface_release"), Get("ddraw.surface_unlock"),
            Get("ddraw.surface_blt"), Get("ddraw.surface_flip"), Get("ddraw.surface_set_palette"), Get("ddraw.palette_set_entries"),
            Get("dxgi.release"),
            Get("d3d12.execute_command_lists"),
        };
        Log.Info($"game capture: offsets dxgi.present=0x{Get("dxgi.present"):x} d3d9.present=0x{Get("d3d9.present"):x}");
        return offsets;
    }

    /// Runs OBS's injector for the game's bitness: first directly (a thread in the game), then, if the game doesn't
    /// allow that, OBS's "anti-cheat compatibility" way (a Windows message hook on the game's window thread).
    /// Null when the hook got in, or why it didn't.
    static string? Inject(string dir, string bits, uint pid, IntPtr window)
    {
        var helper = Path.Combine(dir, $"inject-helper{bits}.exe");
        var dll = Path.Combine(dir, $"graphics-hook{bits}.dll");
        int Run(bool viaWindow, uint id)
        {
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add(viaWindow ? "1" : "0");
            psi.ArgumentList.Add(id.ToString());
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        try
        {
            int direct = Run(false, pid);
            Log.Info($"game capture: OBS's inject-helper{bits} (direct) exited with {direct}");
            if (direct == 0) return null;
            uint thread = window != IntPtr.Zero ? GetWindowThreadProcessId(window, out _) : 0;
            if (thread == 0) return $"OBS's injector couldn't load the hook into the game (code {direct}).";
            int viaWindow = Run(true, thread);
            Log.Info($"game capture: OBS's inject-helper{bits} (window hook) exited with {viaWindow}");
            return viaWindow == 0 ? null : $"OBS's injector couldn't load the hook into the game (codes {direct} and {viaWindow}).";
        }
        catch (Exception e) { return "Rewind couldn't start OBS's injector (" + e.Message + "). Antivirus may have blocked it."; }
    }

    // MARK: Anti-cheat

    /// Games whose kernel anti-cheat would see a DLL inside them and may ban for it. Rewind never hooks these.
    static readonly string[] ProtectedGames =
    {
        "valorant-win64-shipping.exe", "valorant.exe",               // Riot Vanguard
        "league of legends.exe", "leagueoflegends.exe",               // Riot Vanguard
        "robloxplayerbeta.exe", "windows10universal.exe",             // Roblox (Hyperion)
        "cs2.exe", "csgo.exe", "dota2.exe", "deadlock.exe",           // Valve Anti-Cheat
        "fortniteclient-win64-shipping.exe",                          // Easy Anti-Cheat
        "r5apex.exe", "r5apex_dx12.exe",                              // Apex Legends (Easy Anti-Cheat)
        "tslgame.exe", "rainbowsix.exe", "rainbowsix_be.exe",         // BattlEye
        "destiny2.exe", "escapefromtarkov.exe", "dayz_x64.exe",       // BattlEye
        "gta5.exe", "gta5_enhanced.exe",                              // BattlEye
        "cod.exe", "modernwarfare.exe", "blackops6.exe",              // Call of Duty (Ricochet)
        "overwatch.exe", "marvel-win64-shipping.exe",                 // Overwatch, Marvel Rivals
        "rocketleague.exe", "deadbydaylight-win64-shipping.exe",      // Easy Anti-Cheat
        "eldenring.exe", "nightreign.exe",                            // Easy Anti-Cheat
        "thefinals.exe", "naraka.exe", "pubg.exe",
    };

    /// Folders and files anti-cheat installs next to (or a few levels above) a game's .exe.
    static readonly string[] AntiCheatMarkers =
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_x64.dll", "EasyAntiCheat_EOS_Setup.exe",
        "BattlEye", "BEClient_x64.dll", "BEService_x64.exe",
        "nProtect", "GameGuard", "XIGNCODE", "mhyprot2.sys", "ACE-Base.sys", "PnkBstrA.exe",
    };

    /// Why Rewind won't hook this game, or null if it may.
    public static string? Blocked(uint pid)
    {
        string? exe = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            exe = p.MainModule?.FileName;
        }
        catch (Exception e) { Log.Info("game capture: can't see the game's .exe: " + e.Message); }
        if (exe == null) return "Rewind can't see inside it (it may run as administrator or be protected).";
        var name = Path.GetFileName(exe).ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(exe);
        if (ProtectedGames.Contains(name)) return $"{title} has kernel anti-cheat that could ban you for it.";
        var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        for (int up = 0; dir != null && dir.Parent != null && up < 4; up++, dir = dir.Parent)
            foreach (var marker in AntiCheatMarkers)
                if (Directory.Exists(Path.Combine(dir.FullName, marker)) || File.Exists(Path.Combine(dir.FullName, marker)))
                    return $"{title} has anti-cheat ({marker}) that could ban you for it.";
        return null;
    }

    static bool Is32Bit(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return IsWow64Process(h, out bool wow64) && wow64; }
        finally { CloseHandle(h); }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
