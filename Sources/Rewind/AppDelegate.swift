import AppKit
import Combine

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let controller = Controller.shared
    private let settings = Settings.shared
    private var cancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        updateIcon()

        controller.$replayActive.combineLatest(controller.$recording)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateIcon() }
            .store(in: &cancellables)

        registerHotkeys(reportConflicts: true)
        settings.$hotkeys.dropFirst()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.registerHotkeys(reportConflicts: true) }
            .store(in: &cancellables)

        settings.objectWillChange
            .debounce(for: .milliseconds(600), scheduler: RunLoop.main)
            .sink { [weak self] in self?.controller.settingsChanged() }
            .store(in: &cancellables)

        controller.startCleanupSchedule()

        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
        }
        if CommandLine.arguments.contains("--demo-toast") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
                Toast.show("Replay saved", "2:00 · Replay 2026-11-13 at 21.04.11.mp4",
                           icon: "checkmark.circle.fill", tint: Theme.replay, duration: 30)
            }
        }
        if CommandLine.arguments.contains("--settings") { SettingsWindow.shared.show() }
        if CommandLine.arguments.contains("--overlay") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { OverlayController.shared.show() }
        }
        if settings.startReplayOnLaunch {
            controller.startReplay()
        } else {
            let hint = settings.hotkeyHint(.toggleOverlay, "opens the overlay")
            Toast.show("Rewind is running", hint.isEmpty ? "Open the overlay from the menu bar." : hint,
                       icon: "record.circle", tint: .green)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if controller.recording { controller.stopRecording(); RunLoop.main.run(until: Date().addingTimeInterval(1)) }
    }

    private func registerHotkeys(reportConflicts: Bool) {
        let bindings: [(Hotkey, () -> Void)] = HotkeyAction.allCases.map { action in
            (settings.hotkey(action), { [weak self] in self?.controller.perform(action) })
        }
        let failed = HotkeyManager.shared.register(bindings)
        if reportConflicts, !failed.isEmpty {
            Toast.show("Some hotkeys are taken", failed.map(\.display).joined(separator: ", ")
                       + " is already used by another app. Change it in Settings.",
                       icon: "keyboard", tint: .orange, duration: 6) { SettingsWindow.shared.show() }
        }
    }

    private func updateIcon() {
        guard let button = statusItem?.button else { return }
        let image: NSImage?
        if controller.recording {
            let cfg = NSImage.SymbolConfiguration(paletteColors: [.systemRed])
            image = NSImage(systemSymbolName: "record.circle.fill", accessibilityDescription: "Recording")?
                .withSymbolConfiguration(cfg)
            image?.isTemplate = false
        } else {
            image = NSImage(systemSymbolName: controller.replayActive ? "arrow.counterclockwise.circle.fill" : "record.circle",
                            accessibilityDescription: "Rewind")
            image?.isTemplate = true
        }
        button.image = image
    }

    // MARK: Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let status: String
        if controller.recording, let start = controller.recordingStart {
            status = "● Recording, \(formatDuration(Date().timeIntervalSince(start)))"
        } else if controller.replayActive {
            status = "Instant Replay on, \(formatDuration(controller.bufferedSeconds)) buffered"
        } else {
            status = "Idle"
        }
        let header = NSMenuItem(title: status, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        add(menu, "Save Replay", .saveReplay, #selector(saveReplay)).isEnabled = controller.replayActive
        add(menu, controller.replayActive ? "Turn Off Instant Replay" : "Turn On Instant Replay", .toggleReplay, #selector(toggleReplay))
        add(menu, controller.recording ? "Stop Recording" : "Start Recording", .toggleRecording, #selector(toggleRecording))
        add(menu, "Take Screenshot", .screenshot, #selector(screenshot))
        menu.addItem(.separator())
        add(menu, "Show Overlay", .toggleOverlay, #selector(showOverlay))
        menu.addItem(item("Open Clips Folder", #selector(openFolder)))
        menu.addItem(item("Settings…", #selector(openSettings), key: ","))
        menu.addItem(.separator())
        menu.addItem(item("Quit Rewind", #selector(quit), key: "q"))
    }

    @discardableResult
    private func add(_ menu: NSMenu, _ title: String, _ action: HotkeyAction, _ selector: Selector) -> NSMenuItem {
        let (key, mods) = settings.hotkey(action).menuKeyEquivalent
        let i = item(title, selector, key: key)
        i.keyEquivalentModifierMask = mods
        menu.addItem(i)
        return i
    }

    private func item(_ title: String, _ selector: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: title, action: selector, keyEquivalent: key)
        i.target = self
        return i
    }

    @objc private func saveReplay() { controller.saveReplay() }
    @objc private func toggleReplay() { controller.toggleReplay() }
    @objc private func toggleRecording() { controller.toggleRecording() }
    @objc private func screenshot() { controller.screenshot() }
    @objc private func showOverlay() { OverlayController.shared.show() }
    @objc private func openFolder() { controller.openClipsFolder() }
    @objc private func openSettings() { SettingsWindow.shared.show() }
    @objc private func quit() { NSApp.terminate(nil) }
}
