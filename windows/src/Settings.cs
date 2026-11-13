using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewind;

sealed class RewindException(string message, int hresult = 0) : Exception(message)
{
    public int Code { get; } = hresult;
}

enum VideoCodec { H264, Hevc }

enum Quality { Medium, High, VeryHigh, Ultra }

enum Resolution { Native = 0, P2160 = 2160, P1440 = 1440, P1080 = 1080, P720 = 720 }

enum AudioTrack { System = 0, Mic = 1 }

static class Labels
{
    public static string Label(this VideoCodec c) => c == VideoCodec.H264 ? "H.264 (most compatible)" : "HEVC / H.265 (smaller files)";
    public static string Short(this VideoCodec c) => c == VideoCodec.H264 ? "H.264" : "HEVC";

    public static string Label(this Quality q) => q switch
    {
        Quality.Medium => "Medium",
        Quality.High => "High",
        Quality.VeryHigh => "Very high",
        _ => "Ultra",
    };

    public static double BitsPerPixel(this Quality q) => q switch
    {
        Quality.Medium => 0.04,
        Quality.High => 0.07,
        Quality.VeryHigh => 0.10,
        _ => 0.15,
    };

    public static string Label(this Resolution r) => r == Resolution.Native ? "Native" : $"{(int)r}p";
}

record struct CaptureSignature(
    string DisplayId,
    string AppId,
    string AppName,
    bool AppRunning,
    int Fps,
    Quality Quality,
    VideoCodec Codec,
    Resolution Resolution,
    bool ShowCursor,
    bool SystemAudio,
    bool Microphone);

static class CaptureMath
{
    public static (int W, int H) OutputSize((int W, int H) native, Resolution resolution, VideoCodec codec)
    {
        double w = native.W, h = native.H;
        if (resolution != Resolution.Native && h > (int)resolution)
        {
            double s = (int)resolution / h;
            w *= s; h *= s;
        }
        // Hardware H.264 encoders (NVENC, Quick Sync, AMF) top out at 4096 px; HEVC goes to 8K.
        double maxDim = codec == VideoCodec.H264 ? 4096 : 8192;
        if (Math.Max(w, h) > maxDim)
        {
            double s = maxDim / Math.Max(w, h);
            w *= s; h *= s;
        }
        return (Math.Max(2, (int)w & ~1), Math.Max(2, (int)h & ~1));
    }

    public static int Bitrate((int W, int H) size, int fps, Quality quality, VideoCodec codec)
    {
        double bps = (double)size.W * size.H * fps * quality.BitsPerPixel();
        if (codec == VideoCodec.Hevc) bps *= 0.7;
        return (int)Math.Min(Math.Max(bps, 2_000_000), 150_000_000);
    }

    public static (int W, int H) NativePixelSize(string displayId)
    {
        var d = Displays.Find(displayId);
        return d is null ? (1920, 1080) : (d.Bounds.Width, d.Bounds.Height);
    }
}


sealed record Hotkey(uint Vk, uint Modifiers, string Display)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    public static string ModifierText(uint m)
    {
        var s = new StringBuilder();
        if ((m & Ctrl) != 0) s.Append("Ctrl+");
        if ((m & Alt) != 0) s.Append("Alt+");
        if ((m & Shift) != 0) s.Append("Shift+");
        if ((m & Win) != 0) s.Append("Win+");
        return s.ToString();
    }

    public static bool IsFunctionKey(uint vk) => vk is >= 0x70 and <= 0x87;

    public static string KeyName(uint vk)
    {
        if (IsFunctionKey(vk)) return "F" + (vk - 0x6F);
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        string? special = vk switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
            0x25 => "Left", 0x27 => "Right", 0x26 => "Up", 0x28 => "Down",
            0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
            0x2C => "PrtScn", 0x13 => "Pause", 0x91 => "ScrollLock",
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-", 0x6E => "Num.", 0x6F => "Num/",
            _ => null,
        };
        if (special != null) return special;
        uint scan = Native.MapVirtualKey(vk, 0);
        var sb = new StringBuilder(32);
        if (scan != 0 && Native.GetKeyNameText((int)(scan << 16), sb, sb.Capacity) > 0) return sb.ToString();
        return $"Key {vk:X2}";
    }

    public static Hotkey Make(uint vk, uint mods) => new(vk, mods, ModifierText(mods) + KeyName(vk));
}

enum HotkeyAction { ToggleOverlay, SaveReplay, ToggleReplay, ToggleRecording, Screenshot }

static class HotkeyActions
{
    public static readonly HotkeyAction[] All = Enum.GetValues<HotkeyAction>();

    public static string Key(this HotkeyAction a) => a switch
    {
        HotkeyAction.ToggleOverlay => "toggleOverlay",
        HotkeyAction.SaveReplay => "saveReplay",
        HotkeyAction.ToggleReplay => "toggleReplay",
        HotkeyAction.ToggleRecording => "toggleRecording",
        _ => "screenshot",
    };

    public static string Label(this HotkeyAction a) => a switch
    {
        HotkeyAction.ToggleOverlay => "Show / hide overlay",
        HotkeyAction.SaveReplay => "Save instant replay",
        HotkeyAction.ToggleReplay => "Turn instant replay on / off",
        HotkeyAction.ToggleRecording => "Start / stop recording",
        _ => "Take screenshot",
    };

    public static Hotkey DefaultHotkey(this HotkeyAction a) => a switch
    {
        HotkeyAction.ToggleOverlay => Hotkey.Make(0x5A, Hotkey.Alt),
        HotkeyAction.SaveReplay => Hotkey.Make(0x79, Hotkey.Alt),
        HotkeyAction.ToggleReplay => Hotkey.Make(0x79, Hotkey.Alt | Hotkey.Shift),
        HotkeyAction.ToggleRecording => Hotkey.Make(0x78, Hotkey.Alt),
        _ => Hotkey.Make(0x70, Hotkey.Alt),
    };
}


sealed class Settings : INotifyPropertyChanged
{
    // Lazy, so the static path and JSON options below exist before the first load.
    static Settings? shared;
    public static Settings Shared => shared ??= Load();

    /// For --render-ui: default settings that are never read from or written to disk.
    public static void UseDefaults() => shared = new Settings(new Data()) { readOnly = true };
    bool readOnly;

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rewind", "settings.json");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public event Action? HotkeysChanged;

    sealed class Data
    {
        public string DisplayId { get; set; } = "";
        public string CaptureAppId { get; set; } = "";
        public string CaptureAppName { get; set; } = "";
        public int Fps { get; set; } = 60;
        public Quality Quality { get; set; } = Quality.High;
        public VideoCodec Codec { get; set; } = VideoCodec.H264;
        public Resolution Resolution { get; set; } = Resolution.Native;
        public bool ShowCursor { get; set; } = true;
        public bool SystemAudio { get; set; } = true;
        public bool Microphone { get; set; }
        public bool MixMicrophone { get; set; } = true;
        public int ReplaySeconds { get; set; } = 120;
        public bool StartReplayOnLaunch { get; set; } = true;
        public bool ShareRewindWindows { get; set; } = true;
        public string? OutputDirectory { get; set; }
        public Dictionary<string, Hotkey> Hotkeys { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    Data d;
    Settings(Data data) { d = data; }

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json);
                if (data != null) return new Settings(Sanitize(data));
            }
        }
        catch (Exception e) { Log.Error("loading settings", e); }
        return new Settings(new Data());
    }

    static Data Sanitize(Data x)
    {
        x.Fps = x.Fps <= 0 ? 60 : Math.Clamp(x.Fps, MinFps, MaxFps);
        if (x.ReplaySeconds is < 5 or > 3600) x.ReplaySeconds = 120;
        x.Hotkeys ??= new();
        x.DisplayId ??= "";
        x.CaptureAppId ??= "";
        x.CaptureAppName ??= "";
        return x;
    }

    void Save()
    {
        if (readOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) { Log.Error("saving settings", e); }
    }

    void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke();
    }

    public string DisplayId { get => d.DisplayId; set => Set(d.DisplayId, value ?? "", v => d.DisplayId = v); }
    public string CaptureAppId { get => d.CaptureAppId; set => Set(d.CaptureAppId, value ?? "", v => d.CaptureAppId = v); }
    public string CaptureAppName { get => d.CaptureAppName; set => Set(d.CaptureAppName, value ?? "", v => d.CaptureAppName = v); }
    public const int MinFps = 1, MaxFps = 360;
    public int Fps { get => d.Fps; set => Set(d.Fps, Math.Clamp(value, MinFps, MaxFps), v => d.Fps = v); }
    public Quality Quality { get => d.Quality; set => Set(d.Quality, value, v => d.Quality = v); }
    public VideoCodec Codec { get => d.Codec; set => Set(d.Codec, value, v => d.Codec = v); }
    public Resolution Resolution { get => d.Resolution; set => Set(d.Resolution, value, v => d.Resolution = v); }
    public bool ShowCursor { get => d.ShowCursor; set => Set(d.ShowCursor, value, v => d.ShowCursor = v); }
    public bool SystemAudio { get => d.SystemAudio; set => Set(d.SystemAudio, value, v => d.SystemAudio = v); }
    public bool Microphone { get => d.Microphone; set => Set(d.Microphone, value, v => d.Microphone = v); }
    public bool MixMicrophone { get => d.MixMicrophone; set => Set(d.MixMicrophone, value, v => d.MixMicrophone = v); }
    public int ReplaySeconds { get => d.ReplaySeconds; set => Set(d.ReplaySeconds, value, v => d.ReplaySeconds = v); }
    public bool StartReplayOnLaunch { get => d.StartReplayOnLaunch; set => Set(d.StartReplayOnLaunch, value, v => d.StartReplayOnLaunch = v); }
    public bool ShareRewindWindows { get => d.ShareRewindWindows; set => Set(d.ShareRewindWindows, value, v => d.ShareRewindWindows = v); }

    public string OutputDirectory
    {
        get => string.IsNullOrEmpty(d.OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Rewind")
            : d.OutputDirectory;
        set => Set(d.OutputDirectory, value, v => d.OutputDirectory = v);
    }

    public Hotkey Hotkey(HotkeyAction a) => d.Hotkeys.TryGetValue(a.Key(), out var h) ? h : a.DefaultHotkey();

    public void SetHotkey(Hotkey h, HotkeyAction a)
    {
        d.Hotkeys[a.Key()] = h;
        Save();
        HotkeysChanged?.Invoke();
        Changed?.Invoke();
    }

    public void ResetHotkeys()
    {
        d.Hotkeys.Clear();
        Save();
        HotkeysChanged?.Invoke();
        Changed?.Invoke();
    }

    public CaptureSignature CaptureSignature => new(
        DisplayId, CaptureAppId, CaptureAppName,
        AppRunning: CaptureAppId.Length > 0 && AppTarget.IsRunning(CaptureAppId),
        Fps, Quality, Codec, Resolution, ShowCursor, SystemAudio, Microphone);
}


sealed record DisplayInfo(string Id, int Number, IntPtr Handle, Native.RECT Bounds, Native.RECT Work, bool Primary)
{
    public string Name => $"Display {Number}{(Primary ? " (primary)" : "")} ({Bounds.Width}×{Bounds.Height})";
}

static class Displays
{
    public static List<DisplayInfo> All()
    {
        var list = new List<DisplayInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                var digits = new string(mi.szDevice.Where(char.IsDigit).ToArray());
                int n = int.TryParse(digits, out var v) ? v : list.Count + 1;
                list.Add(new DisplayInfo(mi.szDevice, n, h, mi.rcMonitor, mi.rcWork, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(x => x.Number).ToList();
    }

    public static DisplayInfo? Find(string id)
    {
        var all = All();
        return (id.Length > 0 ? all.FirstOrDefault(x => x.Id == id) : null) ?? all.FirstOrDefault(x => x.Primary) ?? all.FirstOrDefault();
    }
}


sealed record AppWindow(IntPtr Hwnd, uint Pid, string Key, string Name, string? ExePath, Native.RECT Bounds);

/// Identifies an app to capture by its executable name (e.g. "javaw.exe"), the closest Windows
/// equivalent of a bundle ID that stays the same across launches.
static class AppTarget
{
    public static string Key(string exePath) => Path.GetFileName(exePath).ToLowerInvariant();

    public static bool IsRunning(string key)
    {
        var name = Path.GetFileNameWithoutExtension(key);
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public static List<AppWindow> Windows()
    {
        var list = new List<AppWindow>();
        uint me = (uint)Environment.ProcessId;
        var paths = new Dictionary<uint, string?>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd)) return true;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == me) return true;
            var r = Native.VisibleBounds(hwnd);
            if (r.Width <= 50 || r.Height <= 50) return true;
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = Native.ProcessPath(pid);
            if (path == null) return true;
            list.Add(new AppWindow(hwnd, pid, Key(path), DisplayName(path), path, r));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<AppWindow> Pickable() =>
        Windows().Where(w => w.Key != "explorer.exe" && w.Key != "applicationframehost.exe" && w.Key != "textinputhost.exe")
            .GroupBy(w => w.Key)
            .Select(g => g.OrderByDescending(w => w.Bounds.Width * w.Bounds.Height).First())
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static AppWindow? FindWindow(string key) =>
        Windows().Where(w => w.Key == key && !Native.IsIconic(w.Hwnd))
            .OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .FirstOrDefault();

    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(string exePath)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(exePath, out var cached)) return cached;
            string name;
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim();
                name = string.IsNullOrEmpty(desc) ? Path.GetFileNameWithoutExtension(exePath) : desc;
            }
            catch { name = Path.GetFileNameWithoutExtension(exePath); }
            NameCache[exePath] = name;
            return name;
        }
    }
}
