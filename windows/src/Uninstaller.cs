using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Rewind;

/// Settings → Uninstall Rewind…: removes every trace of Rewind except the clips folder.
/// - In the app: finishes a recording, removes the "Open Rewind when Windows starts" entry and moves downloaded
///   Rewind installers to the Recycle Bin.
/// - After it quits, a small helper runs Setup's uninstaller (or deletes this copy when it wasn't installed with
///   Setup) and deletes Rewind's settings, log and temporary files, so nothing written on the way out brings them back.
static class Uninstaller
{
    sealed record Plan(List<string> Data, List<string> Installers, string? UninstallCommand, string? PortableExe);

    static readonly string[] DataDirs =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rewind"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind"),
    };

    /// Rewind's folders, except one that holds the clips folder (if someone saved clips inside it).
    static IEnumerable<string> OwnDirs()
    {
        var keep = Path.TrimEndingDirectorySeparator(Settings.Shared.OutputDirectory) + Path.DirectorySeparatorChar;
        return DataDirs.Where(d => !keep.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    static Plan MakePlan()
    {
        var data = OwnDirs().Where(Directory.Exists).ToList();
        try { data.AddRange(Directory.GetDirectories(Path.GetTempPath(), "Rewind-update-*")); } catch { }

        var keep = Path.TrimEndingDirectorySeparator(Settings.Shared.OutputDirectory);
        var pattern = new Regex(@"^Rewind-[0-9]+(\.[0-9]+)*-Windows-(x64|arm64)(-Setup\.exe|\.zip)$", RegexOptions.IgnoreCase);
        var installers = new[] { DownloadsFolder(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .SelectMany(d => { try { return Directory.GetFiles(d); } catch { return Array.Empty<string>(); } })
            .Where(f => pattern.IsMatch(Path.GetFileName(f)) && !f.StartsWith(keep + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? uninstall = null, portable = null;
        if (Updater.Installation() is { } install && UninstallerPath(install.AllUsers) is { } u) uninstall = u;
        // Only ever a published single-file Rewind.exe, never a build folder (which has Rewind.dll beside it).
        else if (Environment.ProcessPath is { } exe && Path.GetFileName(exe).Equals("Rewind.exe", StringComparison.OrdinalIgnoreCase)
                 && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Rewind.dll")))
            portable = exe;
        return new Plan(data, installers, uninstall, portable);
    }

    static string? UninstallerPath(bool allUsers)
    {
        using var root = RegistryKey.OpenBaseKey(allUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5C82EE1B-8C86-4157-966F-0F973D4C317F}_is1");
        var path = (key?.GetValue("UninstallString") as string)?.Trim('"');
        return path != null && File.Exists(path) ? path : null;
    }

    static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // Asking

    public static void ConfirmAndRun(Window owner)
    {
        var p = MakePlan();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Short(string path) => path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;

        var text = new StringBuilder("This permanently deletes Rewind, its settings and log, and its \"Open Rewind when Windows starts\" entry. It can't be undone.");
        if (p.UninstallCommand == null && p.PortableExe == null)
            text.Append("\n\nThis copy of Rewind isn't one Rewind can delete (it looks like a build folder), so it stays.");
        if (p.Installers.Count > 0)
        {
            text.Append("\n\nThese downloaded installers go to the Recycle Bin:\n");
            text.Append(string.Join("\n", p.Installers.Take(6).Select(f => "• " + Short(f))));
            if (p.Installers.Count > 6) text.Append($"\n• and {p.Installers.Count - 6} more");
        }
        text.Append($"\n\nYour clips in {Short(Settings.Shared.OutputDirectory)} are kept.");

        var uninstall = new Forms.TaskDialogButton("Uninstall");
        var page = new Forms.TaskDialogPage
        {
            Caption = "Rewind",
            Heading = "Uninstall Rewind?",
            Text = text.ToString(),
            Icon = Forms.TaskDialogIcon.Warning,
            Buttons = { uninstall, Forms.TaskDialogButton.Cancel },
            DefaultButton = Forms.TaskDialogButton.Cancel,
        };
        if (Forms.TaskDialog.ShowDialog(new WindowInteropHelper(owner).Handle, page) != uninstall) return;
        owner.Close();
        Run(p);
    }

    // Removing

    static async void Run(Plan p)
    {
        OverlayController.Hide();
        // A recording in progress is saved like a normal stop; replays still being written finish.
        await Controller.Shared.ShutdownAsync();

        try { LoginItem.Set(false); } catch (Exception e) { Log.Error("uninstall: startup entry", e); }
        var leftOver = p.Installers.Where(f => !Cleanup.Recycle(f)).ToList();

        StartHelper(p);

        if (leftOver.Count == 0)
            Toast.Show("Rewind is uninstalled", "Your clips are still in your clips folder.", Sym.Check, Theme.Accent);
        else
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{leftOver[0]}\"") { UseShellExecute = true });
            Toast.Show("Rewind is uninstalled, almost",
                $"Windows didn't let Rewind move {(leftOver.Count == 1 ? "1 installer" : $"{leftOver.Count} installers")} to the Recycle Bin. It's selected in File Explorer.",
                Sym.Warning, Theme.Orange, duration: 6);
        }
        await Task.Delay(leftOver.Count == 0 ? 1500 : 5000);
        Environment.Exit(0); // no normal quit: nothing should be saved now
    }

    /// A hidden PowerShell that waits for Rewind to exit, then removes this copy and Rewind's own files.
    static void StartHelper(Plan p)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        script.AppendLine($"Wait-Process -Id {Environment.ProcessId} -Timeout 30");
        if (p.UninstallCommand != null)
            script.AppendLine($"Start-Process -FilePath {Q(p.UninstallCommand)} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait");
        if (p.PortableExe != null)
            script.AppendLine($"for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath {Q(p.PortableExe)}); $i++) {{ Remove-Item -LiteralPath {Q(p.PortableExe)} -Force; Start-Sleep -Milliseconds 250 }}");
        foreach (var d in p.Data.Concat(OwnDirs()).Distinct(StringComparer.OrdinalIgnoreCase))
            script.AppendLine($"Remove-Item -LiteralPath {Q(d)} -Recurse -Force");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
