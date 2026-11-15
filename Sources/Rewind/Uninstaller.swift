import AppKit
import ServiceManagement

/// Settings → Uninstall Rewind…: removes every trace of Rewind except the clips folder.
/// - In the app: finishes a recording, removes the login item, resets Rewind's privacy permissions, and
///   permanently deletes other copies of Rewind, downloaded Rewind installers and finally this copy.
/// - After it quits, a small helper deletes Rewind's settings, caches and temporary files, so nothing the app
///   writes on its way out can bring them back.
@MainActor
enum Uninstaller {
    struct Plan {
        /// Settings, caches and temp files: deleted permanently (they're Rewind's own, never user content).
        var data: [URL] = []
        /// Other Rewind.app copies and downloaded installers: deleted permanently.
        var trash: [URL] = []
        /// This copy, deleted last.
        var app: URL
    }

    static var bundleID: String { Bundle.main.bundleIdentifier ?? "local.rewind.recorder" }

    // MARK: Finding traces

    static func plan() -> Plan {
        let fm = FileManager.default
        let home = fm.homeDirectoryForCurrentUser
        let lib = home.appendingPathComponent("Library")
        let id = bundleID
        var p = Plan(app: Bundle.main.bundleURL)

        let candidates = [
            "Preferences/\(id).plist",
            "Caches/\(id)",
            "HTTPStorages/\(id)",
            "HTTPStorages/\(id).binarycookies",
            "Saved Application State/\(id).savedState",
            "WebKit/\(id)",
            "Application Support/\(id)",
            "Application Support/Rewind",
            "Logs/Rewind",
        ].map { lib.appendingPathComponent($0) }
        p.data = candidates.filter { fm.fileExists(atPath: $0.path) }
        if let temp = try? fm.contentsOfDirectory(at: fm.temporaryDirectory, includingPropertiesForKeys: nil) {
            p.data += temp.filter { $0.lastPathComponent.hasPrefix("Rewind-update-") }
        }

        let keep = Settings.shared.outputDirectory.standardizedFileURL.path
        let me = p.app.standardizedFileURL.path
        func safe(_ url: URL) -> Bool {
            let path = url.standardizedFileURL.path
            return path != me && !path.contains("/.Trash/") && !path.hasPrefix(keep + "/") && path != keep
                && (path.hasPrefix(home.path + "/") || path.hasPrefix("/Applications/"))
        }
        p.trash = otherCopies().filter(safe) + installers(in: home).filter(safe)
        return p
    }

    /// Every other Rewind.app on this Mac that Spotlight knows about (same bundle ID).
    private static func otherCopies() -> [URL] {
        let out = run("/usr/bin/mdfind", ["kMDItemCFBundleIdentifier == '\(bundleID)'"]) ?? ""
        return out.split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
            .filter { $0.pathExtension == "app" && FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Rewind's own release downloads in Downloads and on the Desktop ("Rewind-1.6.0-macOS.dmg" etc.).
    private static func installers(in home: URL) -> [URL] {
        let pattern = #"^Rewind-[0-9]+(\.[0-9]+)*-macOS\.(dmg|zip)$"#
        return ["Downloads", "Desktop"].flatMap { folder -> [URL] in
            let dir = home.appendingPathComponent(folder)
            let items = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
            return items.filter { $0.lastPathComponent.range(of: pattern, options: .regularExpression) != nil }
        }
    }

    // MARK: Asking

    static func confirmAndRun() {
        let p = plan()
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Uninstall Rewind?"
        var text = "This permanently deletes Rewind, its settings and caches, and removes its Screen Recording and Microphone permissions and its login item. It can't be undone."
        if !p.trash.isEmpty {
            let names = p.trash.prefix(6).map { "• " + $0.path.replacingOccurrences(of: NSHomeDirectory(), with: "~") }
            text += "\n\nThese are deleted too:\n" + names.joined(separator: "\n")
            if p.trash.count > 6 { text += "\n• and \(p.trash.count - 6) more" }
        }
        text += "\n\nYour clips in \(Settings.shared.outputDirectory.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) are kept."
        alert.informativeText = text
        let uninstall = alert.addButton(withTitle: "Uninstall")
        uninstall.hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        run(p)
    }

    // MARK: Removing

    static func run(_ p: Plan) {
        let c = Controller.shared
        if c.recording { c.stopRecording() } // saved like a normal stop
        OverlayController.shared.hide()
        Task {
            // Let replays and recordings finish writing and mixing (2 minutes at most).
            var waited = 0.0
            while Controller.shared.savesInFlight > 0 && waited < 120 {
                try? await Task.sleep(for: .milliseconds(200)); waited += 0.2
            }
            finish(p)
        }
    }

    private static func finish(_ p: Plan) {
        try? SMAppService.mainApp.unregister()
        for service in ["ScreenCapture", "Microphone"] {
            _ = run("/usr/bin/tccutil", ["reset", service, bundleID])
        }
        let fm = FileManager.default
        // macOS's App Management protection can stop Rewind deleting *other* app bundles (it may always
        // delete itself), so anything left over is shown in Finder instead of silently staying behind.
        let leftOver = p.trash.filter { (try? fm.removeItem(at: $0)) == nil }

        // Settings and caches go once Rewind has quit.
        let script = fm.temporaryDirectory.appendingPathComponent("rewind-uninstall-\(UUID().uuidString).sh")
        let quoted = p.data.map { "'" + $0.path.replacingOccurrences(of: "'", with: "'\\''") + "'" }.joined(separator: " ")
        try? """
        #!/bin/sh
        PID="$1"
        i=0; while kill -0 "$PID" 2>/dev/null && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done
        /usr/bin/defaults delete \(bundleID) >/dev/null 2>&1
        rm -rf \(quoted) "$HOME/Library/Preferences/\(bundleID).plist"
        rm -f "$0"
        """.write(to: script, atomically: true, encoding: .utf8)
        _ = run("/bin/sh", ["-c", "/usr/bin/nohup /bin/sh '\(script.path)' \(ProcessInfo.processInfo.processIdentifier) >/dev/null 2>&1 &"])

        // This copy last. The running program is already in memory, so deleting its files is safe.
        // Only ever an app bundle (never, say, a build folder when run from the command line).
        let deletedSelf = p.app.pathExtension == "app" && (try? fm.removeItem(at: p.app)) != nil
        var manual = leftOver
        if !deletedSelf, p.app.pathExtension == "app" { manual.append(p.app) }
        if manual.isEmpty {
            Toast.show("Rewind is uninstalled", "Your clips are still in your clips folder.",
                       icon: "checkmark.circle.fill", tint: Theme.accent)
        } else {
            NSWorkspace.shared.activateFileViewerSelecting(manual)
            Toast.show("Rewind is uninstalled, almost",
                       "macOS didn't let Rewind delete \(manual.count == 1 ? "1 copy" : "\(manual.count) copies"). It's selected in Finder: drag it to the Trash.",
                       icon: "exclamationmark.triangle.fill", tint: .orange, duration: 6)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + (manual.isEmpty ? 1.5 : 5)) { exit(0) } // no normal quit: nothing should be saved now
    }

    @discardableResult
    private static func run(_ tool: String, _ args: [String]) -> String? {
        let proc = Process(), pipe = Pipe()
        proc.executableURL = URL(fileURLWithPath: tool)
        proc.arguments = args
        proc.standardOutput = pipe
        proc.standardError = FileHandle.nullDevice
        guard (try? proc.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        proc.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    }
}
