import AppKit
import SwiftUI
import ServiceManagement

@MainActor
final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
            w.title = "Rewind Settings"
            w.styleMask = [.titled, .closable, .miniaturizable]
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

private struct DisplayOption: Identifiable, Hashable {
    let id: UInt32
    let name: String
}

struct SettingsView: View {
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var c = Controller.shared
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    private var displays: [DisplayOption] {
        [DisplayOption(id: 0, name: "Main display")] + NSScreen.screens.compactMap { screen in
            guard let n = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            let w = Int(screen.frame.width * screen.backingScaleFactor), h = Int(screen.frame.height * screen.backingScaleFactor)
            return DisplayOption(id: n.uint32Value, name: "\(screen.localizedName) (\(w)×\(h))")
        }
    }

    private var estimate: String {
        let size = CaptureMath.outputSize(native: CaptureMath.nativePixelSize(displayID: s.displayID),
                                          resolution: s.resolution, codec: s.codec)
        let bitrate = CaptureMath.bitrate(size: size, fps: s.fps, quality: s.quality, codec: s.codec)
        let audioTracks = (s.systemAudio ? 1 : 0) + (s.microphone ? 1 : 0)
        let bytes = Double(bitrate) / 8 * Double(s.replaySeconds) + Double(audioTracks * 384_000 * s.replaySeconds)
        let mem = ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .memory)
        return "\(Int(size.width))×\(Int(size.height)) at ≈\(bitrate / 1_000_000) Mbps. The replay buffer uses about \(mem) of RAM."
    }

    var body: some View {
        Form {
            Section("Video") {
                Picker("Display", selection: $s.displayID) {
                    ForEach(displays) { Text($0.name).tag($0.id) }
                }
                Picker("Resolution", selection: $s.resolution) {
                    ForEach(Resolution.allCases) { Text($0.label).tag($0) }
                }
                Picker("Frame rate", selection: $s.fps) {
                    ForEach([30, 60, 120], id: \.self) { Text("\($0) fps").tag($0) }
                }
                Picker("Quality", selection: $s.quality) {
                    ForEach(Quality.allCases) { Text($0.label).tag($0) }
                }
                Picker("Codec", selection: $s.codec) {
                    ForEach(VideoCodec.allCases) { Text($0.label).tag($0) }
                }
                Toggle("Show mouse cursor", isOn: $s.showCursor)
            }

            Section {
                Toggle("Record system audio", isOn: $s.systemAudio)
                Toggle("Record microphone", isOn: $s.microphone)
            } header: {
                Text("Audio")
            } footer: {
                Text("System audio and the microphone are saved as separate audio tracks.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                Picker("Replay length", selection: $s.replaySeconds) {
                    ForEach([15, 30, 60, 120, 180, 300, 600, 900, 1200], id: \.self) {
                        Text($0 < 60 ? "\($0) seconds" : "\($0 / 60) minute\($0 == 60 ? "" : "s")").tag($0)
                    }
                }
                Toggle("Turn on Instant Replay when Rewind starts", isOn: $s.startReplayOnLaunch)
            } header: {
                Text("Instant Replay")
            } footer: {
                Text(estimate).font(.caption).foregroundStyle(.secondary)
            }

            Section("Saving") {
                LabeledContent("Save to") {
                    HStack {
                        Text(s.outputDirectory.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                            .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                        Button("Choose…", action: chooseFolder)
                    }
                }
            }

            Section {
                ForEach(HotkeyAction.allCases) { action in
                    LabeledContent(action.label) { HotkeyField(action: action) }
                }
                Button("Reset to defaults") { s.resetHotkeys() }
            } header: {
                Text("Hotkeys")
            } footer: {
                Text("On a Mac keyboard, hold fn for F-keys unless they're set to work as standard function keys.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("General") {
                Toggle("Open Rewind at login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, on in
                        do {
                            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                        } catch {
                            Toast.error("Couldn't change login item", error)
                            launchAtLogin = SMAppService.mainApp.status == .enabled
                        }
                    }
                if c.isCapturing {
                    LabeledContent("Capturing now", value: c.captureInfo)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 680)
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = s.outputDirectory
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url { s.outputDirectory = url }
    }
}

private struct HotkeyField: View {
    let action: HotkeyAction
    @ObservedObject private var s = Settings.shared
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        Button(recording ? "Press a shortcut…" : s.hotkey(action).display) {
            recording ? stop() : start()
        }
        .frame(minWidth: 130)
        .onDisappear(perform: stop)
    }

    private func start() {
        recording = true
        HotkeyManager.shared.suspend()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 { stop(); return nil } // Esc cancels
            let mods = Hotkey.carbonModifiers(e.modifierFlags)
            let code = UInt32(e.keyCode)
            guard mods != 0 || Hotkey.functionKeys[code] != nil else { NSSound.beep(); return nil }
            let name = Hotkey.keyName(keyCode: code, characters: e.charactersIgnoringModifiers)
            s.setHotkey(Hotkey(keyCode: code, modifiers: mods, display: Hotkey.modifierSymbols(mods) + name), for: action)
            stop()
            return nil
        }
    }

    private func stop() {
        guard recording else { return }
        recording = false
        if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
        HotkeyManager.shared.resume()
    }
}
