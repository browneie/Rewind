using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace Rewind;

/// Checks GitHub for a newer release and installs it: downloads the release's Windows installer into a temporary
/// folder, checks it's signed with the same certificate as this copy, runs it silently and lets it reopen Rewind.
sealed class Updater
{
    public static readonly Updater Shared = new();

    public const string Repo = "browneie/Rewind";
    const string AppId = "{5C82EE1B-8C86-4157-966F-0F973D4C317F}_is1";

    public sealed record Release(string Version, Uri InstallerUrl, Uri? PageUrl);

    public enum Phase { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    public Phase State { get; private set; }
    public Release? Offer { get; private set; }
    public string Message { get; private set; } = "";
    public event Action? Changed;

    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    static string InstallerName(string version) => $"Rewind-{version}-Windows-{Arch}-Setup.exe";

    static readonly string[] Args = Environment.GetCommandLineArgs();

    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };

    static Updater()
    {
        foreach (var c in new[] { Http, Download }) c.DefaultRequestHeaders.UserAgent.ParseAdd("Rewind-Updater");
    }

    /// GitHub's "latest release" API, or a test feed passed with --update-feed <url>.
    static Uri FeedUrl
    {
        get
        {
            int i = Array.IndexOf(Args, "--update-feed");
            if (i >= 0 && i + 1 < Args.Length && Uri.TryCreate(Args[i + 1], UriKind.Absolute, out var u)) return u;
            return new Uri($"https://api.github.com/repos/{Repo}/releases/latest");
        }
    }

    void Set(Phase p, string message = "")
    {
        State = p;
        Message = message;
        Changed?.Invoke();
    }

    // Checking

    /// `userInitiated`: from the Settings button, so "you're up to date" and errors are shown too.
    public async void Check(bool userInitiated)
    {
        if (State is Phase.Checking or Phase.Downloading or Phase.Installing) return;
        Set(Phase.Checking);
        try
        {
            var release = await FetchLatest();
            if (release == null || !IsNewer(release.Version, CurrentVersion))
            {
                Set(Phase.UpToDate);
                if (!userInitiated) Toast.Release();
                return;
            }
            Offer = release;
            Set(Phase.Available);
            if (!userInitiated)
            {
                // Stays up for 90 s: click to update, ✕ (or ignore it) to skip. Settings → Updates still offers it later.
                Toast.Show($"Rewind {release.Version} is available", "Click to update now · ✕ for later", Sym.Download, Theme.Accent,
                    duration: 90, onClick: () => Install(release), closable: true, pinned: true);
                Toast.Release();
            }
            if (Args.Contains("--update-auto-install")) Install(release);
        }
        catch (Exception e)
        {
            Log.Error("update check", e);
            if (userInitiated) Set(Phase.Failed, e is RewindException ? e.Message : "Couldn't check for updates: " + e.Message);
            else { Set(Phase.Idle); Toast.Release(); }
        }
    }

    static async Task<Release?> FetchLatest()
    {
        var url = FeedUrl;
        if (url.IsFile) return Parse(await File.ReadAllTextAsync(url.LocalPath));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        // The API allows 60 checks an hour per network; past that, read the version from the release page's address.
        if ((res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) && url.Host == "api.github.com")
            return await FromReleasePage();
        if (!res.IsSuccessStatusCode) throw new RewindException($"GitHub answered {(int)res.StatusCode}.");
        return Parse(await res.Content.ReadAsStringAsync());
    }

    static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = Version(root.GetProperty("tag_name").GetString() ?? "");
        Uri? page = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var p) ? p : null;
        var name = InstallerName(version);
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == name && Uri.TryCreate(a.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var dl))
                return new Release(version, dl, page);
        if (!IsNewer(version, CurrentVersion)) return new Release(version, page ?? new Uri("about:blank"), page);
        throw new RewindException($"Rewind {version} is out, but not for Windows yet.");
    }

    static async Task<Release?> FromReleasePage()
    {
        using var res = await Http.GetAsync($"https://github.com/{Repo}/releases/latest");
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        var tag = res.Headers.Location?.ToString().Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(tag)) throw new RewindException("GitHub didn't say which release is the latest.");
        var version = Version(tag);
        var page = new Uri($"https://github.com/{Repo}/releases/tag/{tag}");
        var dl = new Uri($"https://github.com/{Repo}/releases/download/{tag}/{InstallerName(version)}");
        if (!IsNewer(version, CurrentVersion)) return new Release(version, dl, page);
        using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, dl));
        if (head.StatusCode == HttpStatusCode.NotFound) throw new RewindException($"Rewind {version} is out, but not for Windows yet.");
        return new Release(version, dl, page);
    }

    static string Version(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

    /// Numeric dotted-version comparison: 1.10.0 > 1.9.2.
    public static bool IsNewer(string a, string b)
    {
        int[] Parts(string v) => v.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    // Installing

    /// Where Setup put this copy, and whether that was for all users; null when Rewind runs from somewhere else
    /// (a zip, a build folder), which Setup can't update.
    public static (string Dir, bool AllUsers)? Installation()
    {
        var me = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        foreach (var (hive, allUsers) in new[] { (RegistryHive.CurrentUser, false), (RegistryHive.LocalMachine, true) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}");
            if (key?.GetValue("InstallLocation") is string dir &&
                string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(me), StringComparison.OrdinalIgnoreCase))
                return (dir, allUsers);
        }
        return null;
    }

    public async void Install(Release release)
    {
        if (State is Phase.Downloading or Phase.Installing) return;
        if (Installation() is not { } install)
        {
            Fail("This copy of Rewind wasn't installed with Setup, so it can't replace itself. Download the new version from GitHub instead.");
            return;
        }
        Set(Phase.Downloading);
        Toast.Show($"Downloading Rewind {release.Version}…", null, Sym.Download, Theme.Accent);
        try
        {
            var setup = await Task.Run(() => DownloadAndVerify(release));
            Set(Phase.Installing);
            // Setup closes Rewind the usual way (a recording in progress is saved), replaces it and reopens it.
            var mode = install.AllUsers ? "/ALLUSERS" : "/CURRENTUSER";
            Process.Start(new ProcessStartInfo(setup, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1 {mode}") { UseShellExecute = true });
            Toast.Show($"Installing Rewind {release.Version}", "Rewind will reopen in a moment.", Sym.Sync, Theme.Accent);
            await Task.Delay(800);
            App.Quit();
        }
        catch (Exception e)
        {
            Log.Error("update", e);
            Fail("Update failed: " + e.Message);
        }
    }

    void Fail(string message)
    {
        Set(Phase.Failed, message);
        Toast.Show("Couldn't update Rewind", message, Sym.Warning, Theme.Orange, duration: 8);
    }

    /// Downloads into a fresh temporary folder and checks the installer; returns its path.
    static async Task<string> DownloadAndVerify(Release release)
    {
        var work = Path.Combine(Path.GetTempPath(), "Rewind-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var file = Path.Combine(work, InstallerName(release.Version));
            if (release.InstallerUrl.IsFile) File.Copy(release.InstallerUrl.LocalPath, file);
            else
            {
                using var res = await Download.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();
                await using var output = File.Create(file);
                await res.Content.CopyToAsync(output);
            }
            Verify(file, release.Version);
            return file;
        }
        catch
        {
            try { Directory.Delete(work, recursive: true); } catch { } // nothing half-downloaded is left behind
            throw;
        }
    }

    /// Must be Rewind's installer for the advertised version, intact, and signed with the same certificate as this
    /// copy. Only the PC holding that certificate's private key can produce such a file.
    static void Verify(string file, string version)
    {
        var info = FileVersionInfo.GetVersionInfo(file);
        var v = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        if (info.ProductName?.Trim() != "Rewind" || IsNewer(v, version) || IsNewer(version, v))
            throw new RewindException($"The download isn't the Rewind {version} installer.");

        var mine = Signer(Environment.ProcessPath!) ?? throw new RewindException("This copy of Rewind isn't signed, so it can't check updates.");
        var theirs = Signer(file);
        if (theirs == null || !theirs.Equals(mine, StringComparison.OrdinalIgnoreCase) || !SignatureIntact(file))
            throw new RewindException("The download isn't signed by Rewind's developer. Nothing was installed.");
    }

    /// Thumbprint of the certificate a file is signed with, or null if it isn't signed.
    static string? Signer(string file)
    {
#pragma warning disable SYSLIB0057
        try { return X509Certificate.CreateFromSignedFile(file).GetCertHashString(); }
#pragma warning restore SYSLIB0057
        catch { return null; }
    }

    /// The signature matches the file's contents. Rewind's own certificate isn't issued by an authority Windows
    /// trusts, which is fine here: who signed it was checked above by thumbprint.
    static bool SignatureIntact(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /* file */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x1000 /* cached URLs only: no network */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            uint r = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            const uint UntrustedRoot = 0x800B0109;
            return r == 0 || r == UntrustedRoot;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    /// Leftover installers from earlier updates (Setup was still running from them, so they couldn't go then).
    public static void CleanUpOldDownloads()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "Rewind-update-*"))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
