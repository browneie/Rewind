namespace Rewind;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        int inspect = Array.IndexOf(args, "--inspect");
        if (inspect >= 0 && args.Length > inspect + 1)
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            return SelfTest.InspectFile(args[inspect + 1]);
        }
        int i = Array.IndexOf(args, "--selftest");
        if (i >= 0)
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            var dir = args.Length > i + 1 ? args[i + 1] : Path.Combine(Path.GetTempPath(), "rewind-selftest");
            try { return SelfTest.Run(Path.GetFullPath(dir)); }
            catch (Exception e) { Console.WriteLine($"FAIL: {e}"); return 1; }
        }
        if (args.Contains("--bench-load")) return Bench.LoadWindow();
        if (args.Contains("--quality-test"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            try { return QualityTest.Run(args); }
            catch (Exception e) { Console.WriteLine($"FAIL: {e}"); return 1; }
        }
        if (args.Contains("--fake-game")) return DevTools.FakeGame();
        if (args.Contains("--probe-overlay-cursor"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            return DevTools.ProbeOverlayCursor();
        }
        if (args.Contains("--bench"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            try { return Bench.Run(args); }
            catch (Exception e) { Console.WriteLine($"FAIL: {e}"); return 1; }
        }
        if (args.Contains("--probe-capture"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            try { return DevTools.ProbeCapture(); }
            catch (Exception e) { Console.WriteLine($"FAIL: {e}"); return 1; }
        }
        int pp = Array.IndexOf(args, "--probe-player");
        if (pp >= 0 && args.Length > pp + 1)
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            return DevTools.ProbePlayer(args[pp + 1]);
        }
        if (args.Contains("--probe-audio"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            return DevTools.ProbeAudio();
        }
        int r = Array.IndexOf(args, "--render-ui");
        if (r >= 0)
        {
            var dir = args.Length > r + 1 ? args[r + 1] : Path.Combine(Path.GetTempPath(), "rewind-ui");
            int b = Array.IndexOf(args, "--background");
            int k = Array.IndexOf(args, "--clips");
            return DevTools.RenderUi(Path.GetFullPath(dir), b >= 0 && args.Length > b + 1 ? args[b + 1] : null,
                                     k >= 0 && args.Length > k + 1 ? args[k + 1] : null);
        }

        var action = RemoteAction.ShowSettings;
        int a = Array.IndexOf(args, "--action");
        if (a >= 0 && args.Length > a + 1 && !RemoteActions.Names.TryGetValue(args[a + 1], out action))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            Console.WriteLine($"Unknown action. Use one of: {string.Join(", ", RemoteActions.Names.Keys)}");
            return 2;
        }

        using var single = new Mutex(true, @"Local\Rewind-ScreenRecorder", out bool first);
        if (!first)
        {
            for (int tries = 0; tries < 20 && !App.SignalRunningInstance(action); tries++) Thread.Sleep(250);
            return 0;
        }
        if (a >= 0) return 0;
        return App.Run(args);
    }
}
