using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Rewind;

enum RemoteAction { ShowSettings = 0, ToggleOverlay, SaveReplay, ToggleReplay, ToggleRecording, Screenshot, Quit }

static class RemoteActions
{
    public static readonly Dictionary<string, RemoteAction> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = RemoteAction.ShowSettings,
        ["toggle-overlay"] = RemoteAction.ToggleOverlay,
        ["save-replay"] = RemoteAction.SaveReplay,
        ["toggle-replay"] = RemoteAction.ToggleReplay,
        ["toggle-recording"] = RemoteAction.ToggleRecording,
        ["screenshot"] = RemoteAction.Screenshot,
        ["quit"] = RemoteAction.Quit,
    };

    public static HotkeyAction? AsHotkeyAction(this RemoteAction a) => a switch
    {
        RemoteAction.ToggleOverlay => HotkeyAction.ToggleOverlay,
        RemoteAction.SaveReplay => HotkeyAction.SaveReplay,
        RemoteAction.ToggleReplay => HotkeyAction.ToggleReplay,
        RemoteAction.ToggleRecording => HotkeyAction.ToggleRecording,
        RemoteAction.Screenshot => HotkeyAction.Screenshot,
        _ => null,
    };
}

sealed class HotkeyManager
{
    public static HotkeyManager Shared { get; } = new();

    public const string WindowName = "Rewind.MessageWindow";
    public static readonly int ActivateMessage = RegisterWindowMessage("Rewind.Activate");

    HwndSource? window;
    readonly Dictionary<int, Action> handlers = new();
    readonly List<int> registered = new();
    List<(Hotkey, Action)> bindings = new();
    bool suspended;

    public event Action<RemoteAction>? ActivateRequested;

    public void EnsureWindow()
    {
        if (window != null) return;
        var p = new HwndSourceParameters(WindowName) { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 };
        window = new HwndSource(p);
        window.AddHook(WndProc);
    }

    public List<Hotkey> Register(List<(Hotkey, Action)> bindings)
    {
        this.bindings = bindings;
        return suspended ? new() : Apply();
    }

    public void Suspend() { suspended = true; UnregisterAll(); }
    public void Resume() { suspended = false; Apply(); }

    List<Hotkey> Apply()
    {
        EnsureWindow();
        UnregisterAll();
        var failed = new List<Hotkey>();
        for (int i = 0; i < bindings.Count; i++)
        {
            var (hk, action) = bindings[i];
            if (!hk.IsSet) continue;
            int id = i + 1;
            if (Native.RegisterHotKey(window!.Handle, id, hk.Modifiers | MOD_NOREPEAT, hk.Vk))
            {
                registered.Add(id);
                handlers[id] = action;
            }
            else failed.Add(hk);
        }
        return failed;
    }

    void UnregisterAll()
    {
        if (window == null) return;
        foreach (var id in registered) Native.UnregisterHotKey(window.Handle, id);
        registered.Clear();
        handlers.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception e) { Log.Error("hotkey", e); }
        }
        else if (msg == ActivateMessage)
        {
            handled = true;
            var a = (RemoteAction)wParam.ToInt32();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ActivateRequested?.Invoke(a));
        }
        return IntPtr.Zero;
    }

    public static bool SignalRunningInstance(RemoteAction action)
    {
        var h = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, WindowName);
        return h != IntPtr.Zero && PostMessage(h, ActivateMessage, new IntPtr((int)action), IntPtr.Zero);
    }

    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
