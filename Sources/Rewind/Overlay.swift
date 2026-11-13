import AppKit
import SwiftUI

private final class OverlayPanel: NSPanel {
    var onCancel: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override func cancelOperation(_ sender: Any?) { onCancel?() }
}

/// Fullscreen in-game style overlay (default ⌥Z).
@MainActor
final class OverlayController {
    static let shared = OverlayController()
    private var panel: OverlayPanel?
    private var keyMonitor: Any?

    var isVisible: Bool { panel != nil }
    func toggle() { isVisible ? hide() : show() }

    /// Shows the overlay; with a URL, opens straight into the preview of that clip.
    func show(preview url: URL? = nil) {
        if let url { ClipPreview.shared.open(url: url) }
        ClipLibrary.shared.refresh()
        guard panel == nil else { return }
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main!
        let p = OverlayPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                             backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.level = .screenSaver
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        p.onCancel = { [weak self] in self?.hide() }
        p.contentView = NSHostingView(rootView: OverlayView(close: { [weak self] in self?.hide() }))
        p.setFrame(screen.frame, display: true)
        p.alphaValue = 0
        p.makeKeyAndOrderFront(nil)
        NSAnimationContext.runAnimationGroup { $0.duration = 0.12; p.animator().alphaValue = 1 }
        panel = p
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            let preview = ClipPreview.shared
            if preview.isTrimming { return e } // the trim bar handles Return (Trim) and Esc (Cancel)
            if e.keyCode == 53 { self?.hide(); return nil } // Esc
            if preview.item?.isVideo == true, e.modifierFlags.intersection([.command, .control, .option]).isEmpty {
                switch e.keyCode {
                case 49: preview.togglePlay(); return nil // Space
                case 123: preview.step(-1); return nil    // ←
                case 124: preview.step(1); return nil     // →
                default: break
                }
            }
            return e
        }
    }

    func hide() {
        guard let p = panel else { return }
        panel = nil
        ClipPreview.shared.close()
        if let m = keyMonitor { NSEvent.removeMonitor(m); keyMonitor = nil }
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.12; p.animator().alphaValue = 0 }) { p.orderOut(nil) }
    }
}

struct OverlayView: View {
    @ObservedObject private var c = Controller.shared
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var preview = ClipPreview.shared
    let close: () -> Void

    /// Width of the three tiles side by side.
    private let tilesWidth = Theme.s(150 * 3 + 14 * 2)

    var body: some View {
        ZStack {
            Color.black.opacity(Theme.dim).ignoresSafeArea().onTapGesture(perform: close)
            Group {
                if let item = preview.item {
                    ClipPreviewPane(item: item, close: close)
                } else {
                    TimelineView(.periodic(from: .now, by: 1)) { _ in
                        VStack(alignment: .leading, spacing: Theme.s(18)) {
                            header
                            HStack(spacing: Theme.s(14)) {
                                replayTile
                                recordTile
                                screenshotTile
                            }
                            RecentClipsRow(width: tilesWidth)
                            footer
                        }
                    }
                }
            }
            .padding(Theme.s(22))
            .fixedSize()
            .panelSurface(radius: Theme.s(Theme.radius))
        }
        .environment(\.colorScheme, .dark)
        .foregroundStyle(.white)
    }

    private var header: some View {
        HStack(spacing: Theme.s(12)) {
            HStack(spacing: Theme.s(9)) {
                Image(systemName: "arrow.counterclockwise")
                    .font(.system(size: Theme.s(13), weight: .heavy))
                    .foregroundStyle(.black)
                    .frame(width: Theme.s(26), height: Theme.s(26))
                    .background(RoundedRectangle(cornerRadius: Theme.s(8), style: .continuous).fill(
                        LinearGradient(colors: [Theme.accent, Theme.accent.mix(with: .black, by: 0.45)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing)))
                Text(Theme.brand).font(Theme.font(20, .bold))
            }
            if c.recording, let start = c.recordingStart {
                RecPill(text: formatDuration(Date().timeIntervalSince(start)))
            }
            Spacer(minLength: Theme.s(20))
            Text(c.isCapturing ? c.captureInfo : "Not capturing")
                .font(Theme.font(12)).foregroundStyle(Theme.muted)
            CircleButton(icon: "xmark", action: close).help("Close (Esc)")
        }
    }

    private var replayTile: some View {
        let on = c.replayActive
        return IconTile(icon: "arrow.counterclockwise", title: Theme.replayTitle, tint: Theme.replay,
                        badge: on ? "ON" : nil,
                        hint: on ? s.hotkey(.saveReplay).display : s.hotkey(.toggleReplay).display,
                        caption: on ? "Save last \(formatDuration(c.bufferedSeconds))" : "Click to turn on",
                        hoverCaption: on ? "Right-click to turn off" : nil,
                        progress: on ? c.bufferedSeconds / Double(max(s.replaySeconds, 1)) : nil,
                        action: { on ? c.saveReplay() : c.startReplay() },
                        rightAction: { c.toggleReplay() })
    }

    private var recordTile: some View {
        let on = c.recording
        return IconTile(icon: on ? "stop.fill" : "record.circle", title: Theme.recordTitle, tint: Theme.record,
                        badge: on ? "REC" : nil, hint: s.hotkey(.toggleRecording).display,
                        caption: on ? "Click to stop" : "Start recording",
                        progress: nil, action: { c.toggleRecording() }, rightAction: { c.toggleRecording() })
    }

    private var screenshotTile: some View {
        IconTile(icon: "camera", title: Theme.screenshotTitle, tint: Theme.screenshot, badge: nil,
                 hint: s.hotkey(.screenshot).display, caption: "Full-res PNG", progress: nil,
                 flashOnTap: true, action: { c.screenshot() }, rightAction: { c.screenshot() })
    }

    private var footer: some View {
        HStack(spacing: Theme.s(8)) {
            Chip(title: Theme.folderLabel, icon: "folder") { close(); c.openClipsFolder() }
            Chip(title: Theme.settingsLabel, icon: "gearshape") { close(); SettingsWindow.shared.show() }
            Spacer()
            Text("Sound: \(s.systemAudio ? "system" : "off")\(s.microphone ? " + mic" : "")")
                .font(Theme.font(12)).foregroundStyle(Theme.muted)
        }
    }
}

/// Big round icon button with title and hotkey hint (the mockup's "Big icons" tile style).
private struct IconTile: View {
    let icon: String
    let title: String
    let tint: Color
    let badge: String?
    let hint: String
    let caption: String
    var hoverCaption: String? = nil
    let progress: Double?
    var flashOnTap = false
    let action: () -> Void
    let rightAction: () -> Void

    @State private var hover = false
    @State private var pressed = false
    @State private var flash = false

    private var on: Bool { badge != nil }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Theme.s(Theme.radius * 0.72), style: .continuous)
        VStack(spacing: Theme.s(9)) {
            ZStack {
                Circle().fill(on ? tint.opacity(0.24) : Color.white.opacity(hover ? 0.11 : 0.07))
                if let progress {
                    Circle().trim(from: 0, to: min(max(progress, 0), 1))
                        .stroke(tint, style: StrokeStyle(lineWidth: Theme.s(3), lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .padding(Theme.s(1.5))
                        .animation(.linear(duration: 0.9), value: progress)
                }
                Image(systemName: icon)
                    .font(.system(size: Theme.s(30), weight: .semibold))
                    .foregroundStyle(on ? tint : .white.opacity(0.9))
            }
            .frame(width: Theme.s(72), height: Theme.s(72))
            .scaleEffect(pressed ? 0.94 : (hover ? 1.04 : 1))

            Text(title).font(Theme.font(14, .semibold))
            Text(hover ? (hoverCaption ?? caption) : caption)
                .font(Theme.font(11.5)).foregroundStyle(Theme.muted).lineLimit(1)

            if Theme.showHotkeys {
                Text(hint)
                    .font(Theme.mono(11))
                    .padding(.horizontal, Theme.s(6)).padding(.vertical, Theme.s(2))
                    .background(RoundedRectangle(cornerRadius: Theme.s(5)).fill(Color.white.opacity(0.07)))
                    .opacity(0.75)
            }
        }
        .padding(.top, Theme.s(18)).padding(.horizontal, Theme.s(12)).padding(.bottom, Theme.s(14))
        .frame(width: Theme.s(150))
        .background(shape.fill(
            on ? AnyShapeStyle(LinearGradient(colors: [tint.opacity(0.16), Color.white.opacity(0.05)],
                                              startPoint: .topLeading, endPoint: .center))
               : AnyShapeStyle(Color.white.opacity(hover ? 0.09 : 0.05))))
        .overlay(shape.strokeBorder(on ? tint.opacity(0.6) : Theme.line))
        .overlay(shape.fill(Color.white.opacity(flash ? 0.25 : 0)).allowsHitTesting(false))
        .overlay(alignment: .topTrailing) {
            if let badge {
                Text(badge).font(.system(size: Theme.s(10), weight: .heavy, design: .rounded))
                    .padding(.horizontal, Theme.s(7)).padding(.vertical, Theme.s(4))
                    .background(Capsule().fill(tint.opacity(0.25)))
                    .foregroundStyle(tint)
                    .padding(Theme.s(10))
            }
        }
        .overlay(ClickCatcher(
            onHover: { h in withAnimation(.easeOut(duration: 0.15)) { hover = h } },
            onPress: { p in withAnimation(p ? .easeOut(duration: 0.08) : .spring(duration: 0.25)) { pressed = p } },
            onLeft: { fire(action) },
            onRight: { fire(rightAction) }))
    }
}

extension IconTile {
    fileprivate func fire(_ f: () -> Void) {
        if flashOnTap {
            flash = true
            withAnimation(.easeOut(duration: 0.35)) { flash = false }
        }
        f()
    }
}

/// Transparent AppKit view on top of a tile that reports left and right clicks separately
/// (SwiftUI's tap gesture only sees the left button) plus hover and press state.
private struct ClickCatcher: NSViewRepresentable {
    let onHover: (Bool) -> Void
    let onPress: (Bool) -> Void
    let onLeft: () -> Void
    let onRight: () -> Void

    func makeNSView(context: Context) -> ClickView { ClickView() }
    func updateNSView(_ v: ClickView, context: Context) {
        v.onHover = onHover; v.onPress = onPress; v.onLeft = onLeft; v.onRight = onRight
    }

    final class ClickView: NSView {
        var onHover: (Bool) -> Void = { _ in }
        var onPress: (Bool) -> Void = { _ in }
        var onLeft: () -> Void = {}
        var onRight: () -> Void = {}

        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            trackingAreas.forEach(removeTrackingArea)
            addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                           owner: self))
        }
        override func mouseEntered(with event: NSEvent) { onHover(true) }
        override func mouseExited(with event: NSEvent) { onHover(false); onPress(false) }

        private func inside(_ e: NSEvent) -> Bool { bounds.contains(convert(e.locationInWindow, from: nil)) }

        override func mouseDown(with event: NSEvent) { onPress(true) }
        override func mouseUp(with event: NSEvent) { onPress(false); if inside(event) { onLeft() } }
        override func rightMouseDown(with event: NSEvent) { onPress(true) }
        override func rightMouseUp(with event: NSEvent) { onPress(false); if inside(event) { onRight() } }
        override func menu(for event: NSEvent) -> NSMenu? { nil }
    }
}

private struct RecPill: View {
    let text: String
    @State private var blink = false
    var body: some View {
        HStack(spacing: Theme.s(6)) {
            Circle().fill(Theme.record).frame(width: Theme.s(7), height: Theme.s(7)).opacity(blink ? 0.2 : 1)
            Text(text).font(Theme.mono(12, .bold))
        }
        .padding(.horizontal, Theme.s(10)).padding(.vertical, Theme.s(4))
        .background(Capsule().fill(Theme.record.opacity(0.22)))
        .foregroundStyle(Theme.record)
        .onAppear { withAnimation(.easeInOut(duration: 0.5).repeatForever()) { blink = true } }
    }
}

struct CircleButton: View {
    let icon: String
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            Image(systemName: icon).font(.system(size: Theme.s(11), weight: .heavy))
                .frame(width: Theme.s(28), height: Theme.s(28))
                .background(Circle().fill(Color.white.opacity(hover ? 0.16 : 0.09)))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

struct Chip: View {
    let title: String
    let icon: String
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            HStack(spacing: Theme.s(6)) {
                Image(systemName: icon).font(.system(size: Theme.s(12), weight: .semibold))
                Text(title).font(Theme.font(12, .medium))
            }
            .padding(.horizontal, Theme.s(12)).padding(.vertical, Theme.s(7))
            .background(Capsule().fill(Color.white.opacity(hover ? 0.16 : 0.09)))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
