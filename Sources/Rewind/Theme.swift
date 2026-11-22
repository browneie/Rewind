import SwiftUI
import AppKit

/// Look of the overlay and notifications.
/// Change values here and run ./build.sh to see them.
enum Theme {
    // Size multiplier for everything (mockup "Size").
    static let scale: CGFloat = 1.35
    // Corner roundness of the main panel; tiles use 72% of it, notifications 60% (mockup "Corner roundness").
    static let radius: CGFloat = 34
    // How opaque the dark tint over the blurred background is, 0–1 (mockup "Panel opacity").
    static let panelOpacity: Double = 0.64
    // How much the game behind the overlay is darkened, 0–1. Set in Settings → Overlay.
    @MainActor static var dim: Double { Settings.shared.overlayDim }
    // true = blurred glass behind the panel, false = solid panel.
    static let glass = true

    // Accent (and Instant Replay) color. Set in Settings → Overlay; presets below, or any color from the wheel.
    @MainActor static var accent: Color { Settings.shared.accentColor }
    @MainActor static var replay: Color { accent }
    static let record = Color(hex: 0xFF4D5E)
    static let screenshot = Color(hex: 0x4DA3FF)
    static let defaultAccent: UInt32 = 0x3DDC84
    static let accentPresets: [(name: String, hex: UInt32)] = [
        ("Rewind green", 0x3DDC84), ("Classic green", 0x76B900), ("Sky", 0x4DA3FF), ("Violet", 0xA78BFA),
        ("Amber", 0xFFB454), ("Rose", 0xFF6B9D), ("White", 0xF2F2F2),
    ]

    @MainActor static var showHotkeys: Bool { Settings.shared.showHotkeys }

    // Two-pane overlay: width of the actions pane and of the clip pane (before scaling).
    static let actionsWidth: CGFloat = 215
    // Player width (before scaling) is set in Settings → Overlay → Player size; these are its range.
    static let playerSizeRange: ClosedRange<Double> = 400...1000
    static let defaultPlayerSize: Double = 600

    // Text
    static let brand = "Rewind"
    static let replayTitle = "Instant Replay"
    static let recordTitle = "Record"
    static let screenshotTitle = "Screenshot"
    static let folderLabel = "Clips folder"
    static let settingsLabel = "Settings"

    // Derived — usually no need to touch these.
    static let panelBase = Color(red: 18 / 255, green: 18 / 255, blue: 22 / 255)
    static let line = Color.white.opacity(0.09)
    static let muted = Color.white.opacity(0.58)
    static func s(_ v: CGFloat) -> CGFloat { v * scale }
    static func font(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: s(size), weight: weight, design: .rounded)
    }
    static func mono(_ size: CGFloat, _ weight: Font.Weight = .semibold) -> Font {
        .system(size: s(size), weight: weight, design: .monospaced)
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255)
    }

    /// How light the color is, 0 (black) to 1 (white), as the eye sees it.
    var luminance: Double {
        guard let c = NSColor(self).usingColorSpace(.sRGB) else { return 0.5 }
        return 0.2126 * c.redComponent + 0.7152 * c.greenComponent + 0.0722 * c.blueComponent
    }

    /// 0xRRGGBB in sRGB, for saving.
    var hexValue: UInt32? {
        guard let c = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        let r = UInt32((c.redComponent * 255).rounded()), g = UInt32((c.greenComponent * 255).rounded()), b = UInt32((c.blueComponent * 255).rounded())
        return r << 16 | g << 8 | b
    }
}

/// Overlay layouts, chosen in Settings → Overlay.
/// The raw values are what's saved; "panes" is kept for Split so existing settings carry over.
enum OverlayLayout: String, CaseIterable, Identifiable {
    case classic, panes, console, sidebar
    var id: String { rawValue }
    var name: String {
        switch self {
        case .classic: "Classic"
        case .panes: "Split"
        case .console: "Console"
        case .sidebar: "Sidebar"
        }
    }
    var label: String {
        switch self {
        case .classic: "Classic (ShadowPlay-style)"
        case .panes: "Split (actions beside a player)"
        case .console: "Console (one solid panel)"
        case .sidebar: "Sidebar (slides in from the right)"
        }
    }
    /// The layout a saved value stands for; the old Tiles layout became Console.
    static func saved(_ raw: String) -> OverlayLayout? { raw == "tiles" ? .console : OverlayLayout(rawValue: raw) }
}

/// Light or dark for the Sidebar layout.
enum OverlayAppearance: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }
    var label: String {
        switch self {
        case .system: "Match system"
        case .light: "Light"
        case .dark: "Dark"
        }
    }
    /// The scheme to draw with right now.
    @MainActor var colorScheme: ColorScheme {
        switch self {
        case .light: .light
        case .dark: .dark
        case .system: NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? .dark : .light
        }
    }
}

/// Real behind-window blur (what the mockup's backdrop-filter stands in for).
struct GlassBackground: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = .hudWindow
        v.blendingMode = .behindWindow
        v.state = .active
        v.appearance = NSAppearance(named: .darkAqua)
        return v
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

/// The panel surface: blur + dark tint + hairline border + soft shadow.
struct PanelSurface: ViewModifier {
    let radius: CGFloat
    var shadow = true

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        content
            .background {
                ZStack {
                    if Theme.glass { GlassBackground() }
                    Theme.panelBase.opacity(Theme.glass ? Theme.panelOpacity : 0.96)
                }
                .clipShape(shape)
            }
            .overlay(shape.strokeBorder(Theme.line))
            .overlay(alignment: .top) {
                // faint top highlight
                shape.strokeBorder(LinearGradient(colors: [.white.opacity(0.07), .clear], startPoint: .top, endPoint: .center))
            }
            .shadow(color: .black.opacity(shadow ? 0.45 : 0), radius: Theme.s(40), y: Theme.s(15))
    }
}

extension View {
    func panelSurface(radius: CGFloat, shadow: Bool = true) -> some View {
        modifier(PanelSurface(radius: radius, shadow: shadow))
    }
}

/// Classic's square look: set on everything inside the Classic layout (its gallery, settings, buttons, chips).
private struct SquareChromeKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    var squareChrome: Bool {
        get { self[SquareChromeKey.self] }
        set { self[SquareChromeKey.self] = newValue }
    }
}
