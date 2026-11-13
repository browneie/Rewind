using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Rewind;

static class App
{
    public static bool CapturableWindows { get; private set; }
    static Tray? tray;

    public static int Run(string[] args)
    {
        CapturableWindows = args.Contains("--capturable");
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetColorMode(Forms.SystemColorMode.System);
        MF.Startup();

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("UI", e.Exception);
            e.Handled = true;
            Toast.Error("Something went wrong", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception ?? new Exception("unknown"));
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };
        app.Startup += (_, _) => tray = new Tray(args);
        app.Exit += (_, _) => tray?.Dispose();
        Log.Info($"Rewind {Assembly.GetExecutingAssembly().GetName().Version} starting on {Environment.OSVersion}");
        return app.Run();
    }

    public static bool SignalRunningInstance(RemoteAction action) => HotkeyManager.SignalRunningInstance(action);

    public static async void Quit()
    {
        OverlayController.Hide();
        await Controller.Shared.ShutdownAsync();
        tray?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}

sealed class Tray : IDisposable
{
    readonly Controller c = Controller.Shared;
    readonly Settings s = Settings.Shared;
    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu = new();
    readonly DispatcherTimer settingsDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Drawing.Icon? currentIcon;

    public Tray(string[] args)
    {
        Build();
        menu.Opening += (_, _) => Build();
        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Text = "Rewind", Visible = true };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(icon, null);
        };
        UpdateIcon();
        c.StateChanged += UpdateIcon;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

        HotkeyManager.Shared.EnsureWindow();
        HotkeyManager.Shared.ActivateRequested += a =>
        {
            if (a == RemoteAction.Quit) App.Quit();
            else if (a.AsHotkeyAction() is { } h) c.Perform(h);
            else SettingsWindow.ShowShared();
        };
        RegisterHotkeys(reportConflicts: true);
        s.HotkeysChanged += () => RegisterHotkeys(reportConflicts: true);

        settingsDebounce.Tick += (_, _) => { settingsDebounce.Stop(); c.SettingsChanged(); };
        s.Changed += () => { settingsDebounce.Stop(); settingsDebounce.Start(); };

        _ = CaptureEngine.RequestBorderlessAsync();

        var ui = Dispatcher.CurrentDispatcher;
        if (args.Contains("--demo-toast"))
            After(4, () => Toast.Show("Replay saved", "2:00 · Replay 2026-11-13 at 21.04.11.mp4", Sym.Check, Theme.Replay, duration: 30));
        if (args.Contains("--settings")) SettingsWindow.ShowShared();
        if (args.Contains("--overlay")) After(1.5, OverlayController.Show);

        if (s.StartReplayOnLaunch) c.StartReplay();
        else Toast.Show("Rewind is running", $"{s.Hotkey(HotkeyAction.ToggleOverlay).Display} opens the overlay", Sym.Record, Theme.Green);
    }

    static void After(double seconds, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateIcon);
    }

    void RegisterHotkeys(bool reportConflicts)
    {
        var bindings = HotkeyActions.All.Select(a => (s.Hotkey(a), (Action)(() => c.Perform(a)))).ToList();
        var failed = HotkeyManager.Shared.Register(bindings);
        if (failed.Count > 0) Log.Info("hotkeys already taken: " + string.Join(", ", failed.Select(h => h.Display)));
        if (reportConflicts && failed.Count > 0)
            Toast.Show("Some hotkeys are taken",
                string.Join(", ", failed.Select(h => h.Display)) + (failed.Count == 1 ? " is" : " are") +
                " already used by another app. Change it in Settings.",
                Sym.Keyboard, Theme.Orange, duration: 6, onClick: SettingsWindow.ShowShared);
    }


    void Build()
    {
        menu.Items.Clear();
        string status = c.Recording && c.RecordingStart is { } start
            ? $"● Recording, {Format.Duration((DateTime.Now - start).TotalSeconds)}"
            : c.ReplayActive ? $"Instant Replay on, {Format.Duration(c.BufferedSeconds)} buffered" : "Idle";
        menu.Items.Add(new Forms.ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Save Replay", HotkeyAction.SaveReplay, c.SaveReplay).Enabled = c.ReplayActive;
        Add(c.ReplayActive ? "Turn Off Instant Replay" : "Turn On Instant Replay", HotkeyAction.ToggleReplay, c.ToggleReplay);
        Add(c.Recording ? "Stop Recording" : "Start Recording", HotkeyAction.ToggleRecording, c.ToggleRecording);
        Add("Take Screenshot", HotkeyAction.Screenshot, c.Screenshot);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Show Overlay", HotkeyAction.ToggleOverlay, OverlayController.Show);
        Item("Open Clips Folder", c.OpenClipsFolder);
        Item("Settings…", SettingsWindow.ShowShared);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item("Quit Rewind", App.Quit);
    }

    Forms.ToolStripMenuItem Add(string title, HotkeyAction action, Action run)
    {
        var i = Item(title, run);
        i.ShortcutKeyDisplayString = s.Hotkey(action).Display;
        return i;
    }

    Forms.ToolStripMenuItem Item(string title, Action run)
    {
        // Menu clicks arrive from WinForms; hand them to WPF once the menu has closed.
        var i = new Forms.ToolStripMenuItem(title);
        i.Click += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(run);
        menu.Items.Add(i);
        return i;
    }


    internal enum State { Idle, Replay, Recording }

    void UpdateIcon()
    {
        var state = c.Recording ? State.Recording : c.ReplayActive ? State.Replay : State.Idle;
        var old = currentIcon;
        currentIcon = Draw(state, LightTaskbar(), Forms.SystemInformation.SmallIconSize.Width);
        icon.Icon = currentIcon;
        icon.Text = state switch
        {
            State.Recording => "Rewind · Recording",
            State.Replay => "Rewind · Instant Replay on",
            _ => "Rewind",
        };
        old?.Dispose();
    }

    static bool LightTaskbar()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    internal static Drawing.Icon Draw(State state, bool light, int px)
    {
        using var bmp = new Drawing.Bitmap(px, px, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.ScaleTransform(px / 100f, px / 100f);
            var fg = light ? Drawing.Color.FromArgb(25, 25, 25) : Drawing.Color.White;
            using var pen = new Drawing.Pen(fg, 10) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            using var brush = new Drawing.SolidBrush(fg);
            switch (state)
            {
                case State.Idle:
                    g.DrawEllipse(pen, 11, 11, 78, 78);
                    g.FillEllipse(brush, 31, 31, 38, 38);
                    break;
                case State.Replay:
                    var pts = new List<Drawing.PointF>();
                    double a0 = Math.Atan2(34.0 - 50, 30.5 - 50), sweep = 309.4 * Math.PI / 180;
                    for (int i = 0; i <= 60; i++)
                    {
                        double a = a0 - sweep * i / 60;
                        pts.Add(new Drawing.PointF((float)(50 + 37 * Math.Cos(a)), (float)(54 + 37 * Math.Sin(a))));
                    }
                    g.DrawLines(pen, pts.ToArray());
                    g.FillPolygon(brush, new[] { new Drawing.PointF(36, 17), new Drawing.PointF(55, 3), new Drawing.PointF(55, 31) });
                    g.FillEllipse(brush, 34, 38, 32, 32);
                    break;
                case State.Recording:
                    using (var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x45, 0x3A)))
                        g.FillEllipse(red, 6, 6, 88, 88);
                    using (var white = new Drawing.SolidBrush(Drawing.Color.White))
                        g.FillEllipse(white, 32, 32, 36, 36);
                    break;
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        icon.Visible = false;
        icon.Dispose();
        currentIcon?.Dispose();
    }
}
