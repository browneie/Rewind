import AppKit
import SwiftUI

/// Small notification in the top-right corner (doesn't need notification permission, shows over fullscreen apps).
@MainActor
enum Toast {
    private static var panel: NSPanel?
    private static var hideWork: DispatchWorkItem?

    static func error(_ title: String, _ error: Error) {
        show(title, error.localizedDescription, icon: "xmark.octagon.fill", tint: .red, duration: 6)
    }

    static func show(_ title: String, _ subtitle: String?, icon: String, tint: Color,
                     duration: Double = 3.5, onClick: (() -> Void)? = nil) {
        hideWork?.cancel()
        panel?.orderOut(nil)

        let view = ToastView(title: title, subtitle: subtitle, icon: icon, tint: tint, clickable: onClick != nil) {
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
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.25; p.animator().alphaValue = 0 }) {
            p.orderOut(nil)
        }
    }
}

private struct ToastView: View {
    let title: String
    let subtitle: String?
    let icon: String
    let tint: Color
    let clickable: Bool
    let action: () -> Void

    var body: some View {
        HStack(spacing: Theme.s(12)) {
            Image(systemName: icon)
                .font(.system(size: Theme.s(16), weight: .bold))
                .foregroundStyle(tint)
                .frame(width: Theme.s(30), height: Theme.s(30))
                .background(RoundedRectangle(cornerRadius: Theme.s(9), style: .continuous).fill(tint.opacity(0.22)))
            VStack(alignment: .leading, spacing: Theme.s(2)) {
                Text(title).font(Theme.font(13, .semibold))
                if let subtitle {
                    Text(subtitle).font(Theme.font(11.5)).foregroundStyle(Theme.muted).lineLimit(2)
                }
            }
            Spacer(minLength: 0)
            if clickable {
                Image(systemName: "chevron.right").font(.system(size: Theme.s(11), weight: .bold)).foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, Theme.s(14))
        .padding(.vertical, Theme.s(11))
        .frame(width: Theme.s(330))
        .panelSurface(radius: Theme.s(Theme.radius * 0.6), shadow: false)
        .shadow(color: .black.opacity(0.35), radius: Theme.s(14), y: Theme.s(6))
        .padding(Theme.s(16))
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
        .contentShape(Rectangle())
        .onTapGesture(perform: action)
    }
}
