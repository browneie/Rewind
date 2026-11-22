import AppKit
import SwiftUI

/// Small notification in the top-right corner (doesn't need notification permission, shows over fullscreen apps).
@MainActor
enum Toast {
    private static var panel: NSPanel?
    private static var hideWork: DispatchWorkItem?
    /// While held (the launch update check) or while a pinned notice is up (the update offer), other notices
    /// wait; the most recent one shows once the way is clear.
    private static var held = false
    private static var pinnedUp = false
    private static var waiting: (() -> Void)?

    /// Keeps other notices back until `release()` or `seconds` pass, whichever comes first.
    static func hold(upTo seconds: Double) {
        held = true
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { release() }
    }

    static func release() {
        guard held else { return }
        held = false
        showWaiting()
    }

    private static func showWaiting() {
        guard !held, !pinnedUp, let next = waiting else { return }
        waiting = nil
        next()
    }

    static func error(_ title: String, _ error: Error) {
        show(title, error.localizedDescription, icon: "xmark.octagon.fill", tint: .red, duration: 6)
    }

    /// `closable` adds an ✕ that dismisses the notice without running `onClick`.
    /// `pinned` notices can't be replaced: others wait until it's clicked, closed or times out.
    /// `button` adds a small button (like "Copy") that runs its own action instead of `onClick`.
    static func show(_ title: String, _ subtitle: String?, icon: String, tint: Color,
                     duration: Double = 3.5, closable: Bool = false, pinned: Bool = false,
                     button: (title: String, action: () -> Void)? = nil, onClick: (() -> Void)? = nil) {
        if !pinned, held || pinnedUp {
            waiting = { show(title, subtitle, icon: icon, tint: tint, duration: duration, closable: closable, button: button, onClick: onClick) }
            return
        }
        pinnedUp = pinned
        hideWork?.cancel()
        panel?.orderOut(nil)

        let view = ToastView(title: title, subtitle: subtitle, icon: icon, tint: tint, clickable: onClick != nil,
                             closable: closable, close: { dismiss() },
                             button: button.map { b in (b.title, { b.action(); dismiss() }) }) {
            onClick?()
            dismiss()
        }
        let host = NSHostingView(rootView: view)
        let size = host.fittingSize
        let p = NSPanel(contentRect: NSRect(origin: .zero, size: size),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.contentView = host
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.level = .screenSaver
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        if let vf = screen?.visibleFrame {
            p.setFrameOrigin(NSPoint(x: vf.maxX - size.width, y: vf.maxY - size.height))
        }
        p.alphaValue = 0
        p.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.15; p.animator().alphaValue = 1 }
        panel = p

        let work = DispatchWorkItem { dismiss() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + duration, execute: work)
    }

    private static func dismiss() {
        guard let p = panel else { return }
        panel = nil
        let wasPinned = pinnedUp
        pinnedUp = false
        NSAnimationContext.runAnimationGroup { $0.duration = 0.25; p.animator().alphaValue = 0 }
        // A timer, not the animation's completion: animations pause while the Mac is locked or asleep,
        // which would leave the panel up and any waiting notice stuck.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            p.orderOut(nil)
            if wasPinned { showWaiting() }
        }
    }
}

private struct ToastView: View {
    let title: String
    let subtitle: String?
    let icon: String
    let tint: Color
    let clickable: Bool
    var closable = false
    var close: () -> Void = {}
    var button: (title: String, action: () -> Void)? = nil
    let action: () -> Void
    /// Classic's style: square, near-black, with an edge in the notice's color.
    private let classic = MainActor.assumeIsolated { Settings.shared.overlayLayout == .classic }

    var body: some View {
        HStack(spacing: Theme.s(12)) {
            Image(systemName: icon)
                .font(.system(size: Theme.s(16), weight: .bold))
                .foregroundStyle(tint)
                .frame(width: Theme.s(30), height: Theme.s(30))
                .background(RoundedRectangle(cornerRadius: classic ? 0 : Theme.s(9), style: .continuous).fill(tint.opacity(0.22)))
            VStack(alignment: .leading, spacing: Theme.s(2)) {
                Text(title).font(Theme.font(13, .semibold))
                if let subtitle {
                    Text(subtitle).font(Theme.font(11.5)).foregroundStyle(Theme.muted).lineLimit(2)
                }
            }
            Spacer(minLength: 0)
            if let button {
                Button(action: button.action) {
                    Text(button.title).font(Theme.font(11, .semibold))
                        .padding(.horizontal, Theme.s(10)).padding(.vertical, Theme.s(5))
                        .background(RoundedRectangle(cornerRadius: classic ? 0 : Theme.s(20)).fill(Color.white.opacity(0.14)))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            if closable {
                Button(action: close) {
                    Image(systemName: "xmark").font(.system(size: Theme.s(10), weight: .heavy))
                        .frame(width: Theme.s(22), height: Theme.s(22))
                        .background(Circle().fill(Color.white.opacity(0.1)))
                }
                .buttonStyle(.plain)
                .help("Not now")
            } else if clickable {
                Image(systemName: "chevron.right").font(.system(size: Theme.s(11), weight: .bold)).foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, Theme.s(14))
        .padding(.vertical, Theme.s(11))
        .frame(width: Theme.s(330))
        .modifier(ToastSurface(classic: classic, tint: tint))
        .shadow(color: .black.opacity(0.35), radius: Theme.s(14), y: Theme.s(6))
        .padding(Theme.s(16))
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
        .contentShape(Rectangle())
        .onTapGesture(perform: action)
    }
}

private struct ToastSurface: ViewModifier {
    let classic: Bool
    let tint: Color

    func body(content: Content) -> some View {
        if classic {
            content
                .background(Rectangle().fill(Color(hex: 0x0B0B0B).opacity(0.96)))
                .overlay(alignment: .leading) { Rectangle().fill(tint).frame(width: Theme.s(3)) }
                .overlay(Rectangle().strokeBorder(Color.white.opacity(0.08)))
        } else {
            content.panelSurface(radius: Theme.s(Theme.radius * 0.6), shadow: false)
        }
    }
}
