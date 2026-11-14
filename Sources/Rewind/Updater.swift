import AppKit
import Security
import Foundation

/// Checks GitHub for a newer release and installs it in place: downloads the release's .dmg into a temporary
/// folder, mounts it out of sight, checks the app inside is signed with the same certificate as this copy,
/// copies it out, swaps the app and relaunches.
@MainActor
final class Updater: ObservableObject {
    static let shared = Updater()

    static let repo = "browneie/Rewind"

    struct Release: Equatable {
        let version: String
        let dmgURL: URL
        let pageURL: URL?
    }

    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(Release)
        case downloading
        case installing
        case failed(String)
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var lastChecked: Date?

    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    /// GitHub's "latest release" API, or a test feed passed with --update-feed <url>.
    private var feedURL: URL {
        if let i = CommandLine.arguments.firstIndex(of: "--update-feed"), CommandLine.arguments.count > i + 1,
           let url = URL(string: CommandLine.arguments[i + 1]) { return url }
        return URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!
    }

    // MARK: Checking

    /// `userInitiated`: from the Settings button, so "you're up to date" and errors are shown too.
    func check(userInitiated: Bool) {
        if case .checking = state { return }
        if case .downloading = state { return }
        if case .installing = state { return }
        state = .checking
        let url = feedURL
        Task {
            do {
                let release = try await Self.fetchLatest(url)
                lastChecked = Date()
                guard let release, Self.isNewer(release.version, than: currentVersion) else {
                    state = .upToDate
                    if !userInitiated { Toast.release() }
                    return
                }
                state = .available(release)
                if !userInitiated {
                    // Stays up for 90 s: click to update, ✕ (or ignore it) to skip. Settings → Updates still offers it later.
                    Toast.show("Rewind \(release.version) is available", "Click to update now · ✕ for later",
                               icon: "arrow.down.circle.fill", tint: Theme.accent, duration: 90, closable: true,
                               pinned: true) {
                        Updater.shared.install(release)
                    }
                    Toast.release()
                }
                if CommandLine.arguments.contains("--update-auto-install") { install(release) }
            } catch {
                state = userInitiated ? .failed("Couldn't check for updates: \(error.localizedDescription)") : .idle
                if !userInitiated { Toast.release() }
            }
        }
    }

    private static func fetchLatest(_ url: URL) async throws -> Release? {
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Rewind-Updater", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: req)
        if let http = response as? HTTPURLResponse, http.statusCode == 404 { return nil } // no releases yet
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw RewindError("GitHub answered \(http.statusCode).")
        }
        struct Feed: Decodable {
            struct Asset: Decodable { let name: String; let browser_download_url: URL }
            let tag_name: String
            let html_url: URL?
            let assets: [Asset]
        }
        let feed = try JSONDecoder().decode(Feed.self, from: data)
        let version = feed.tag_name.hasPrefix("v") ? String(feed.tag_name.dropFirst()) : feed.tag_name
        guard let dmg = feed.assets.first(where: { $0.name == "Rewind-\(version)-macOS.dmg" }) else {
            throw RewindError("Release \(version) has no .dmg.")
        }
        return Release(version: version, dmgURL: dmg.browser_download_url, pageURL: feed.html_url)
    }

    /// Numeric dotted-version comparison: 1.10.0 > 1.9.2.
    nonisolated static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }, pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // MARK: Installing

    func install(_ release: Release) {
        if case .downloading = state { return }
        if case .installing = state { return }
        let dest = Bundle.main.bundleURL
        if dest.path.contains("/AppTranslocation/") {
            fail("Move Rewind to your Applications folder first, then open it from there and try again.")
            return
        }
        guard FileManager.default.isWritableFile(atPath: dest.deletingLastPathComponent().path) else {
            fail("Rewind can't replace itself in \(dest.deletingLastPathComponent().path).")
            return
        }
        state = .downloading
        Toast.show("Downloading Rewind \(release.version)…", nil, icon: "arrow.down.circle", tint: Theme.accent)
        Task {
            do {
                let newApp = try await Self.downloadAndVerify(release)
                state = .installing
                try Self.launchSwap(newApp: newApp, dest: dest)
                Toast.show("Installing Rewind \(release.version)", "Rewind will reopen in a moment.",
                           icon: "arrow.triangle.2.circlepath", tint: Theme.accent)
                try? await Task.sleep(for: .milliseconds(800))
                NSApp.terminate(nil) // finishes a running recording first, then the helper swaps and relaunches
            } catch {
                fail("Update failed: \(error.localizedDescription)")
            }
        }
    }

    private func fail(_ message: String) {
        state = .failed(message)
        Toast.show("Couldn't update Rewind", message, icon: "exclamationmark.triangle.fill", tint: .orange, duration: 8)
    }

    /// Downloads into a fresh temporary folder, copies the app out of the .dmg and checks it; returns its URL.
    nonisolated private static func downloadAndVerify(_ release: Release) async throws -> URL {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("Rewind-update-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        do {
            return try await verify(release, in: work)
        } catch {
            try? fm.removeItem(at: work) // nothing half-downloaded is left behind
            throw error
        }
    }

    nonisolated private static func verify(_ release: Release, in work: URL) async throws -> URL {
        let fm = FileManager.default
        let (tmp, _) = try await URLSession.shared.download(from: release.dmgURL)
        let dmg = work.appendingPathComponent("update.dmg")
        try fm.moveItem(at: tmp, to: dmg)

        // Mount read-only at a private folder, hidden from Finder, and copy the app out.
        let mount = work.appendingPathComponent("mnt")
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        try run("/usr/bin/hdiutil", ["attach", dmg.path, "-nobrowse", "-readonly", "-noautoopen", "-noverify",
                                     "-mountpoint", mount.path])
        let app = work.appendingPathComponent("unpacked/Rewind.app")
        do {
            try run("/usr/bin/ditto", [mount.appendingPathComponent("Rewind.app").path, app.path])
        } catch {
            eject(mount)
            throw RewindError("The update's disk image doesn't contain Rewind.")
        }
        eject(mount)

        // Must be Rewind, the advertised version, intact, and signed with the same certificate as this copy.
        guard let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")),
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info["CFBundleShortVersionString"] as? String == release.version else {
            throw RewindError("The downloaded app isn't Rewind \(release.version).")
        }
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        try checkSameSigner(app)
        return app
    }

    /// Ejects the mounted image, retrying once with force if macOS still has it busy.
    nonisolated private static func eject(_ mount: URL) {
        if (try? run("/usr/bin/hdiutil", ["detach", mount.path])) == nil {
            Thread.sleep(forTimeInterval: 1)
            _ = try? run("/usr/bin/hdiutil", ["detach", mount.path, "-force"])
        }
    }

    /// The new app has to satisfy this copy's own designated requirement, i.e. be signed with the same
    /// certificate. Only the Mac holding that certificate's private key can produce such an app.
    nonisolated private static func checkSameSigner(_ app: URL) throws {
        var me: SecCode?, requirement: SecRequirement?, newCode: SecStaticCode?
        guard SecCodeCopySelf([], &me) == errSecSuccess, let me,
              SecCodeCopyDesignatedRequirement(me as! SecStaticCode, [], &requirement) == errSecSuccess,
              let requirement else {
            throw RewindError("This copy of Rewind isn't signed, so it can't check updates.")
        }
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &newCode) == errSecSuccess, let newCode,
              SecStaticCodeCheckValidity(newCode, SecCSFlags(rawValue: kSecCSCheckAllArchitectures), requirement) == errSecSuccess else {
            throw RewindError("The download isn't signed by Rewind's developer. Nothing was installed.")
        }
    }

    /// Starts a detached helper that waits for Rewind to quit, swaps the app and opens the new one.
    private static func launchSwap(newApp: URL, dest: URL) throws {
        let script = newApp.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("swap.sh")
        try """
        #!/bin/sh
        PID="$1"; DEST="$2"; NEW="$3"; RELAUNCH="$4"
        i=0; while kill -0 "$PID" 2>/dev/null && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done
        OLD="$DEST.old-$$"
        mv "$DEST" "$OLD" || exit 1
        if mv "$NEW" "$DEST"; then rm -rf "$OLD"; else mv "$OLD" "$DEST"; fi
        rm -rf "$(dirname "$(dirname "$NEW")")"
        [ "$RELAUNCH" = 1 ] && /usr/bin/open "$DEST"
        exit 0
        """.write(to: script, atomically: true, encoding: .utf8)
        let relaunch = CommandLine.arguments.contains("--update-no-relaunch") ? "0" : "1"
        let quoted = [script.path, "\(ProcessInfo.processInfo.processIdentifier)", dest.path, newApp.path, relaunch]
            .map { "'" + $0.replacingOccurrences(of: "'", with: "'\\''") + "'" }.joined(separator: " ")
        // nohup + & so the helper outlives Rewind.
        try run("/bin/sh", ["-c", "/usr/bin/nohup /bin/sh \(quoted) >/dev/null 2>&1 &"])
    }

    @discardableResult
    nonisolated private static func run(_ tool: String, _ args: [String]) throws -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try p.run()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            throw RewindError("\((tool as NSString).lastPathComponent) failed (\(p.terminationStatus)).")
        }
        return p.terminationStatus
    }
}
