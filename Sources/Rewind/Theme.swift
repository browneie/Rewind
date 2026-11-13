import SwiftUI
import AppKit

/// Look of the overlay and notifications, from the design made in design/overlay-mockup.html.
/// Change values here and run ./build.sh to see them.
enum Theme {
    // Size multiplier for everything (mockup "Size").
    static let scale: CGFloat = 1.35
    // Corner roundness of the main panel; tiles use 72% of it, notifications 60% (mockup "Corner roundness").
    static let radius: CGFloat = 34
    // How opaque the dark tint over the blurred background is, 0–1 (mockup "Panel opacity").
    static let panelOpacity: Double = 0.64
    // How much the game behind the overlay is darkened, 0–1 (mockup "Dim the game").
    static let dim: Double = 0.6
    // true = blurred glass behind the panel, false = solid panel.
    static let glass = true

    static let accent = Color(hex: 0x3DDC84)
    static let replay = Color(hex: 0x3DDC84)
    static let record = Color(hex: 0xFF4D5E)
    static let screenshot = Color(hex: 0x4DA3FF)

    static let showHotkeys = true

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
