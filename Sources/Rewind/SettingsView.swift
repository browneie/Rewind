import AppKit
import SwiftUI
import ServiceManagement

@MainActor
final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let host = NSHostingController(rootView: SettingsView())
            host.sizingOptions = [] // size the window ourselves; auto-sizing can collapse it to 2 px wide
            let w = NSWindow(contentViewController: host)
            w.title = "Rewind Settings"
            w.styleMask = [.titled, .closable, .miniaturizable]
            w.setContentSize(NSSize(width: 520, height: 680))
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        // Don't let the first control grab focus and scroll the form away from the top.
        DispatchQueue.main.async { [weak window] in window?.makeFirstResponder(nil) }
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
    @State private var runningApps = AppTarget.pickable()

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
        let bitrate = CaptureMath.bitrate(size: size, fps: s.fps, quality: s.quality, codec: s.codec, customMbps: s.customMbps)
        let audioTracks = (s.systemAudio ? 1 : 0) + (s.microphone ? 1 : 0)
        let bytes = Double(bitrate) / 8 * Double(s.replaySeconds) + Double(audioTracks * 384_000 * s.replaySeconds)
        let mem = ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .memory)
        return "\(Int(size.width))×\(Int(size.height)) at ≈\(bitrate / 1_000_000) Mbps. The replay buffer uses about \(mem) of RAM."
    }

    var body: some View {
        Form {
            Section {
                Picker("Capture", selection: appSelection) {
                    Label("Entire display", systemImage: "display").tag("")
                    Divider()
                    ForEach(runningApps, id: \.processIdentifier) { app in
                        Label {
                            Text(app.localizedName ?? "Unknown app")
                        } icon: {
                            Image(nsImage: smallIcon(app.icon))
                        }
                        .tag(AppTarget.key(for: app))
                    }
                    if !s.captureAppID.isEmpty, !runningApps.contains(where: { AppTarget.key(for: $0) == s.captureAppID }) {
                        Divider()
                        Text("\(s.captureAppName) (not running)").tag(s.captureAppID)
                    }
                }
            } header: {
                Text("What to capture")
            } footer: {
                Text(s.captureAppID.isEmpty
                     ? "Records everything on the display."
                     : "Records only \(s.captureAppName)'s windows and sound. While it isn't running, Rewind records the whole display and switches over when it opens.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didLaunchApplicationNotification)) { _ in
                runningApps = AppTarget.pickable()
            }
            .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didTerminateApplicationNotification)) { _ in
                runningApps = AppTarget.pickable()
            }

            Section {
                Picker(s.captureAppID.isEmpty ? "Display" : "Fallback display", selection: $s.displayID) {
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
                if s.quality == .custom {
                    LabeledContent("Bitrate") {
                        HStack(spacing: 6) {
                            TextField("", value: $s.customMbps, format: .number)
                                .multilineTextAlignment(.trailing).frame(width: 56)
                                .onSubmit { s.customMbps = min(max(s.customMbps, Quality.customMbpsRange.lowerBound), Quality.customMbpsRange.upperBound) }
                            Stepper("", value: $s.customMbps, in: Quality.customMbpsRange, step: 1).labelsHidden()
                            Text("Mbps").foregroundStyle(.secondary)
                        }
                    }
                }
                Picker("Codec", selection: $s.codec) {
                    ForEach(VideoCodec.allCases) { Text($0.label).tag($0) }
                }
                Toggle("Show mouse cursor", isOn: $s.showCursor)
            } header: {
                Text("Video")
            } footer: {
                if s.fps >= 120 {
                    Text("120 fps makes macOS copy and convert twice as many frames as 60 fps, which uses more of the GPU your game is using. 60 fps is easier on frame rates.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            Section {
                Toggle("Record system audio", isOn: $s.systemAudio)
                Toggle("Record microphone", isOn: $s.microphone)
                Toggle("Mix microphone into the main track", isOn: $s.mixMicrophone)
                    .disabled(!(s.microphone && s.systemAudio))
            } header: {
                Text("Audio")
            } footer: {
                if !audioFooter.isEmpty {
                    Text(audioFooter).font(.caption).foregroundStyle(.secondary)
                }
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

            Section {
                LabeledContent("Save to") {
                    HStack {
                        Text(s.outputDirectory.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                            .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                        Button("Choose…", action: chooseFolder)
                    }
                }
                Toggle("Delete old clips automatically", isOn: $s.autoCleanup)
                Picker("Keep clips for", selection: $s.cleanupDays) {
                    ForEach(Cleanup.dayOptions, id: \.self) { Text($0 == 1 ? "1 day" : "\($0) days").tag($0) }
                }
                .disabled(!s.autoCleanup)
            } header: {
                Text("Saving")
            } footer: {
                Text(s.autoCleanup
                     ? "Replays, recordings and screenshots older than \(s.cleanupDays) \(s.cleanupDays == 1 ? "day" : "days") are moved to the Trash, so you can still get them back. Other files in the folder are never touched."
                     : "Clips are kept until you delete them.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                Picker("Layout", selection: $s.overlayLayout) {
                    ForEach(OverlayLayout.allCases) { Text($0.label).tag($0) }
                }
                ColorPicker("Accent color", selection: $s.accentColor, supportsOpacity: false)
                LabeledContent("Presets") {
                    HStack(spacing: 7) {
                        ForEach(Theme.accentPresets, id: \.hex) { preset in
                            Button { s.accentHex = preset.hex } label: {
                                Circle().fill(Color(hex: preset.hex)).frame(width: 18, height: 18)
                                    .overlay(Circle().strokeBorder(.primary.opacity(s.accentHex == preset.hex ? 0.9 : 0.15),
                                                                   lineWidth: s.accentHex == preset.hex ? 2 : 1))
                            }
                            .buttonStyle(.plain).help(preset.name)
                        }
                    }
                }
                LabeledContent("Dim the game") {
                    Slider(value: $s.overlayDim, in: 0...0.9).frame(width: 180)
                }
                LabeledContent("Player size") {
                    HStack(spacing: 8) {
                        Image(systemName: "rectangle.inset.filled").font(.caption).foregroundStyle(.secondary)
                        Slider(value: $s.playerSize, in: Theme.playerSizeRange, step: 10).frame(width: 160)
                        Image(systemName: "rectangle.inset.filled").font(.title3).foregroundStyle(.secondary)
                    }
                }
                .disabled(s.overlayLayout == .tiles)
                .help("Width of the player in the Split layout and Classic's Gallery")
                Toggle("Show hotkeys in the overlay", isOn: $s.showHotkeys)
                Button("Preview overlay") { OverlayController.shared.show() }
            } header: {
                Text("Overlay")
            } footer: {
                Text("The accent color is used for Instant Replay and highlights. Click the color well for the color wheel. Changes apply the next time the overlay opens.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                ForEach(HotkeyAction.allCases) { action in
                    LabeledContent(action.label) { HotkeyField(action: action) }
                }
                Button("Reset to defaults") { s.resetHotkeys() }
            } header: {
                Text("Hotkeys")
            } footer: {
                Text("Click a shortcut, then press the new keys. Press Esc to remove it. On a Mac keyboard, hold fn for F-keys unless they're set to work as standard function keys.")
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
        .defaultScrollAnchor(.top)
        .scrollPosition(id: .constant(nil as String?), anchor: .top)
        .frame(width: 520, height: 680)
    }

    private var audioFooter: String {
        guard s.microphone && s.systemAudio else { return "" }
        return s.mixMicrophone
            ? "Game sound and your voice share one track, so clips play correctly everywhere (VLC, browsers, Discord, phones)."
            : "Game sound and your voice are saved as separate tracks, handy for editing. Some players only play the first one."
    }

    private var appSelection: Binding<String> {
        Binding(get: { s.captureAppID }, set: { key in
            s.captureAppName = runningApps.first { AppTarget.key(for: $0) == key }?.localizedName
                ?? (key == s.captureAppID ? s.captureAppName : "")
            s.captureAppID = key
        })
    }

    private func smallIcon(_ image: NSImage?) -> NSImage {
        let img = (image?.copy() as? NSImage) ?? NSImage(systemSymbolName: "app", accessibilityDescription: nil)!
        img.size = NSSize(width: 16, height: 16)
        return img
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
        .foregroundStyle(!recording && !s.hotkey(action).isSet ? .secondary : .primary)
        .frame(minWidth: 130)
        .onDisappear(perform: stop)
    }

    private func start() {
        recording = true
        HotkeyManager.shared.suspend()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 { s.clearHotkey(action); stop(); return nil } // Esc clears the bind
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
