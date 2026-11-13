import Foundation
import AppKit
import SwiftUI
import ScreenCaptureKit

/// App state + capture lifecycle. Capture runs only while Instant Replay or a recording needs it.
@MainActor
final class Controller: ObservableObject {
    static let shared = Controller()

    @Published private(set) var replayActive = false
    @Published private(set) var recording = false
    @Published private(set) var recordingStart: Date?
    @Published private(set) var isCapturing = false
    @Published private(set) var captureInfo = ""

    let settings = Settings.shared
    let router = MediaRouter()
    private let engine = CaptureEngine()
    private var runningSignature: CaptureSignature?
    private var opChain: Task<Void, Never>?
    private var retargetWork: DispatchWorkItem?
    private var workspaceObservers: [NSObjectProtocol] = []

    init() {
        let router = self.router
        engine.onVideo = { router.video($0) }
        engine.onAudio = { router.audio($0, $1) }
        engine.onStop = { error in Task { @MainActor in Controller.shared.captureDied(error) } }
        router.replay.duration = Double(settings.replaySeconds)

        // Retarget when the chosen app opens or quits.
        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
            workspaceObservers.append(nc.addObserver(forName: name, object: nil, queue: .main) { note in
                guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
                let key = AppTarget.key(for: app)
                MainActor.assumeIsolated {
                    let c = Controller.shared
                    guard !c.settings.captureAppID.isEmpty, key == c.settings.captureAppID else { return }
                    // Give a launching app a moment to open its window.
                    c.scheduleRetarget(after: name == NSWorkspace.didLaunchApplicationNotification ? 3 : 0.5)
                }
            })
        }
    }

    private func scheduleRetarget(after delay: Double) {
        retargetWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.settingsChanged() }
        retargetWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    // MARK: Actions

    func perform(_ action: HotkeyAction) {
        switch action {
        case .toggleOverlay: OverlayController.shared.toggle()
        case .saveReplay: saveReplay()
        case .toggleReplay: toggleReplay()
        case .toggleRecording: toggleRecording()
        case .screenshot: screenshot()
        }
    }

    func toggleReplay() { replayActive ? stopReplay() : startReplay() }
    func toggleRecording() { recording ? stopRecording() : startRecording() }

    func startReplay() {
        guard !replayActive else { return }
        router.replay.clear()
        router.replay.duration = Double(settings.replaySeconds)
        router.bufferReplay = true
        replayActive = true
        reconcile { [weak self] ok in
            guard let self, ok, self.replayActive else { return }
            let what = self.engine.capturedApp.map { "\($0) · " } ?? ""
            Toast.show("Instant Replay is on",
                       "\(what)Keeping the last \(formatDuration(Double(self.settings.replaySeconds))) · \(self.settings.hotkey(.saveReplay).display) to save",
                       icon: "arrow.counterclockwise.circle.fill", tint: .green)
        }
    }

    func stopReplay() {
        guard replayActive else { return }
        router.bufferReplay = false
        router.replay.clear()
        replayActive = false
        Toast.show("Instant Replay is off", nil, icon: "arrow.counterclockwise.circle", tint: .gray)
        reconcile()
    }

    func saveReplay() {
        guard replayActive else {
            Toast.show("Instant Replay is off", "Press \(settings.hotkey(.toggleReplay).display) to turn it on",
                       icon: "exclamationmark.triangle.fill", tint: .orange)
            return
        }
        let snap = router.replay.snapshot()
        guard snap.video.count > 1 else {
            Toast.show("Nothing to save yet", "The replay buffer is still empty.", icon: "hourglass", tint: .orange)
            return
        }
        guard let url = makeOutputURL(prefix: "Replay", ext: "mp4") else { return }
        let mix = settings.mixMicrophone
        ClipWriter.write(snap, to: url) { result in
            Task { @MainActor in
                switch result {
                case .success(let length):
                    await Controller.mixDown(url, enabled: mix)
                    Toast.show("Replay saved", "\(formatDuration(length)) · \(url.lastPathComponent)",
                               icon: "checkmark.circle.fill", tint: .green) { reveal(url) }
                case .failure(let error):
                    Toast.error("Couldn't save replay", error)
                }
            }
        }
    }

    func startRecording() {
        guard !recording, let url = makeOutputURL(prefix: "Recording", ext: "mp4") else { return }
        var tracks: Set<AudioTrack> = []
        if settings.systemAudio { tracks.insert(.system) }
        if settings.microphone { tracks.insert(.mic) }
        let rec = LiveRecorder(url: url, tracks: tracks, knownFormats: router.audioFormats)
        let engine = self.engine
        rec.requestKeyframe = { engine.forceKeyframe() }
        router.recorder = rec
        recording = true
        recordingStart = Date()
        engine.forceKeyframe()
        reconcile { [weak self] ok in
            guard let self, ok, self.recording else { return }
            self.engine.forceKeyframe()
            Toast.show("Recording started", "\(self.settings.hotkey(.toggleRecording).display) to stop",
                       icon: "record.circle.fill", tint: .red)
        }
    }

    func stopRecording() {
        guard recording, let rec = router.recorder else { return }
        router.recorder = nil
        recording = false
        recordingStart = nil
        let mix = settings.mixMicrophone
        rec.finish { result in
            Task { @MainActor in
                switch result {
                case .success(let length):
                    await Controller.mixDown(rec.url, enabled: mix)
                    Toast.show("Recording saved", "\(formatDuration(length)) · \(rec.url.lastPathComponent)",
                               icon: "checkmark.circle.fill", tint: .green) { reveal(rec.url) }
                case .failure(let error):
                    Toast.error("Recording failed", error)
                }
            }
        }
        reconcile() // stops capture if replay is off, or applies settings that changed mid-recording
    }

    /// Folds the mic track into the main one when that setting is on. Keeps separate tracks if it fails.
    static func mixDown(_ url: URL, enabled: Bool) async {
        guard enabled else { return }
        do {
            try await AudioMixdown.mixIfNeeded(url)
        } catch {
            Toast.show("Kept separate audio tracks", "Mixing failed: \(error.localizedDescription)",
                       icon: "exclamationmark.triangle.fill", tint: .orange)
            try? await Task.sleep(for: .seconds(2))
        }
    }

    func screenshot() {
        guard let url = makeOutputURL(prefix: "Screenshot", ext: "png") else { return }
        let displayID = settings.displayID, cursor = settings.showCursor, appID = settings.captureAppID
        Task {
            do {
                try await CaptureEngine.screenshot(displayID: displayID, appID: appID, showCursor: cursor, to: url)
                Toast.show("Screenshot saved", url.lastPathComponent, icon: "camera.fill", tint: .blue) { reveal(url) }
            } catch {
                Toast.error("Screenshot failed", error)
            }
        }
    }

    func openClipsFolder() {
        try? FileManager.default.createDirectory(at: settings.outputDirectory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(settings.outputDirectory)
    }

    func settingsChanged() {
        router.replay.duration = Double(settings.replaySeconds)
        let cleanupSig = settings.autoCleanup ? "\(settings.cleanupDays)|\(settings.outputDirectory.path)" : ""
        if cleanupSig != lastCleanupSig { lastCleanupSig = cleanupSig; cleanUpOldClips() }
        if isCapturing, runningSignature != settings.captureSignature { reconcile() }
    }

    // MARK: Auto-cleanup

    private var lastCleanupSig = ""
    private var cleanupTimer: Timer?

    /// Checks now, then every hour while Rewind runs.
    func startCleanupSchedule() {
        lastCleanupSig = settings.autoCleanup ? "\(settings.cleanupDays)|\(settings.outputDirectory.path)" : ""
        cleanUpOldClips()
        cleanupTimer?.invalidate()
        cleanupTimer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in
            MainActor.assumeIsolated { Controller.shared.cleanUpOldClips() }
        }
    }

    func cleanUpOldClips() {
        guard settings.autoCleanup else { return }
        let dir = settings.outputDirectory, days = settings.cleanupDays
        let keep: Set<URL> = router.recorder.map { [$0.url] } ?? []
        Task.detached(priority: .utility) {
            let r = Cleanup.run(in: dir, olderThan: days, keep: keep)
            guard r.count > 0 else { return }
            await MainActor.run {
                let size = ByteCountFormatter.string(fromByteCount: r.bytes, countStyle: .file)
                Toast.show("Moved \(r.count) old \(r.count == 1 ? "file" : "files") to the Trash",
                           "Older than \(days) \(days == 1 ? "day" : "days") · \(size) freed",
                           icon: "trash.fill", tint: .gray)
            }
        }
    }

    var bufferedSeconds: Double { router.replay.bufferedSeconds }
    var bufferBytes: Int { router.replay.bytes }

    // MARK: Capture lifecycle

    /// Serially brings capture in line with what's wanted (replay and/or recording) and the current settings.
    private func reconcile(_ completion: ((Bool) -> Void)? = nil) {
        let prev = opChain
        opChain = Task {
            await prev?.value
            let ok = await reconcileNow()
            completion?(ok)
        }
    }

    private func reconcileNow() async -> Bool {
        let want = replayActive || recording
        let sig = settings.captureSignature
        if !want {
            if isCapturing { await engine.stop(); markStopped() }
            return true
        }
        if isCapturing {
            // Don't restart mid-recording; changed settings apply once it stops.
            if sig == runningSignature || recording { return true }
            let previous = runningSignature
            await engine.stop()
            markStopped()
            router.replay.clear()
            if previous?.appRunning == sig.appRunning || sig.appID.isEmpty {
                Toast.show("Capture settings updated", "The replay buffer was restarted.", icon: "gearshape.fill", tint: .blue)
            }
        }
        do {
            try await engine.start(sig)
            isCapturing = true
            runningSignature = sig
            captureInfo = engine.info
            announceTarget(sig)
            return true
        } catch {
            reportStartError(error)
            failAll()
            return false
        }
    }

    /// Says what's being captured when an app was chosen, and retries if its window wasn't up yet.
    private func announceTarget(_ sig: CaptureSignature) {
        guard !sig.appID.isEmpty else { return }
        let name = sig.appName.isEmpty ? "The chosen app" : sig.appName
        if let app = engine.capturedApp {
            retargetAttempts = 0
            Toast.show("Capturing \(app)", "Only \(app)'s windows and sound are recorded.",
                       icon: "app.badge.checkmark", tint: Theme.accent)
        } else if sig.appRunning {
            // Running but no window on screen yet: try again shortly.
            scheduleRetargetForce()
        } else {
            Toast.show("\(name) isn't running", "Capturing the whole display until it opens.",
                       icon: "display", tint: .orange)
        }
    }

    private var retargetAttempts = 0
    private func scheduleRetargetForce() {
        guard retargetAttempts < 10 else { retargetAttempts = 0; return }
        retargetAttempts += 1
        retargetWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.isCapturing, !self.recording, self.engine.capturedApp == nil else { return }
            self.runningSignature = nil // forces a restart on the next reconcile
            self.reconcile()
        }
        retargetWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: work)
    }

    private func markStopped() {
        router.resetAudioFormats()
        isCapturing = false
        runningSignature = nil
        captureInfo = ""
    }

    private func captureDied(_ error: Error) {
        markStopped()
        Toast.error("Capture stopped", error)
        failAll()
    }

    private func failAll() {
        router.bufferReplay = false
        router.replay.clear()
        replayActive = false
        if recording { stopRecording() }
    }

    private func reportStartError(_ error: Error) {
        let ns = error as NSError
        if ns.domain == SCStreamErrorDomain && ns.code == SCStreamError.userDeclined.rawValue || !CGPreflightScreenCaptureAccess() {
            Toast.show("Screen Recording permission needed",
                       "Click to open System Settings, allow Rewind, then try again.",
                       icon: "lock.fill", tint: .orange, duration: 8) {
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
            }
        } else {
            Toast.error("Couldn't start capture", error)
        }
    }

    private func makeOutputURL(prefix: String, ext: String) -> URL? {
        let dir = settings.outputDirectory
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            Toast.error("Can't write to \(dir.path)", error)
            return nil
        }
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let base = "\(prefix) \(f.string(from: Date()))"
        var url = dir.appendingPathComponent("\(base).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(base) (\(n)).\(ext)"); n += 1
        }
        return url
    }
}

func formatDuration(_ s: Double) -> String {
    let t = Int(s.rounded())
    if t < 60 { return "\(t)s" }
    return String(format: "%d:%02d", t / 60, t % 60)
}

@MainActor func reveal(_ url: URL) {
    NSWorkspace.shared.activateFileViewerSelecting([url])
}
