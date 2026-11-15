import AppKit
import SwiftUI
import ServiceManagement

// Settings live inside the overlay: Console shows them in its two columns, Sidebar as a tabbed page,
// Classic in the Gallery's place, and Split as a sheet on top (SettingsSheet). The pages and controls here are shared by all three.

// MARK: - Pages

enum SettingsPage: String, CaseIterable, Identifiable {
    case capture, video, audio, replay, saving, overlay, hotkeys, general
    var id: String { rawValue }

    var title: String {
        switch self {
        case .capture: "Capture"
        case .video: "Video"
        case .audio: "Audio"
        case .replay: "Instant Replay"
        case .saving: "Saving"
        case .overlay: "Overlay"
        case .hotkeys: "Hotkeys"
        case .general: "General"
        }
    }

    /// Current value in a few characters, for Console's category list.
    @MainActor var summary: String {
        let s = Settings.shared
        switch self {
        case .capture: return s.captureAppID.isEmpty ? (Displays.info(for: s.displayID)?.short ?? "Display") : s.captureAppName
        case .video: return "\(s.fps) fps · \(s.codec.short)"
        case .audio: return s.systemAudio ? (s.microphone ? "System + mic" : "System") : (s.microphone ? "Mic" : "Off")
        case .replay: return formatDuration(Double(s.replaySeconds))
        case .saving: return s.autoCleanup ? "\(s.cleanupDays) days" : "Keep all"
        case .overlay: return s.overlayLayout.name
        case .hotkeys: return s.hotkey(.toggleOverlay).isSet ? s.hotkey(.toggleOverlay).display : "—"
        case .general: return "v" + Updater.shared.currentVersion
        }
    }
}

/// Sidebar's tabs; each holds one or more pages.
enum SettingsTab: String, CaseIterable, Identifiable {
    case recording, replay, overlay, hotkeys, general
    var id: String { rawValue }

    var title: String {
        switch self {
        case .recording: "Recording"
        case .replay: "Replay"
        case .overlay: "Overlay"
        case .hotkeys: "Hotkeys"
        case .general: "General"
        }
    }

    var pages: [SettingsPage] {
        switch self {
        case .recording: [.capture, .video, .audio]
        case .replay: [.replay, .saving]
        case .overlay: [.overlay]
        case .hotkeys: [.hotkeys]
        case .general: [.general]
        }
    }

    static func holding(_ page: SettingsPage) -> SettingsTab { allCases.first { $0.pages.contains(page) } ?? .recording }
}

// MARK: - Look

/// Colors and sizes for the settings controls, set by the layout showing them.
struct SettingsStyle {
    var text: Color
    var muted: Color
    var value: Color
    var line: Color
    var rowLine: Color
    /// Fill of a selected segment or an "on" switch; nil means the accent color.
    var selected: Color?
    var selectedText: Color
    var hover: Color
    var radius: CGFloat
    /// Widest a row of segments may get before it turns into a drop-down.
    var segmentWidth: CGFloat

    @MainActor var fill: Color { selected ?? Theme.accent }

    static let console = SettingsStyle(text: Color(hex: 0xE9E9E6), muted: Color(hex: 0x7D7E79), value: Color(hex: 0xB5B6B1),
                                       line: Color(hex: 0x2A2B29), rowLine: Color(hex: 0x1F201E), selected: nil,
                                       selectedText: Color(hex: 0x04140A), hover: .white.opacity(0.04), radius: 4, segmentWidth: 520)
    static let sheet: SettingsStyle = { var s = console; s.segmentWidth = 330; return s }()
}

private struct SettingsStyleKey: EnvironmentKey { static let defaultValue = SettingsStyle.console }

extension EnvironmentValues {
    var settingsStyle: SettingsStyle {
        get { self[SettingsStyleKey.self] }
        set { self[SettingsStyleKey.self] = newValue }
    }
}

private let valueFont = Font.system(size: 12, design: .monospaced)

// MARK: - Controls

private struct RowLabel: View {
    let title: String
    let hint: String?
    @Environment(\.settingsStyle) private var st

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.system(size: 14)).foregroundStyle(st.text)
            if let hint, !hint.isEmpty {
                Text(hint).font(.system(size: 12)).foregroundStyle(st.muted).fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A label (and hint) on the left, a control on the right, a hairline underneath.
private struct SRow<Trailing: View>: View {
    let title: String
    var hint: String? = nil
    @ViewBuilder let trailing: () -> Trailing
    @Environment(\.settingsStyle) private var st

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            RowLabel(title: title, hint: hint)
            trailing().fixedSize()
        }
        .padding(.vertical, 13)
        .overlay(alignment: .bottom) { Rectangle().fill(st.rowLine).frame(height: 1) }
    }
}

private struct Segments<T: Hashable>: View {
    let options: [(T, String)]
    @Binding var selection: T
    @Environment(\.settingsStyle) private var st

    var body: some View {
        HStack(spacing: 0) {
            ForEach(options.indices, id: \.self) { i in
                let on = options[i].0 == selection
                if i > 0 { Rectangle().fill(st.line).frame(width: 1) }
                Button { selection = options[i].0 } label: {
                    Text(options[i].1).font(valueFont).fontWeight(on ? .bold : .regular).lineLimit(1)
                        .foregroundStyle(on ? st.selectedText : st.value)
                        .padding(.horizontal, 11).padding(.vertical, 6)
                        .background(on ? st.fill : .clear)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .fixedSize()
        .clipShape(RoundedRectangle(cornerRadius: st.radius))
        .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(st.line))
    }
}

/// A choice shown as segments when they fit, otherwise as a drop-down that opens a list under the row.
/// (Real pop-up menus would open behind the overlay, so this stays inside it.)
private struct ChoiceRow<T: Hashable>: View {
    let title: String
    var hint: String? = nil
    let options: [(T, String)]
    @Binding var selection: T
    var list = false
    /// Shorter text for the closed drop-down, e.g. "Display 1" instead of its full name.
    var short: ((T) -> String)? = nil
    @State private var open = false
    @Environment(\.settingsStyle) private var st

    private var fitsAsSegments: Bool {
        !list && options.reduce(0) { $0 + CGFloat($1.1.count) * 7.4 + 23 } <= st.segmentWidth
    }

    private var current: String { short?(selection) ?? options.first { $0.0 == selection }?.1 ?? "—" }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 16) {
                RowLabel(title: title, hint: hint)
                if fitsAsSegments {
                    Segments(options: options, selection: $selection)
                } else {
                    Button { withAnimation(.easeOut(duration: 0.15)) { open.toggle() } } label: {
                        HStack(spacing: 8) {
                            Text(current).font(valueFont).foregroundStyle(st.value).lineLimit(1).truncationMode(.middle)
                            Image(systemName: open ? "chevron.up" : "chevron.down").font(.system(size: 9, weight: .bold))
                                .foregroundStyle(st.muted)
                        }
                        .padding(.horizontal, 11).padding(.vertical, 6)
                        .frame(maxWidth: 240)
                        .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(st.line))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .fixedSize()
                }
            }
            .padding(.vertical, 13)
            if open {
                VStack(spacing: 0) {
                    ForEach(options.indices, id: \.self) { i in
                        ListOption(label: options[i].1, on: options[i].0 == selection) {
                            selection = options[i].0
                            withAnimation(.easeOut(duration: 0.15)) { open = false }
                        }
                        if i < options.count - 1 { Rectangle().fill(st.rowLine).frame(height: 1) }
                    }
                }
                .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(st.line))
                .padding(.bottom, 13)
                .transition(.opacity)
            }
        }
        .overlay(alignment: .bottom) { Rectangle().fill(st.rowLine).frame(height: 1) }
    }
}

private struct ListOption: View {
    let label: String
    let on: Bool
    let action: () -> Void
    @State private var hover = false
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Button(action: action) {
            HStack {
                Text(label).font(.system(size: 13)).foregroundStyle(on ? st.text : st.value).lineLimit(1)
                Spacer(minLength: 8)
                if on { Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(st.fill) }
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(hover ? st.hover : .clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

private struct Switch: View {
    @Binding var isOn: Bool
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Button { withAnimation(.easeOut(duration: 0.15)) { isOn.toggle() } } label: {
            Capsule().fill(isOn ? st.fill : st.line)
                .frame(width: 36, height: 20)
                .overlay(alignment: isOn ? .trailing : .leading) {
                    Circle().fill(isOn ? st.selectedText : st.muted).frame(width: 14, height: 14).padding(3)
                }
        }
        .buttonStyle(.plain)
    }
}

private struct ToggleRow: View {
    let title: String
    var hint: String? = nil
    @Binding var isOn: Bool
    var body: some View { SRow(title: title, hint: hint) { Switch(isOn: $isOn) } }
}

private struct NumberRow: View {
    let title: String
    var hint: String? = nil
    @Binding var value: Int
    let range: ClosedRange<Int>
    let unit: String
    @Environment(\.settingsStyle) private var st

    var body: some View {
        SRow(title: title, hint: hint) {
            HStack(spacing: 0) {
                step("−", -1)
                TextField("", value: $value, format: .number.grouping(.never))
                    .textFieldStyle(.plain).font(valueFont).foregroundStyle(st.text)
                    .multilineTextAlignment(.center).frame(width: 46)
                    .onSubmit(clamp)
                step("+", 1)
                Text(unit).font(valueFont).foregroundStyle(st.muted).padding(.trailing, 10)
            }
            .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(st.line))
        }
    }

    private func step(_ label: String, _ by: Int) -> some View {
        Button { value = min(max(value + by, range.lowerBound), range.upperBound) } label: {
            Text(label).font(valueFont).foregroundStyle(st.value).frame(width: 26, height: 26).contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func clamp() { value = min(max(value, range.lowerBound), range.upperBound) }
}

private struct SliderRow: View {
    let title: String
    var hint: String? = nil
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double? = nil
    @Environment(\.settingsStyle) private var st

    var body: some View {
        SRow(title: title, hint: hint) {
            Group {
                if let step { Slider(value: $value, in: range, step: step) } else { Slider(value: $value, in: range) }
            }
            .tint(st.fill).controlSize(.small).frame(width: 190)
        }
    }
}

private struct SButton: View {
    let title: String
    var destructive = false
    let action: () -> Void
    @State private var hover = false
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Button(action: action) {
            Text(title).font(.system(size: 13))
                .foregroundStyle(destructive ? Color(hex: 0xFF5F6D) : st.text)
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(hover ? st.hover : .clear)
                .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(destructive ? Color(hex: 0xFF5F6D).opacity(0.5) : st.line))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

private struct Note: View {
    let text: String
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Text(text).font(.system(size: 12)).foregroundStyle(st.muted)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 12)
    }
}

/// Heading for a page when several share one scroll (Sidebar's tabs).
struct SettingsGroupTitle: View {
    let title: String
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Text(title).font(.system(size: 12)).foregroundStyle(st.muted)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 22).padding(.bottom, 2)
    }
}

private struct HotkeyField: View {
    let action: HotkeyAction
    @ObservedObject private var s = Settings.shared
    @State private var recording = false
    @State private var monitor: Any?
    @Environment(\.settingsStyle) private var st

    var body: some View {
        let key = s.hotkey(action)
        Button { recording ? stop() : start() } label: {
            Text(recording ? "Press keys…" : key.display).font(valueFont)
                .foregroundStyle(recording ? st.fill : (key.isSet ? st.text : st.muted))
                .padding(.horizontal, 10).padding(.vertical, 5)
                .frame(minWidth: 90)
                .overlay(RoundedRectangle(cornerRadius: st.radius).strokeBorder(recording ? st.fill : st.line))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onDisappear(perform: stop)
    }

    private func start() {
        recording = true
        OverlayState.shared.capturingKeys = true
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
        // Let the key that ended recording finish before the overlay handles keys (Esc) again.
        DispatchQueue.main.async { OverlayState.shared.capturingKeys = false }
    }
}

// MARK: - Page contents

struct SettingsPageView: View {
    let page: SettingsPage
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var c = Controller.shared
    @ObservedObject private var u = Updater.shared
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var runningApps = AppTarget.pickable()
    @State private var wheelOpen = false
    @Environment(\.settingsStyle) private var st

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            switch page {
            case .capture: capture
            case .video: video
            case .audio: audio
            case .replay: replay
            case .saving: saving
            case .overlay: overlay
            case .hotkeys: hotkeys
            case .general: general
            }
        }
    }

    // MARK: Capture

    @ViewBuilder private var capture: some View {
        let displays = Displays.all()
        let sources = captureSources(displays)
        ChoiceRow(title: "What to capture",
                  hint: s.captureAppID.isEmpty
                      ? "Everything on \(Displays.info(for: s.displayID)?.short ?? "the display"), with all system audio."
                      : "Only \(s.captureAppName)'s windows and sound. While it isn't running, Rewind records the fallback display.",
                  options: sources, selection: captureSelection, list: true,
                  short: { key in key.hasPrefix("display:") ? (Displays.info(for: UInt32(key.dropFirst(8)) ?? 0)?.short ?? "Display") : s.captureAppName })
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
                runningApps = AppTarget.pickable()
            }
            .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didLaunchApplicationNotification)) { _ in
                runningApps = AppTarget.pickable()
            }
            .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didTerminateApplicationNotification)) { _ in
                runningApps = AppTarget.pickable()
            }
        if !s.captureAppID.isEmpty {
            ChoiceRow(title: "Fallback display", options: displays.map { ($0.id, $0.label) },
                      selection: Binding(get: { Displays.resolve(s.displayID) }, set: { s.displayID = $0 }), list: true,
                      short: { Displays.info(for: $0)?.short ?? "Display" })
        }
    }

    /// Screens, then running apps, as (key, name).
    private func captureSources(_ displays: [Displays.Info]) -> [(String, String)] {
        var sources: [(String, String)] = displays.map { ("display:\($0.id)", $0.label) }
        if s.captureAppID.isEmpty, !displays.contains(where: { $0.id == Displays.resolve(s.displayID) }) {
            sources.append(("display:\(Displays.resolve(s.displayID))", "Disconnected display"))
        }
        sources += runningApps.map { (AppTarget.key(for: $0), $0.localizedName ?? "Unknown app") }
        if !s.captureAppID.isEmpty, !runningApps.contains(where: { AppTarget.key(for: $0) == s.captureAppID }) {
            sources.append((s.captureAppID, "\(s.captureAppName) (not running)"))
        }
        return sources
    }

    /// "display:<id>" for a screen, or an app key.
    private var captureSelection: Binding<String> {
        Binding(get: { s.captureAppID.isEmpty ? "display:\(Displays.resolve(s.displayID))" : s.captureAppID },
                set: { key in
            if key.hasPrefix("display:"), let id = UInt32(key.dropFirst("display:".count)) {
                s.captureAppID = ""
                s.captureAppName = ""
                s.displayID = id
                return
            }
            s.captureAppName = runningApps.first { AppTarget.key(for: $0) == key }?.localizedName
                ?? (key == s.captureAppID ? s.captureAppName : "")
            s.captureAppID = key
        })
    }

    // MARK: Video

    @ViewBuilder private var video: some View {
        ChoiceRow(title: "Resolution", options: Resolution.allCases.map { ($0, $0.label) }, selection: $s.resolution)
        ChoiceRow(title: "Frame rate", hint: "1–240 fps, or pick a preset",
                  options: Settings.presetFps.map { ($0, "\($0)") } + [(0, "Custom")], selection: fpsChoice)
        if s.customFps {
            NumberRow(title: "Custom frame rate", value: $s.fps, range: Settings.fpsRange, unit: "fps")
        }
        ChoiceRow(title: "Quality", options: Quality.allCases.map { ($0, $0 == .custom ? "Custom" : $0.label) }, selection: $s.quality)
        if s.quality == .custom {
            NumberRow(title: "Bitrate", value: $s.customMbps, range: Quality.customMbpsRange, unit: "Mbps")
        }
        ChoiceRow(title: "Codec", hint: "HEVC files are about half the size; H.264 plays everywhere",
                  options: VideoCodec.allCases.map { ($0, $0.short) }, selection: $s.codec)
        ToggleRow(title: "Show mouse cursor", isOn: $s.showCursor)
        if !videoNotes.isEmpty { Note(text: videoNotes) }
    }

    /// 30 / 60 / 120, or 0 for Custom.
    private var fpsChoice: Binding<Int> {
        Binding(get: { s.customFps ? 0 : s.fps }, set: { v in
            if v == 0 { s.customFps = true } else { s.customFps = false; s.fps = v }
        })
    }

    /// Fastest refresh rate of the screen being captured (the fallback display when an app is chosen).
    private var refreshRate: Int {
        let id = Displays.resolve(s.displayID)
        let screen = NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == id
        } ?? NSScreen.main
        return screen?.maximumFramesPerSecond ?? 60
    }

    private var videoNotes: String {
        var notes: [String] = []
        if s.fps > refreshRate {
            notes.append("This screen only refreshes \(refreshRate) times a second, so macOS can't capture more than \(refreshRate) fps. Above that, clips just get bigger, not smoother.")
        }
        if s.fps >= 120 {
            let x = Double(s.fps) / 60
            let times = x == x.rounded() ? "\(Int(x))×" : String(format: "%.1f×", x)
            notes.append("At \(s.fps) fps macOS copies and converts \(times) as many frames as at 60 fps, which uses more of the GPU your game is using. 60 fps is easier on frame rates.")
        }
        return notes.joined(separator: "\n\n")
    }

    // MARK: Audio

    @ViewBuilder private var audio: some View {
        ToggleRow(title: "Record system audio", isOn: $s.systemAudio)
        ToggleRow(title: "Record microphone", isOn: $s.microphone)
        ToggleRow(title: "Mix microphone into the main track", isOn: $s.mixMicrophone)
            .disabled(!(s.microphone && s.systemAudio)).opacity(s.microphone && s.systemAudio ? 1 : 0.4)
        if s.microphone && s.systemAudio {
            Note(text: s.mixMicrophone
                 ? "Game sound and your voice share one track, so clips play correctly everywhere (VLC, browsers, Discord, phones)."
                 : "Game sound and your voice are saved as separate tracks, handy for editing. Some players only play the first one.")
        }
    }

    // MARK: Instant Replay

    @ViewBuilder private var replay: some View {
        ChoiceRow(title: "Replay length",
                  options: [15, 30, 60, 120, 180, 300, 600, 900, 1200].map { ($0, $0 < 60 ? "\($0)s" : "\($0 / 60)m") },
                  selection: $s.replaySeconds)
        ToggleRow(title: "Turn on Instant Replay when Rewind starts", isOn: $s.startReplayOnLaunch)
        Note(text: estimate)
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

    // MARK: Saving

    @ViewBuilder private var saving: some View {
        SRow(title: "Save to", hint: s.outputDirectory.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
            SButton(title: "Choose…", action: chooseFolder)
        }
        ToggleRow(title: "Delete old clips automatically", isOn: $s.autoCleanup)
        ChoiceRow(title: "Keep clips for", options: Cleanup.dayOptions.map { ($0, "\($0)d") }, selection: $s.cleanupDays)
            .disabled(!s.autoCleanup).opacity(s.autoCleanup ? 1 : 0.4)
        Note(text: s.autoCleanup
             ? "Replays, recordings and screenshots older than \(s.cleanupDays) \(s.cleanupDays == 1 ? "day" : "days") are moved to the Trash, so you can still get them back. Other files in the folder are never touched."
             : "Clips are kept until you delete them.")
    }

    /// The folder picker is a normal window, which would open behind the overlay, so the overlay steps aside for it.
    private func chooseFolder() {
        OverlayController.shared.hide()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            let s = Settings.shared
            let panel = NSOpenPanel()
            panel.canChooseDirectories = true
            panel.canChooseFiles = false
            panel.canCreateDirectories = true
            panel.directoryURL = s.outputDirectory
            panel.prompt = "Choose"
            NSApp.activate(ignoringOtherApps: true)
            if panel.runModal() == .OK, let url = panel.url { s.outputDirectory = url }
            OverlayController.shared.showSettings(.saving)
        }
    }

    // MARK: Overlay

    @ViewBuilder private var overlay: some View {
        ChoiceRow(title: "Layout", options: OverlayLayout.allCases.map { ($0, $0.name) },
                  selection: Binding(get: { s.overlayLayout }, set: { v in withAnimation(.easeInOut(duration: 0.2)) { s.overlayLayout = v } }))
        if s.overlayLayout == .sidebar {
            ChoiceRow(title: "Appearance", options: OverlayAppearance.allCases.map { ($0, $0.label) }, selection: $s.overlayAppearance)
        }
        SRow(title: "Accent color", hint: "Instant Replay, highlights and notifications") {
            HStack(spacing: 7) {
                ForEach(Theme.accentPresets, id: \.hex) { preset in
                    Button { s.accentHex = preset.hex } label: {
                        Circle().fill(Color(hex: preset.hex)).frame(width: 18, height: 18)
                            .overlay(Circle().strokeBorder(st.text.opacity(s.accentHex == preset.hex ? 0.9 : 0.15),
                                                           lineWidth: s.accentHex == preset.hex ? 2 : 1))
                    }
                    .buttonStyle(.plain).help(preset.name)
                }
                Button { withAnimation(.easeOut(duration: 0.15)) { wheelOpen.toggle() } } label: {
                    Image(systemName: "eyedropper").font(.system(size: 11, weight: .semibold)).foregroundStyle(st.value)
                        .frame(width: 22, height: 22)
                        .overlay(Circle().strokeBorder(st.line))
                        .contentShape(Circle())
                }
                .buttonStyle(.plain).help("Any color, or a hex code")
            }
        }
        if wheelOpen {
            ColorWheelPicker(hex: $s.accentHex)
                .overlay(alignment: .bottom) { Rectangle().fill(st.rowLine).frame(height: 1) }
        }
        SliderRow(title: "Dim the game", value: $s.overlayDim, range: 0...0.9)
        if s.overlayLayout == .classic || s.overlayLayout == .panes {
            SliderRow(title: "Player size", hint: s.overlayLayout == .classic ? "In the Gallery" : nil,
                      value: $s.playerSize, range: Theme.playerSizeRange, step: 10)
        }
        ToggleRow(title: "Show hotkeys in the overlay", isOn: $s.showHotkeys)
    }

    // MARK: Hotkeys

    @ViewBuilder private var hotkeys: some View {
        ForEach(HotkeyAction.allCases) { action in
            SRow(title: action.label) { HotkeyField(action: action) }
        }
        SRow(title: "Reset to defaults") { SButton(title: "Reset") { s.resetHotkeys() } }
        Note(text: "Click a shortcut, then press the new keys. Press Esc to remove it. On a Mac keyboard, hold fn for F-keys unless they're set to work as standard function keys.")
    }

    // MARK: General

    @ViewBuilder private var general: some View {
        ToggleRow(title: "Open Rewind at login", isOn: $launchAtLogin)
            .onChange(of: launchAtLogin) { _, on in
                do {
                    if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                } catch {
                    Toast.error("Couldn't change login item", error)
                    launchAtLogin = SMAppService.mainApp.status == .enabled
                }
            }
        if c.isCapturing {
            SRow(title: "Capturing now", hint: c.captureInfo) { EmptyView() }
        }
        SRow(title: "Version") { Text(u.currentVersion).font(valueFont).foregroundStyle(st.value) }
        ToggleRow(title: "Check for updates when Rewind starts", isOn: $s.checkForUpdates)
        SRow(title: "Updates", hint: updateStatus) { updateButton }
        Note(text: "Updates come from Rewind's GitHub releases. Each one is checked against Rewind's signing key before it's installed, then Rewind reopens by itself.")
        SRow(title: "Uninstall Rewind",
             hint: "Deletes Rewind, its settings and caches, and removes its permissions and login item. Your clips are kept.") {
            SButton(title: "Uninstall…", destructive: true) {
                OverlayController.shared.hide() // the confirmation is a normal window
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                    NSApp.activate(ignoringOtherApps: true)
                    Uninstaller.confirmAndRun()
                }
            }
        }
    }

    @ViewBuilder private var updateButton: some View {
        switch u.state {
        case .available(let r): SButton(title: "Install \(r.version)") { u.install(r) }
        case .downloading, .installing, .checking: SButton(title: "Check now") {}.disabled(true).opacity(0.4)
        default: SButton(title: "Check now") { u.check(userInitiated: true) }
        }
    }

    private var updateStatus: String? {
        switch u.state {
        case .checking: "Checking…"
        case .upToDate: "Rewind is up to date"
        case .available(let r): "Version \(r.version) is available"
        case .downloading: "Downloading…"
        case .installing: "Installing…"
        case .failed(let m): m
        case .idle: nil
        }
    }
}

// MARK: - Sheet (Split)

/// Settings floating over the Split layout: quick toggles on top, categories on the left.
struct SettingsSheet: View {
    let page: SettingsPage
    let close: () -> Void
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var c = Controller.shared
    @ObservedObject private var state = OverlayState.shared
    private let st = SettingsStyle.sheet

    private var height: CGFloat { min(700, (NSScreen.main?.frame.height ?? 900) - 120) }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 16) {
                Button { back() } label: {
                    Text("← Back").font(.system(size: 13)).foregroundStyle(st.muted).contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("Back to the overlay (Esc)")
                Text("Settings").font(.system(size: 17, weight: .semibold)).foregroundStyle(st.text)
                Spacer()
                Button(action: close) { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(st.muted) }
                    .buttonStyle(.plain).help("Close")
            }
            .padding(.horizontal, 24).frame(height: 58)
            hr
            HStack(spacing: 10) {
                QuickTile(title: Theme.replayTitle, value: c.replayActive ? formatDuration(Double(s.replaySeconds)) : "Off",
                          on: c.replayActive) { c.toggleReplay() }
                QuickTile(title: "Frame rate", value: "\(s.fps)", on: false) { state.settings = .video }
                QuickTile(title: "System audio", value: s.systemAudio ? "On" : "Off", on: s.systemAudio) { s.systemAudio.toggle() }
                QuickTile(title: "Microphone", value: s.microphone ? "On" : "Off", on: s.microphone) { s.microphone.toggle() }
            }
            .padding(.horizontal, 24).padding(.vertical, 18)
            hr
            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(SettingsPage.allCases) { p in
                        NavItem(title: p.title, on: p == page, st: st) { state.settings = p }
                    }
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 10)
                .frame(width: 180)
                Rectangle().fill(st.line).frame(width: 1)
                ScrollView {
                    SettingsPageView(page: page).padding(.horizontal, 24).padding(.vertical, 6)
                }
                .id(page)
            }
        }
        .frame(width: 800, height: height)
        .background(Color(hex: 0x121312))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(st.line))
        .shadow(color: .black.opacity(0.6), radius: 40, y: 20)
        .environment(\.settingsStyle, st)
    }

    private var hr: some View { Rectangle().fill(st.line).frame(height: 1) }
    private func back() { withAnimation(.easeOut(duration: 0.15)) { state.settings = nil } }
}

private struct QuickTile: View {
    let title: String
    let value: String
    let on: Bool
    let action: () -> Void
    @State private var hover = false
    @Environment(\.settingsStyle) private var st

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.system(size: 12)).foregroundStyle(st.muted).lineLimit(1)
                Text(value).font(.system(size: 18, weight: .semibold, design: .monospaced)).foregroundStyle(on ? st.fill : st.text)
            }
            .padding(.horizontal, 14).padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(hover ? 0.07 : 0.045)))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(on ? st.fill : st.line))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

/// A category in a settings list: Console's side column and the sheet's left column.
struct NavItem: View {
    let title: String
    var detail: String? = nil
    let on: Bool
    let st: SettingsStyle
    var bar = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack {
                Text(title).font(.system(size: 14)).foregroundStyle(on ? Color.white : st.value)
                Spacer(minLength: 8)
                if let detail {
                    Text(detail).font(.system(size: 11, design: .monospaced)).foregroundStyle(st.muted).lineLimit(1)
                }
            }
            .padding(.horizontal, bar ? 18 : 24).padding(.vertical, bar ? 12 : 9)
            .background(on ? Color.white.opacity(0.05) : (hover ? st.hover : .clear))
            .overlay(alignment: .leading) { if on && bar { Rectangle().fill(st.fill).frame(width: 2) } }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
