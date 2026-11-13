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

    @State private var gallery = false // Classic layout: clip pane shown under the bar

    var body: some View {
        ZStack(alignment: s.overlayLayout == .classic ? .top : .center) {
            Color.black.opacity(Theme.dim).ignoresSafeArea().onTapGesture(perform: close)
            switch s.overlayLayout {
            case .classic: classic
            case .tiles: tiles
            case .panes: panes
            }
        }
        .environment(\.colorScheme, .dark)
        .foregroundStyle(.white)
    }

    // MARK: Two panes

    private var panes: some View {
            HStack(alignment: .top, spacing: Theme.s(10)) {
                TimelineView(.periodic(from: .now, by: 1)) { _ in
                    actions
                        .padding(Theme.s(16))
                        .frame(width: Theme.s(Theme.actionsWidth))
                        .panelSurface(radius: Theme.s(Theme.radius * 0.7))
                }
                ClipPane(close: close, reservedWidth: Theme.s(Theme.actionsWidth + 10), reservedHeight: Theme.s(70))
                    .padding(Theme.s(16))
                    .panelSurface(radius: Theme.s(Theme.radius * 0.7))
            }
            .fixedSize()
    }

    // MARK: Tiles

    /// Width of the three tiles side by side.
    private let tilesWidth = Theme.s(150 * 3 + 14 * 2)

    /// The original layout: one panel with three big tiles, a player with your newest clip under them,
    /// the recent clips row and a footer.
    private var tiles: some View {
        VStack(alignment: .leading, spacing: Theme.s(18)) {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                VStack(alignment: .leading, spacing: Theme.s(18)) {
                    tilesHeader
                    HStack(spacing: Theme.s(14)) {
                        replayTile
                        recordTile
                        screenshotTile
                    }
                }
            }
            ClipPane(close: close, fixedWidth: tilesWidth, reservedHeight: Theme.s(420), showsClose: false)
            tilesFooter
        }
        .padding(Theme.s(22))
        .fixedSize()
        .panelSurface(radius: Theme.s(Theme.radius))
    }

    private var tilesHeader: some View {
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
                        hint: on ? s.hotkey(.saveReplay) : s.hotkey(.toggleReplay),
                        caption: on ? "Save last \(formatDuration(c.bufferedSeconds))" : "Click to turn on",
                        hoverCaption: on ? "Right-click to turn off" : nil,
                        progress: on ? c.bufferedSeconds / Double(max(s.replaySeconds, 1)) : nil,
                        action: { on ? c.saveReplay() : c.startReplay() },
                        rightAction: { c.toggleReplay() })
    }

    private var recordTile: some View {
        let on = c.recording
        return IconTile(icon: on ? "stop.fill" : "record.circle", title: Theme.recordTitle, tint: Theme.record,
                        badge: on ? "REC" : nil, hint: s.hotkey(.toggleRecording),
                        caption: on ? "Click to stop" : "Start recording",
                        progress: nil, action: { c.toggleRecording() }, rightAction: { c.toggleRecording() })
    }

    private var screenshotTile: some View {
        IconTile(icon: "camera", title: Theme.screenshotTitle, tint: Theme.screenshot, badge: nil,
                 hint: s.hotkey(.screenshot), caption: "Full-res PNG", progress: nil,
                 flashOnTap: true, action: { c.screenshot() }, rightAction: { c.screenshot() })
    }

    private var tilesFooter: some View {
        HStack(spacing: Theme.s(8)) {
            Chip(title: Theme.folderLabel, icon: "folder") { close(); c.openClipsFolder() }
            Chip(title: Theme.settingsLabel, icon: "gearshape") { close(); SettingsWindow.shared.show() }
            Spacer()
            Text("Sound: \(s.systemAudio ? "system" : "off")\(s.microphone ? " + mic" : "")")
                .font(Theme.font(12)).foregroundStyle(Theme.muted)
        }
    }

    // MARK: Classic

    /// ShadowPlay-style: a title strip across the top, square tiles (label, icon, status) in a dark panel,
    /// a Gallery panel to the right with small round buttons, and a hotkey hint bar underneath.
    private var classic: some View {
        TimelineView(.periodic(from: .now, by: 1)) { _ in
            ZStack(alignment: .top) {
                // Title strip
                HStack {
                    Image(systemName: "arrow.counterclockwise")
                        .font(.system(size: Theme.s(12), weight: .heavy)).foregroundStyle(.black)
                        .frame(width: Theme.s(24), height: Theme.s(24))
                        .background(RoundedRectangle(cornerRadius: Theme.s(4), style: .continuous).fill(Theme.accent))
                    Spacer()
                    CircleButton(icon: "xmark", action: close).help("Close (Esc)")
                }
                .overlay { Text(Theme.brand).font(Theme.font(15, .semibold)) }
                .padding(.horizontal, Theme.s(14)).frame(height: Theme.s(40))
                .frame(maxWidth: .infinity)
                .background(Color.black.opacity(0.45))

                VStack(spacing: Theme.s(8)) {
                    HStack(alignment: .top, spacing: Theme.s(8)) {
                        HStack(spacing: Theme.s(6)) {
                            ClassicTile(title: Theme.replayTitle, icon: "arrow.counterclockwise", tint: Theme.replay,
                                        status: c.replayActive ? "On · \(formatDuration(c.bufferedSeconds))" : "Off", on: c.replayActive,
                                        progress: c.replayActive ? c.bufferedSeconds / Double(max(s.replaySeconds, 1)) : nil,
                                        hover: { hovered = $0 ? .replay : nil },
                                        action: { c.replayActive ? c.saveReplay() : c.startReplay() }, rightAction: { c.toggleReplay() })
                            ClassicTile(title: Theme.recordTitle, icon: c.recording ? "stop.fill" : "record.circle", tint: Theme.record,
                                        status: c.recording ? "Recording · \(formatDuration(Date().timeIntervalSince(c.recordingStart ?? Date())))" : "Not recording",
                                        on: c.recording, progress: nil, hover: { hovered = $0 ? .record : nil },
                                        action: { c.toggleRecording() }, rightAction: { c.toggleRecording() })
                            ClassicTile(title: Theme.screenshotTitle, icon: "camera", tint: Theme.screenshot, status: "Full-res PNG",
                                        on: false, progress: nil, flashOnTap: true, hover: { hovered = $0 ? .screenshot : nil },
                                        action: { c.screenshot() }, rightAction: { c.screenshot() })
                        }
                        .padding(Theme.s(6))
                        .classicPanel()

                        VStack(spacing: Theme.s(8)) {
                            ClassicWide(icon: "film.stack", title: "Gallery", on: gallery) {
                                withAnimation(.easeOut(duration: 0.15)) { gallery.toggle() }
                            }
                            HStack(spacing: Theme.s(8)) {
                                ClassicRound(icon: s.microphone ? "mic.fill" : "mic.slash.fill", on: s.microphone) { s.microphone.toggle() }
                                    .help(s.microphone ? "Microphone on (click to turn off)" : "Microphone off (click to turn on)")
                                ClassicRound(icon: "folder.fill", on: false) { close(); c.openClipsFolder() }.help(Theme.folderLabel)
                                ClassicRound(icon: "gearshape.fill", on: false) { close(); SettingsWindow.shared.show() }.help(Theme.settingsLabel)
                            }
                        }
                        .padding(Theme.s(10))
                        .frame(width: Theme.s(150))
                        .frame(maxHeight: .infinity)
                        .classicPanel()
                    }
                    .fixedSize()

                    HStack(spacing: Theme.s(6)) {
                        Image(systemName: "keyboard").font(.system(size: Theme.s(10), weight: .semibold))
                        Text(classicHint).font(Theme.font(10.5))
                    }
                    .foregroundStyle(.white.opacity(0.85))
                    .padding(.horizontal, Theme.s(10)).padding(.vertical, Theme.s(5))
                    .classicPanel()

                    if gallery {
                        ClipPane(close: close, reservedHeight: Theme.s(300))
                            .padding(Theme.s(14))
                            .fixedSize()
                            .classicPanel()
                            .transition(.opacity.combined(with: .move(edge: .top)))
                    }
                }
                .padding(.top, Theme.s(gallery ? 64 : 150))
            }
        }
    }

    private var classicHint: String {
        func line(_ a: HotkeyAction, _ what: String) -> String {
            let h = s.hotkey(a); return h.isSet ? "Press \(h.display) to \(what)" : "No hotkey set to \(what)"
        }
        switch hovered {
        case .replay?: return c.replayActive ? line(.saveReplay, "save the last \(formatDuration(Double(s.replaySeconds)))") : line(.toggleReplay, "turn on Instant Replay")
        case .record?: return line(.toggleRecording, c.recording ? "stop recording" : "start recording")
        default: return line(.screenshot, "save a screenshot to the Gallery")
        }
    }

    private enum HoverTile { case replay, record, screenshot }
    @State private var hovered: HoverTile?

    /// Left pane: logo, the three actions, status, Folder and Settings.
    private var actions: some View {
        VStack(alignment: .leading, spacing: Theme.s(8)) {
            HStack(spacing: Theme.s(8)) {
                Image(systemName: "arrow.counterclockwise")
                    .font(.system(size: Theme.s(12), weight: .heavy))
                    .foregroundStyle(.black)
                    .frame(width: Theme.s(24), height: Theme.s(24))
                    .background(RoundedRectangle(cornerRadius: Theme.s(7), style: .continuous).fill(
                        LinearGradient(colors: [Theme.accent, Theme.accent.mix(with: .black, by: 0.45)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing)))
                Text(Theme.brand).font(Theme.font(17, .bold))
                Spacer(minLength: 0)
                if c.recording, let start = c.recordingStart {
                    RecPill(text: formatDuration(Date().timeIntervalSince(start)))
                }
            }
            .padding(.bottom, Theme.s(6))

            replayRow
            recordRow
            screenshotRow

            Spacer(minLength: Theme.s(10))

            Text(c.isCapturing ? c.captureInfo : "Not capturing")
                .font(Theme.font(10.5)).foregroundStyle(Theme.muted).lineLimit(2)
            Text("Sound: \(s.systemAudio ? "system" : "off")\(s.microphone ? " + mic" : "")")
                .font(Theme.font(10.5)).foregroundStyle(Theme.muted)
            HStack(spacing: Theme.s(6)) {
                Chip(title: Theme.folderLabel, icon: "folder") { close(); c.openClipsFolder() }
                Chip(title: nil, icon: "gearshape") { close(); SettingsWindow.shared.show() }.help(Theme.settingsLabel)
            }
            .padding(.top, Theme.s(2))
        }
    }

    private var replayRow: some View {
        let on = c.replayActive
        return ActionRow(icon: "arrow.counterclockwise", title: on ? "Save replay" : Theme.replayTitle, tint: Theme.replay, on: on,
                         hint: on ? s.hotkey(.saveReplay) : s.hotkey(.toggleReplay),
                         caption: on ? "Last \(formatDuration(c.bufferedSeconds)) ready" : "Click to turn on",
                         hoverCaption: on ? "Right-click to turn off" : nil,
                         progress: on ? c.bufferedSeconds / Double(max(s.replaySeconds, 1)) : nil,
                         action: { on ? c.saveReplay() : c.startReplay() },
                         rightAction: { c.toggleReplay() })
    }

    private var recordRow: some View {
        let on = c.recording
        return ActionRow(icon: on ? "stop.fill" : "record.circle", title: on ? "Stop recording" : Theme.recordTitle, tint: Theme.record, on: on,
                         hint: s.hotkey(.toggleRecording), caption: on ? "Recording…" : "Start recording",
                         progress: nil, action: { c.toggleRecording() }, rightAction: { c.toggleRecording() })
    }

    private var screenshotRow: some View {
        ActionRow(icon: "camera", title: Theme.screenshotTitle, tint: Theme.screenshot, on: false,
                  hint: s.hotkey(.screenshot), caption: "Full-res PNG", progress: nil, flashOnTap: true,
                  action: { c.screenshot() }, rightAction: { c.screenshot() })
    }
}

/// One action in the left pane: round icon, title, caption and hotkey. Left and right clicks are separate.
private struct ActionRow: View {
    let icon: String
    let title: String
    let tint: Color
    let on: Bool
    let hint: Hotkey
    let caption: String
    var hoverCaption: String? = nil
    let progress: Double?
    var flashOnTap = false
    let action: () -> Void
    let rightAction: () -> Void

    @State private var hover = false
    @State private var pressed = false
    @State private var flash = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Theme.s(12), style: .continuous)
        HStack(spacing: Theme.s(9)) {
            ZStack {
                Circle().fill(on ? tint.opacity(0.24) : Color.white.opacity(hover ? 0.12 : 0.08))
                if let progress {
                    Circle().trim(from: 0, to: min(max(progress, 0), 1))
                        .stroke(tint, style: StrokeStyle(lineWidth: Theme.s(2.2), lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .padding(Theme.s(1))
                        .animation(.linear(duration: 0.9), value: progress)
                }
                Image(systemName: icon)
                    .font(.system(size: Theme.s(15), weight: .semibold))
                    .foregroundStyle(on ? tint : .white.opacity(0.9))
            }
            .frame(width: Theme.s(36), height: Theme.s(36))
            VStack(alignment: .leading, spacing: Theme.s(1)) {
                Text(title).font(Theme.font(12, .semibold)).lineLimit(1)
                Text(hover ? (hoverCaption ?? caption) : caption)
                    .font(Theme.font(10)).foregroundStyle(Theme.muted).lineLimit(1)
            }
            Spacer(minLength: Theme.s(4))
            if Theme.showHotkeys, hint.isSet {
                Text(hint.display).font(Theme.mono(9.5))
                    .padding(.horizontal, Theme.s(5)).padding(.vertical, Theme.s(2))
                    .background(RoundedRectangle(cornerRadius: Theme.s(4)).fill(Color.white.opacity(0.07)))
                    .opacity(0.75)
            }
        }
        .padding(Theme.s(7))
        .background(shape.fill(on ? AnyShapeStyle(tint.opacity(0.1)) : AnyShapeStyle(Color.white.opacity(hover ? 0.09 : 0.05))))
        .overlay(shape.strokeBorder(on ? tint.opacity(0.6) : Theme.line))
        .overlay(shape.fill(Color.white.opacity(flash ? 0.25 : 0)).allowsHitTesting(false))
        .scaleEffect(pressed ? 0.97 : 1)
        .overlay(ClickCatcher(
            onHover: { h in withAnimation(.easeOut(duration: 0.15)) { hover = h } },
            onPress: { p in withAnimation(p ? .easeOut(duration: 0.08) : .spring(duration: 0.25)) { pressed = p } },
            onLeft: { fire(action) },
            onRight: { fire(rightAction) }))
    }

    private func fire(_ f: () -> Void) {
        if flashOnTap {
            flash = true
            withAnimation(.easeOut(duration: 0.35)) { flash = false }
        }
        f()
    }
}

/// Classic layout tile: label on top, big icon, status under it. Accent outline when on or hovered.
private struct ClassicTile: View {
    let title: String
    let icon: String
    let tint: Color
    let status: String
    let on: Bool
    let progress: Double?
    var flashOnTap = false
    let hover: (Bool) -> Void
    let action: () -> Void
    let rightAction: () -> Void

    @State private var hovering = false
    @State private var pressed = false
    @State private var flash = false

    var body: some View {
        VStack(spacing: Theme.s(8)) {
            Text(title).font(Theme.font(11, .semibold)).lineLimit(1)
            ZStack {
                Circle().strokeBorder(.white.opacity(on ? 0 : 0.9), lineWidth: Theme.s(2.5))
                if on { Circle().fill(tint.opacity(0.22)); Circle().strokeBorder(tint.opacity(0.5), lineWidth: Theme.s(2.5)) }
                if let progress {
                    Circle().trim(from: 0, to: min(max(progress, 0), 1))
                        .stroke(tint, style: StrokeStyle(lineWidth: Theme.s(2.5), lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .animation(.linear(duration: 0.9), value: progress)
                }
                Image(systemName: icon).font(.system(size: Theme.s(17), weight: .bold)).foregroundStyle(on ? tint : .white)
            }
            .frame(width: Theme.s(40), height: Theme.s(40))
            .scaleEffect(pressed ? 0.93 : 1)
            Text(status).font(Theme.font(9.5)).foregroundStyle(on ? tint : Theme.muted).lineLimit(1)
        }
        .padding(.vertical, Theme.s(12)).padding(.horizontal, Theme.s(8))
        .frame(width: Theme.s(104))
        .background(Rectangle().fill(Color.white.opacity(flash ? 0.25 : (hovering ? 0.06 : 0))))
        .overlay(Rectangle().strokeBorder(on || hovering ? Theme.accent : .clear, lineWidth: Theme.s(1.5)))
        .overlay(ClickCatcher(
            onHover: { h in withAnimation(.easeOut(duration: 0.12)) { hovering = h }; hover(h) },
            onPress: { p in withAnimation(p ? .easeOut(duration: 0.08) : .spring(duration: 0.25)) { pressed = p } },
            onLeft: { fire(action) },
            onRight: { fire(rightAction) }))
    }

    private func fire(_ f: () -> Void) {
        if flashOnTap { flash = true; withAnimation(.easeOut(duration: 0.35)) { flash = false } }
        f()
    }
}

/// Classic layout: the wide Gallery button.
private struct ClassicWide: View {
    let icon: String
    let title: String
    let on: Bool
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            HStack(spacing: Theme.s(8)) {
                Image(systemName: icon).font(.system(size: Theme.s(15), weight: .semibold))
                Text(title).font(Theme.font(11.5, .semibold))
            }
            .frame(maxWidth: .infinity).padding(.vertical, Theme.s(9))
            .background(Rectangle().fill(Color.white.opacity(hover || on ? 0.1 : 0.05)))
            .overlay(Rectangle().strokeBorder(on ? Theme.accent : Theme.line, lineWidth: on ? Theme.s(1.5) : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

/// Classic layout: small round icon button (mic, folder, settings).
private struct ClassicRound: View {
    let icon: String
    let on: Bool
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            Image(systemName: icon).font(.system(size: Theme.s(11), weight: .bold))
                .foregroundStyle(on ? Theme.accent : .white)
                .frame(width: Theme.s(30), height: Theme.s(30))
                .background(Circle().fill(Color.white.opacity(hover ? 0.16 : 0.08)))
                .overlay(Circle().strokeBorder(on ? Theme.accent.opacity(0.7) : .clear, lineWidth: 1))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

private extension View {
    /// Near-black, square-cornered panel of the Classic layout.
    func classicPanel() -> some View {
        background(Rectangle().fill(Color.black.opacity(0.78)))
            .overlay(Rectangle().strokeBorder(Color.white.opacity(0.08)))
    }
}

/// Big round icon button with title and hotkey hint (Tiles layout).
private struct IconTile: View {
    let icon: String
    let title: String
    let tint: Color
    let badge: String?
    let hint: Hotkey
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

            if Theme.showHotkeys, hint.isSet {
                Text(hint.display)
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
    let title: String?
    let icon: String
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            HStack(spacing: Theme.s(6)) {
                Image(systemName: icon).font(.system(size: Theme.s(12), weight: .semibold))
                if let title { Text(title).font(Theme.font(12, .medium)) }
            }
            .padding(.horizontal, Theme.s(title == nil ? 9 : 12)).padding(.vertical, Theme.s(7))
            .background(Capsule().fill(Color.white.opacity(hover ? 0.16 : 0.09)))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
