import Foundation
import Carbon.HIToolbox
import AppKit

struct RewindError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum VideoCodec: String, CaseIterable, Identifiable {
    case h264, hevc
    var id: String { rawValue }
    var label: String {
        switch self {
        case .h264: "H.264 (most compatible)"
        case .hevc: "HEVC / H.265 (smaller files)"
        }
    }
    var short: String { self == .h264 ? "H.264" : "HEVC" }
}

enum Quality: String, CaseIterable, Identifiable {
    case medium, high, veryHigh, ultra
    var id: String { rawValue }
    var label: String {
        switch self {
        case .medium: "Medium"
        case .high: "High"
        case .veryHigh: "Very high"
        case .ultra: "Ultra"
        }
    }
    var bitsPerPixel: Double {
        switch self {
        case .medium: 0.04
        case .high: 0.07
        case .veryHigh: 0.10
        case .ultra: 0.15
        }
    }
}

enum Resolution: Int, CaseIterable, Identifiable {
    case native = 0, p2160 = 2160, p1440 = 1440, p1080 = 1080, p720 = 720
    var id: Int { rawValue }
    var label: String { self == .native ? "Native" : "\(rawValue)p" }
}

enum AudioTrack: Int, CaseIterable {
    case system = 0, mic = 1
}

struct CaptureSignature: Equatable {
    var displayID: UInt32
    /// "" = whole display; otherwise an app key from `AppTarget.key(for:)`.
    var appID: String
    var appName: String
    /// Whether that app is running right now (so capture retargets when it opens or quits).
    var appRunning: Bool
    var fps: Int
    var quality: Quality
    var codec: VideoCodec
    var resolution: Resolution
    var showCursor: Bool
    var systemAudio: Bool
    var microphone: Bool
}

enum CaptureMath {
    static func outputSize(native: CGSize, resolution: Resolution, codec: VideoCodec) -> CGSize {
        var w = native.width, h = native.height
        if resolution != .native, h > CGFloat(resolution.rawValue) {
            let s = CGFloat(resolution.rawValue) / h
            w *= s; h *= s
        }
        // Apple's hardware H.264 encoder tops out at 4096 px; HEVC goes to 8K.
        let maxDim: CGFloat = codec == .h264 ? 4096 : 8192
        if max(w, h) > maxDim {
            let s = maxDim / max(w, h)
            w *= s; h *= s
        }
        return CGSize(width: CGFloat(Int(w) & ~1), height: CGFloat(Int(h) & ~1))
    }

    static func bitrate(size: CGSize, fps: Int, quality: Quality, codec: VideoCodec) -> Int {
        var bps = Double(size.width * size.height) * Double(fps) * quality.bitsPerPixel
        if codec == .hevc { bps *= 0.7 }
        return Int(min(max(bps, 2_000_000), 150_000_000))
    }

    static func nativePixelSize(displayID: UInt32) -> CGSize {
        let wanted = displayID == 0 ? CGMainDisplayID() : displayID
        let screen = NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == wanted
        } ?? NSScreen.main
        guard let screen else { return CGSize(width: 1920, height: 1080) }
        return CGSize(width: screen.frame.width * screen.backingScaleFactor,
                      height: screen.frame.height * screen.backingScaleFactor)
    }
}

// MARK: - Hotkeys

struct Hotkey: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt32 // Carbon modifier mask
    var display: String

    /// An action with no shortcut. Saved like a normal hotkey so it survives restarts.
    static let none = Hotkey(keyCode: .max, modifiers: 0, display: "No bind set")
    var isSet: Bool { keyCode != Hotkey.none.keyCode }

    static let functionKeys: [UInt32: Int] = [
        0x7A: 1, 0x78: 2, 0x63: 3, 0x76: 4, 0x60: 5, 0x61: 6, 0x62: 7, 0x64: 8, 0x65: 9, 0x6D: 10,
        0x67: 11, 0x6F: 12, 0x69: 13, 0x6B: 14, 0x71: 15, 0x6A: 16, 0x40: 17, 0x4F: 18, 0x50: 19, 0x5A: 20,
    ]
    static let specialKeys: [UInt32: String] = [
        0x31: "Space", 0x24: "↩", 0x30: "⇥", 0x33: "⌫", 0x75: "⌦",
        0x7B: "←", 0x7C: "→", 0x7D: "↓", 0x7E: "↑", 0x73: "Home", 0x77: "End", 0x74: "PgUp", 0x79: "PgDn",
    ]

    static func modifierSymbols(_ m: UInt32) -> String {
        var s = ""
        if m & UInt32(controlKey) != 0 { s += "⌃" }
        if m & UInt32(optionKey) != 0 { s += "⌥" }
        if m & UInt32(shiftKey) != 0 { s += "⇧" }
        if m & UInt32(cmdKey) != 0 { s += "⌘" }
        return s
    }

    static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var m: UInt32 = 0
        if flags.contains(.control) { m |= UInt32(controlKey) }
        if flags.contains(.option) { m |= UInt32(optionKey) }
        if flags.contains(.shift) { m |= UInt32(shiftKey) }
        if flags.contains(.command) { m |= UInt32(cmdKey) }
        return m
    }

    static func keyName(keyCode: UInt32, characters: String?) -> String {
        if let f = functionKeys[keyCode] { return "F\(f)" }
        if let s = specialKeys[keyCode] { return s }
        return (characters ?? "?").uppercased()
    }

    init(keyCode: UInt32, modifiers: UInt32, display: String) {
        self.keyCode = keyCode; self.modifiers = modifiers; self.display = display
    }

    init(keyCode: Int, modifiers: Int, key: String) {
        self.init(keyCode: UInt32(keyCode), modifiers: UInt32(modifiers),
                  display: Hotkey.modifierSymbols(UInt32(modifiers)) + key)
    }

    /// Key equivalent + mask for showing the shortcut in an NSMenu.
    var menuKeyEquivalent: (String, NSEvent.ModifierFlags) {
        guard isSet else { return ("", []) }
        var flags: NSEvent.ModifierFlags = []
        if modifiers & UInt32(controlKey) != 0 { flags.insert(.control) }
        if modifiers & UInt32(optionKey) != 0 { flags.insert(.option) }
        if modifiers & UInt32(shiftKey) != 0 { flags.insert(.shift) }
        if modifiers & UInt32(cmdKey) != 0 { flags.insert(.command) }
        if let f = Hotkey.functionKeys[keyCode], let scalar = UnicodeScalar(UInt32(NSF1FunctionKey + f - 1)) {
            return (String(Character(scalar)), flags)
        }
        if let last = display.last, last.isLetter || last.isNumber {
            return (String(last).lowercased(), flags)
        }
        return ("", [])
    }
}

enum HotkeyAction: String, CaseIterable, Identifiable {
    case toggleOverlay, saveReplay, toggleReplay, toggleRecording, screenshot
    var id: String { rawValue }

    var label: String {
        switch self {
        case .toggleOverlay: "Show / hide overlay"
        case .saveReplay: "Save instant replay"
        case .toggleReplay: "Turn instant replay on / off"
        case .toggleRecording: "Start / stop recording"
        case .screenshot: "Take screenshot"
        }
    }

    var defaultHotkey: Hotkey {
        let opt = optionKey, optShift = optionKey | shiftKey
        switch self {
        case .toggleOverlay: return Hotkey(keyCode: kVK_ANSI_Z, modifiers: opt, key: "Z")
        case .saveReplay: return Hotkey(keyCode: kVK_F10, modifiers: opt, key: "F10")
        case .toggleReplay: return Hotkey(keyCode: kVK_F10, modifiers: optShift, key: "F10")
        case .toggleRecording: return Hotkey(keyCode: kVK_F9, modifiers: opt, key: "F9")
        case .screenshot: return Hotkey(keyCode: kVK_F1, modifiers: opt, key: "F1")
        }
    }
}

// MARK: - Settings

@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()
    private let d = UserDefaults.standard

    private enum K {
        static let display = "displayID", fps = "fps", quality = "quality", codec = "codec"
        static let resolution = "resolution", cursor = "showCursor", sysAudio = "systemAudio", mic = "microphone"
        static let replay = "replaySeconds", startReplay = "startReplayOnLaunch", output = "outputDirectory"
        static let hotkeys = "hotkeys"
        static let appID = "captureAppID", appName = "captureAppName"
        static let mixMic = "mixMicrophone"
        static let cleanup = "autoCleanup", cleanupDays = "cleanupDays"
        static let appPrefixes = "appPrefixes"
    }

    @Published var displayID: UInt32 { didSet { d.set(Int(displayID), forKey: K.display) } }
    @Published var captureAppID: String { didSet { d.set(captureAppID, forKey: K.appID) } }
    @Published var captureAppName: String { didSet { d.set(captureAppName, forKey: K.appName) } }
    @Published var fps: Int { didSet { d.set(fps, forKey: K.fps) } }
    @Published var quality: Quality { didSet { d.set(quality.rawValue, forKey: K.quality) } }
    @Published var codec: VideoCodec { didSet { d.set(codec.rawValue, forKey: K.codec) } }
    @Published var resolution: Resolution { didSet { d.set(resolution.rawValue, forKey: K.resolution) } }
    @Published var showCursor: Bool { didSet { d.set(showCursor, forKey: K.cursor) } }
    @Published var systemAudio: Bool { didSet { d.set(systemAudio, forKey: K.sysAudio) } }
    @Published var microphone: Bool { didSet { d.set(microphone, forKey: K.mic) } }
    @Published var mixMicrophone: Bool { didSet { d.set(mixMicrophone, forKey: K.mixMic) } }
    @Published var replaySeconds: Int { didSet { d.set(replaySeconds, forKey: K.replay) } }
    @Published var startReplayOnLaunch: Bool { didSet { d.set(startReplayOnLaunch, forKey: K.startReplay) } }
    @Published var outputDirectory: URL { didSet { d.set(outputDirectory.path, forKey: K.output) } }
    @Published var autoCleanup: Bool { didSet { d.set(autoCleanup, forKey: K.cleanup) } }
    @Published var cleanupDays: Int { didSet { d.set(cleanupDays, forKey: K.cleanupDays) } }
    /// App names used to name clips ("Roblox"), so cleanup and Recent clips still recognise those files.
    @Published private(set) var appPrefixes: [String] { didSet { d.set(appPrefixes, forKey: K.appPrefixes) } }
    @Published private(set) var hotkeys: [String: Hotkey] {
        didSet { d.set(try? JSONEncoder().encode(hotkeys), forKey: K.hotkeys) }
    }

    init() {
        d.register(defaults: [
            K.fps: 60, K.quality: Quality.high.rawValue, K.codec: VideoCodec.h264.rawValue,
            K.resolution: 0, K.cursor: true, K.sysAudio: true, K.mic: false, K.mixMic: true,
            K.replay: 120, K.startReplay: true, K.cleanup: false, K.cleanupDays: 14,
        ])
        displayID = UInt32(clamping: d.integer(forKey: K.display))
        captureAppID = d.string(forKey: K.appID) ?? ""
        captureAppName = d.string(forKey: K.appName) ?? ""
        fps = d.integer(forKey: K.fps)
        quality = Quality(rawValue: d.string(forKey: K.quality) ?? "") ?? .high
        codec = VideoCodec(rawValue: d.string(forKey: K.codec) ?? "") ?? .h264
        resolution = Resolution(rawValue: d.integer(forKey: K.resolution)) ?? .native
        showCursor = d.bool(forKey: K.cursor)
        systemAudio = d.bool(forKey: K.sysAudio)
        microphone = d.bool(forKey: K.mic)
        mixMicrophone = d.bool(forKey: K.mixMic)
        replaySeconds = d.integer(forKey: K.replay)
        startReplayOnLaunch = d.bool(forKey: K.startReplay)
        autoCleanup = d.bool(forKey: K.cleanup)
        cleanupDays = max(1, d.integer(forKey: K.cleanupDays))
        appPrefixes = d.stringArray(forKey: K.appPrefixes) ?? []
        outputDirectory = d.string(forKey: K.output).map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("Rewind")
        if let data = d.data(forKey: K.hotkeys),
           let hk = try? JSONDecoder().decode([String: Hotkey].self, from: data) {
            hotkeys = hk
        } else {
            hotkeys = [:]
        }
    }

    func hotkey(_ a: HotkeyAction) -> Hotkey { hotkeys[a.rawValue] ?? a.defaultHotkey }
    func setHotkey(_ h: Hotkey, for a: HotkeyAction) { hotkeys[a.rawValue] = h }
    func resetHotkeys() { hotkeys = [:] }
    func clearHotkey(_ a: HotkeyAction) { hotkeys[a.rawValue] = Hotkey.none }

    /// File name prefix for clips of an app: its name as the app spells it ("Roblox"), safe for a file name.
    func clipPrefix(forApp name: String) -> String? {
        let cleaned = name
            .replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        guard !cleaned.isEmpty, !Cleanup.builtInPrefixes.contains(where: { $0.caseInsensitiveCompare(cleaned) == .orderedSame }) else { return nil }
        if !appPrefixes.contains(cleaned) { appPrefixes.append(cleaned) }
        return cleaned
    }

    /// "⌥F10 to save", or "" when that action has no shortcut.
    func hotkeyHint(_ a: HotkeyAction, _ what: String) -> String {
        let h = hotkey(a)
        return h.isSet ? "\(h.display) \(what)" : ""
    }

    var captureSignature: CaptureSignature {
        CaptureSignature(displayID: displayID, appID: captureAppID, appName: captureAppName,
                         appRunning: !captureAppID.isEmpty && AppTarget.isRunning(captureAppID), fps: fps, quality: quality, codec: codec, resolution: resolution,
                         showCursor: showCursor, systemAudio: systemAudio, microphone: microphone)
    }
}

// MARK: - App targets

/// Identifies an app to capture. Uses the bundle ID when there is one; apps without one
/// (some games and Java processes) are matched by name.
enum AppTarget {
    static func key(bundleID: String?, name: String?) -> String {
        if let b = bundleID, !b.isEmpty { return b }
        return "name:" + (name ?? "")
    }

    static func key(for app: NSRunningApplication) -> String {
        key(bundleID: app.bundleIdentifier, name: app.localizedName)
    }

    static func isRunning(_ key: String) -> Bool {
        NSWorkspace.shared.runningApplications.contains { AppTarget.key(for: $0) == key }
    }

    /// Apps with a normal UI, for the picker (Rewind itself left out).
    static func pickable() -> [NSRunningApplication] {
        let me = ProcessInfo.processInfo.processIdentifier
        return NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.processIdentifier != me }
            .sorted { ($0.localizedName ?? "").localizedCaseInsensitiveCompare($1.localizedName ?? "") == .orderedAscending }
    }
}
