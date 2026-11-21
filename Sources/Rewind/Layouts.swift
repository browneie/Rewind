import AppKit
import SwiftUI

// The Console and Sidebar overlay layouts. Classic and Split live in Overlay.swift.

// MARK: - Shared pieces

/// What's being captured, as short lines: "Display 1", "2048×1280 · 120 fps", "HEVC · 33 Mbps".
@MainActor
private func captureLines() -> [String] {
    let c = Controller.shared, s = Settings.shared
    guard c.isCapturing, !c.captureInfo.isEmpty else { return ["Not capturing"] }
    let parts = c.captureInfo.components(separatedBy: " · ")
    var lines = [parts[0]]
    var i = 1
    while i < parts.count { lines.append(parts[i..<min(i + 2, parts.count)].joined(separator: " · ")); i += 2 }
    lines.append(s.systemAudio ? (s.microphone ? "System audio + mic" : "System audio") : (s.microphone ? "Microphone" : "No sound"))
    return lines
}

/// Plain text that acts as a button: brighter on hover.
private struct TextButton: View {
    let title: String
    var color: Color
    var underline = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Text(title).underline(underline, color: color.opacity(0.5))
                .foregroundStyle(color.opacity(hover ? 1 : 0.78))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

/// The selected clip, or a hint when there are none.
private struct Media: View {
    let item: ClipItem?
    let empty: Color
    @ObservedObject private var preview = ClipPreview.shared

    var body: some View {
        ZStack {
            Color.black
            if let item {
                if item.isVideo {
                    PlayerView(player: preview.player)
                } else if let image = NSImage(contentsOf: item.url) {
                    Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                }
            } else {
                Text("Clips and screenshots you save show up here.").font(.system(size: 13)).foregroundStyle(empty)
            }
        }
    }
}

private extension Hotkey {
    @MainActor var shown: String? { Theme.showHotkeys && isSet ? display : nil }
}

// MARK: - Console

/// One solid panel: actions and capture details on the left; the clip, its details and a filmstrip on the right.
struct ConsoleLayout: View {
    let close: () -> Void
    @ObservedObject private var c = Controller.shared
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var lib = ClipLibrary.shared
    @ObservedObject private var preview = ClipPreview.shared
    @ObservedObject private var state = OverlayState.shared
    @ObservedObject private var sharer = ClipSharer.shared

    static let bg = Color(hex: 0x0F100F), line = Color(hex: 0x2A2B29), faint = Color(hex: 0x1F201E)
    static let dim = Color(hex: 0x7D7E79), soft = Color(hex: 0xB5B6B1)
    private let sideWidth: CGFloat = 270, thumb = CGSize(width: 150, height: 94)

    private var screen: CGSize { NSScreen.main?.frame.size ?? CGSize(width: 1440, height: 900) }

    var body: some View {
        let width = min(1200, screen.width - 100), height = min(740, screen.height - 110)
        HStack(spacing: 0) {
            if let page = state.settings {
                settingsSide(page).frame(width: sideWidth)
                Rectangle().fill(Self.line).frame(width: 1)
                settingsMain(page)
            } else {
                TimelineView(.periodic(from: .now, by: 1)) { _ in side }.frame(width: sideWidth)
                Rectangle().fill(Self.line).frame(width: 1)
                main(width: width - sideWidth - 1)
            }
        }
        .frame(width: width, height: height)
        .background(Self.bg.opacity(0.97))
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Self.line))
        .shadow(color: .black.opacity(0.45), radius: 30, y: 12)
        .onAppear(perform: preview.selectNewestIfNeeded)
        .onChange(of: lib.items) { preview.selectNewestIfNeeded() }
    }

    private var hr: some View { Rectangle().fill(Self.line).frame(height: 1) }

    private var side: some View {
        VStack(spacing: 0) {
            HStack {
                Text(Theme.brand).font(.system(size: 15, weight: .semibold))
                Spacer()
                if let k = s.hotkey(.toggleOverlay).shown { Text(k).font(.system(size: 12, design: .monospaced)).foregroundStyle(Self.dim) }
            }
            .padding(.horizontal, 18).frame(height: 52)
            hr
            ConsoleRow(title: c.replayActive ? "Save replay" : Theme.replayTitle,
                       sub: c.replayActive ? "● \(formatDuration(c.bufferedSeconds)) buffered" : "Off · click to turn on",
                       subColor: c.replayActive ? Theme.accent : Self.dim,
                       hoverSub: c.replayActive ? "Right-click to turn off" : nil,
                       key: (c.replayActive ? s.hotkey(.saveReplay) : s.hotkey(.toggleReplay)).shown,
                       on: c.replayActive, tint: Theme.accent,
                       action: { c.replayActive ? c.saveReplay() : c.startReplay() }, right: { c.toggleReplay() })
            ConsoleRow(title: c.recording ? "Stop recording" : Theme.recordTitle,
                       sub: c.recording ? "● \(formatDuration(Date().timeIntervalSince(c.recordingStart ?? Date())))" : "Not recording",
                       subColor: c.recording ? Theme.record : Self.dim,
                       key: s.hotkey(.toggleRecording).shown, on: c.recording, tint: Theme.record,
                       action: { c.toggleRecording() }, right: { c.toggleRecording() })
            ConsoleRow(title: Theme.screenshotTitle, sub: nil, subColor: Self.dim, key: s.hotkey(.screenshot).shown,
                       on: false, tint: Theme.accent, flashOnTap: true,
                       action: { c.screenshot() }, right: { c.screenshot() })
            Spacer(minLength: 0)
            hr
            VStack(alignment: .leading, spacing: 6) {
                ForEach(captureLines(), id: \.self) { Text($0) }
            }
            .font(.system(size: 12, design: .monospaced)).foregroundStyle(Self.dim).lineLimit(1)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 18).padding(.vertical, 14)
            hr
            HStack(spacing: 0) {
                TextButton(title: Theme.folderLabel, color: .white) { close(); c.openClipsFolder() }
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 18)
                Rectangle().fill(Self.line).frame(width: 1)
                TextButton(title: Theme.settingsLabel, color: .white) { state.settings = .video }
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 18)
            }
            .font(.system(size: 13)).frame(height: 42)
        }
    }

    private func main(width: CGFloat) -> some View {
        let item = preview.item
        let busy = preview.isTrimming || preview.isSavingTrim
        return VStack(spacing: 0) {
            HStack(spacing: 20) {
                if let item {
                    Text(item.displayName).font(.system(size: 15)).lineLimit(1).layoutPriority(1)
                    Text(lib.details(item)).font(.system(size: 12, design: .monospaced)).foregroundStyle(Self.dim).lineLimit(1)
                } else {
                    Text("Recent clips").font(.system(size: 15))
                }
                Spacer(minLength: 10)
                HStack(spacing: 20) {
                    if let item {
                        if item.isVideo {
                            TextButton(title: preview.isSavingTrim ? "Saving…" : "Trim", color: Self.soft) { preview.beginTrim() }
                                .disabled(busy).opacity(busy ? 0.4 : 1)
                        }
                        TextButton(title: sharer.buttonTitle(for: item.url), color: Self.soft) { sharer.copy(item.url) }
                        TextButton(title: "Show in Finder", color: Self.soft) { let url = item.url; close(); reveal(url) }
                        TextButton(title: "Delete", color: Self.soft) { preview.close(); lib.trash(item) }
                    }
                    Button(action: close) { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(Self.dim) }
                        .buttonStyle(.plain).help("Close (Esc)")
                }
                .font(.system(size: 13)).fixedSize()
            }
            .padding(.horizontal, 18).frame(height: 52)
            hr
            Media(item: item, empty: Self.dim).padding([.horizontal, .top], 18)
            if item?.isVideo == true, preview.isTrimming || preview.convertProgress != nil {
                SaveAsRow().padding(.horizontal, 18).padding(.top, 10).padding(.bottom, 14)
            } else {
                strip(width: width - 36).padding(18)
            }
        }
    }

    // MARK: Settings

    private func settingsSide(_ page: SettingsPage) -> some View {
        VStack(spacing: 0) {
            HStack {
                TextButton(title: "← Clips", color: Self.soft) { state.settings = nil }.font(.system(size: 13)).help("Back (Esc)")
                Spacer()
                Text(Theme.settingsLabel).font(.system(size: 15, weight: .semibold))
            }
            .padding(.horizontal, 18).frame(height: 52)
            hr
            VStack(spacing: 0) {
                ForEach(SettingsPage.allCases) { p in
                    NavItem(title: p.title, detail: p.summary, on: p == page, st: .console, bar: true) { state.settings = p }
                }
            }
            .padding(.top, 6)
            Spacer(minLength: 0)
            hr
            VStack(alignment: .leading, spacing: 6) {
                ForEach(captureLines(), id: \.self) { Text($0) }
            }
            .font(.system(size: 12, design: .monospaced)).foregroundStyle(Self.dim).lineLimit(1)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 18).padding(.vertical, 14)
        }
    }

    private func settingsMain(_ page: SettingsPage) -> some View {
        VStack(spacing: 0) {
            HStack {
                Text(page.title).font(.system(size: 15))
                Spacer()
                Button(action: close) { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(Self.dim) }
                    .buttonStyle(.plain).help("Close")
            }
            .padding(.horizontal, 24).frame(height: 52)
            hr
            ScrollView {
                SettingsPageView(page: page).padding(.horizontal, 24).padding(.vertical, 6)
            }
            .id(page)
            hr
            HStack {
                Text("Esc to go back · changes save straight away").font(.system(size: 12)).foregroundStyle(Self.dim)
                Spacer()
            }
            .padding(.horizontal, 24).frame(height: 40)
        }
        .environment(\.settingsStyle, .console)
    }

    private func strip(width: CGFloat) -> some View {
        let fits = max(1, Int((width + 2) / (thumb.width + 2)))
        return HStack(alignment: .top, spacing: 2) {
            ForEach(lib.items.prefix(fits)) { item in
                ConsoleThumb(item: item, size: thumb, selected: item == preview.item)
            }
            Spacer(minLength: 0)
        }
        .frame(height: thumb.height + 18, alignment: .top)
    }
}

private struct ConsoleRow: View {
    let title: String
    let sub: String?
    let subColor: Color
    var hoverSub: String? = nil
    let key: String?
    let on: Bool
    let tint: Color
    var flashOnTap = false
    let action: () -> Void
    let right: () -> Void
    @State private var hover = false
    @State private var flash = false

    var body: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.system(size: 15))
                if let sub {
                    Text(hover ? (hoverSub ?? sub) : sub).font(.system(size: 12, design: .monospaced)).foregroundStyle(subColor)
                }
            }
            Spacer(minLength: 8)
            if let key { Text(key).font(.system(size: 12, design: .monospaced)).foregroundStyle(ConsoleLayout.dim) }
        }
        .padding(.horizontal, 18).padding(.vertical, 16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(on ? Color.white.opacity(0.035) : Color.white.opacity(hover ? 0.03 : 0))
        .overlay(alignment: .leading) { if on { Rectangle().fill(tint).frame(width: 2) } }
        .overlay(alignment: .bottom) { Rectangle().fill(ConsoleLayout.faint).frame(height: 1) }
        .overlay(Color.white.opacity(flash ? 0.14 : 0).allowsHitTesting(false))
        .overlay(ClickCatcher(onHover: { h in withAnimation(.easeOut(duration: 0.12)) { hover = h } }, onPress: { _ in },
                              onLeft: { fire(action) }, onRight: { fire(right) }))
    }

    private func fire(_ f: () -> Void) {
        if flashOnTap { flash = true; withAnimation(.easeOut(duration: 0.35)) { flash = false } }
        f()
    }
}

private struct ConsoleThumb: View {
    let item: ClipItem
    let size: CGSize
    let selected: Bool
    @ObservedObject private var lib = ClipLibrary.shared
    @State private var hover = false

    var body: some View {
        Button { ClipPreview.shared.open(item) } label: {
            VStack(alignment: .leading, spacing: 5) {
                ZStack(alignment: .bottomTrailing) {
                    Color.white.opacity(0.05)
                    if let t = lib.thumbs[item.url] {
                        Image(nsImage: t).resizable().aspectRatio(contentMode: .fill).frame(width: size.width, height: size.height).clipped()
                    }
                    Text(item.isVideo ? lib.durations[item.url].map(formatDuration) ?? "" : (lib.codecs[item.url] ?? "PNG"))
                        .font(.system(size: 11, design: .monospaced)).foregroundStyle(.white)
                        .padding(.horizontal, 4).padding(.vertical, 1)
                        .background(Color.black.opacity(0.75)).padding(4)
                }
                .frame(width: size.width, height: size.height)
                .overlay(Rectangle().strokeBorder(selected ? Theme.accent : Color.white.opacity(hover ? 0.35 : 0), lineWidth: 2))
                Text(timeAgo(item.date)).font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(selected ? .white : ConsoleLayout.dim)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(item.url.lastPathComponent)
    }
}

// MARK: - Sidebar

/// A full-height panel that slides in from the right edge, in light or dark (Settings → Overlay → Appearance).
struct SidebarLayout: View {
    let close: () -> Void
    @ObservedObject private var c = Controller.shared
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var lib = ClipLibrary.shared
    @ObservedObject private var preview = ClipPreview.shared
    @ObservedObject private var state = OverlayState.shared
    @ObservedObject private var sharer = ClipSharer.shared

    fileprivate struct Palette {
        let bg, text, muted, line, rowLine, wash: Color
        static let light = Palette(bg: Color(hex: 0xF3F1EC), text: Color(hex: 0x1B1B19), muted: Color(hex: 0x6B6A64),
                                   line: Color(hex: 0xD9D6CE), rowLine: Color(hex: 0xE2DFD8), wash: .black)
        static let dark = Palette(bg: Color(hex: 0x171716), text: Color(hex: 0xECEBE7), muted: Color(hex: 0x8E8D88),
                                  line: Color(hex: 0x2E2D2A), rowLine: Color(hex: 0x252422), wash: .white)
    }

    private let width: CGFloat = 470, pad: CGFloat = 30

    var body: some View {
        let scheme = s.overlayAppearance.colorScheme
        let p = scheme == .dark ? Palette.dark : Palette.light
        HStack(spacing: 0) {
            Spacer(minLength: 0).allowsHitTesting(false)
            panel(p)
                .frame(width: width)
                .clipped()
                .frame(maxHeight: .infinity)
                .background(p.bg)
                .overlay(alignment: .leading) { Rectangle().fill(p.line).frame(width: 1) }
                .shadow(color: .black.opacity(0.35), radius: 30)
                .environment(\.colorScheme, scheme)
                .foregroundStyle(p.text)
                .offset(x: state.open ? 0 : width + 40)
        }
        .ignoresSafeArea()
        .onAppear(perform: preview.selectNewestIfNeeded)
        .onChange(of: lib.items) { preview.selectNewestIfNeeded() }
    }

    @ViewBuilder private func panel(_ p: Palette) -> some View {
        if let page = state.settings { settingsPanel(p, page) } else { clipsPanel(p) }
    }

    private func clipsPanel(_ p: Palette) -> some View {
        let item = preview.item
        return VStack(alignment: .leading, spacing: 0) {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                VStack(alignment: .leading, spacing: 0) {
                    header(p)
                    actions(p)
                }
            }
            VStack(alignment: .leading, spacing: 0) {
                Media(item: item, empty: p.muted)
                    .frame(height: mediaHeight(item))
                if item?.isVideo == true, preview.isTrimming || preview.convertProgress != nil {
                    SaveAsRow().padding(.top, 10)
                } else if let item {
                    Text(item.displayName).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                        .padding(.top, 10)
                    Text(lib.details(item)).font(.system(size: 12, design: .monospaced)).foregroundStyle(p.muted)
                        .lineLimit(1).minimumScaleFactor(0.85).padding(.top, 3)
                    HStack(spacing: 18) {
                        if item.isVideo {
                            TextButton(title: preview.isSavingTrim ? "Saving…" : "Trim", color: p.muted, underline: true) { preview.beginTrim() }
                                .disabled(preview.isSavingTrim)
                        }
                        TextButton(title: sharer.buttonTitle(for: item.url), color: p.muted, underline: true) { sharer.copy(item.url) }
                        TextButton(title: "Show in Finder", color: p.muted, underline: true) { let url = item.url; close(); reveal(url) }
                        TextButton(title: "Delete", color: p.muted, underline: true) { preview.close(); lib.trash(item) }
                    }
                    .font(.system(size: 13)).padding(.top, 6)
                }
            }
            .padding(.horizontal, pad).padding(.top, 22)

            earlier(p, selected: item)
            Spacer(minLength: 0)
            footer(p)
        }
    }

    // MARK: Settings

    private func style(_ p: Palette) -> SettingsStyle {
        SettingsStyle(text: p.text, muted: p.muted, value: p.text.opacity(0.8), line: p.line, rowLine: p.rowLine,
                      selected: p.text, selectedText: p.bg, hover: p.wash.opacity(0.05), radius: 0, segmentWidth: 250)
    }

    private func settingsPanel(_ p: Palette, _ page: SettingsPage) -> some View {
        let tab = SettingsTab.holding(page)
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) {
                    TextButton(title: "← Back to clips", color: p.muted) { state.settings = nil }.font(.system(size: 13)).help("Back (Esc)")
                    Text(Theme.settingsLabel).font(.system(size: 30, weight: .bold)).tracking(-0.6)
                }
                Spacer()
                Button(action: close) { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)).foregroundStyle(p.muted) }
                    .buttonStyle(.plain).help("Close").padding(.top, 8)
            }
            .padding(.horizontal, pad).padding(.top, 30).padding(.bottom, 18)
            HStack(spacing: 20) {
                ForEach(SettingsTab.allCases) { t in
                    Button { state.settings = t.pages[0] } label: {
                        Text(t.title).font(.system(size: 13, weight: t == tab ? .semibold : .regular))
                            .foregroundStyle(t == tab ? p.text : p.muted)
                            .padding(.bottom, 12)
                            .overlay(alignment: .bottom) { if t == tab { Rectangle().fill(p.text).frame(height: 2) } }
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, pad)
            .overlay(alignment: .bottom) { Rectangle().fill(p.line).frame(height: 1) }
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(tab.pages) { pg in
                        if tab.pages.count > 1 { SettingsGroupTitle(title: pg.title) }
                        SettingsPageView(page: pg)
                    }
                }
                .padding(.horizontal, pad).padding(.bottom, 20).padding(.top, tab.pages.count > 1 ? 0 : 8)
            }
            .id(tab)
            HStack(spacing: 22) {
                TextButton(title: Theme.folderLabel, color: p.muted) { close(); c.openClipsFolder() }
                Spacer(minLength: 8)
                Text(footerInfo)
                    .font(.system(size: 12, design: .monospaced)).foregroundStyle(p.muted).lineLimit(1).fixedSize()
            }
            .font(.system(size: 13))
            .padding(.horizontal, pad).padding(.vertical, 16)
            .overlay(alignment: .top) { Rectangle().fill(p.line).frame(height: 1) }
        }
        .environment(\.settingsStyle, style(p))
    }

    private func header(_ p: Palette) -> some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 4) {
                Text(Theme.brand).font(.system(size: 30, weight: .bold)).tracking(-0.6)
                HStack(spacing: 6) {
                    Circle().fill(c.replayActive ? Theme.accent : p.muted.opacity(0.6)).frame(width: 9, height: 9)
                    Text(status).font(.system(size: 13)).foregroundStyle(p.muted).lineLimit(1)
                }
            }
            Spacer()
            Button(action: close) { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)).foregroundStyle(p.muted) }
                .buttonStyle(.plain).help("Close (Esc)").padding(.top, 8)
        }
        .padding(.horizontal, pad).padding(.top, 30).padding(.bottom, 18)
    }

    private var status: String {
        var parts: [String] = []
        if c.recording, let start = c.recordingStart { parts.append("Recording \(formatDuration(Date().timeIntervalSince(start)))") }
        parts.append(c.replayActive ? "Replay on · last \(formatDuration(c.bufferedSeconds)) ready" : "Replay off")
        if c.isCapturing, let source = c.captureInfo.components(separatedBy: " · ").first { parts.append(source) }
        return parts.joined(separator: " · ")
    }

    private func actions(_ p: Palette) -> some View {
        HStack(spacing: 0) {
            SidebarAction(title: c.replayActive ? "Save replay" : Theme.replayTitle,
                          key: (c.replayActive ? s.hotkey(.saveReplay) : s.hotkey(.toggleReplay)).shown,
                          hoverKey: c.replayActive ? "right-click: off" : nil,
                          on: c.replayActive, p: p,
                          action: { c.replayActive ? c.saveReplay() : c.startReplay() }, right: { c.toggleReplay() })
            Rectangle().fill(p.line).frame(width: 1)
            SidebarAction(title: c.recording ? "Stop" : Theme.recordTitle, key: s.hotkey(.toggleRecording).shown,
                          on: c.recording, tint: Theme.record, p: p,
                          action: { c.toggleRecording() }, right: { c.toggleRecording() })
            Rectangle().fill(p.line).frame(width: 1)
            SidebarAction(title: Theme.screenshotTitle, key: s.hotkey(.screenshot).shown, on: false, flashOnTap: true, p: p,
                          action: { c.screenshot() }, right: { c.screenshot() })
        }
        .frame(height: 64)
        .overlay(alignment: .top) { Rectangle().fill(p.line).frame(height: 1) }
        .overlay(alignment: .bottom) { Rectangle().fill(p.line).frame(height: 1) }
    }

    private func mediaHeight(_ item: ClipItem?) -> CGFloat {
        let w = width - pad * 2
        let natural = item.flatMap { lib.sizes[$0.url] } ?? CGSize(width: 16, height: 10)
        let aspect = natural.width > 0 && natural.height > 0 ? natural.width / natural.height : 1.6
        return min(max(w / aspect, w * 9 / 21), w * 3 / 4)
    }

    private func earlier(_ p: Palette, selected: ClipItem?) -> some View {
        let others = lib.items.filter { $0 != selected }.prefix(3)
        return VStack(alignment: .leading, spacing: 0) {
            if !others.isEmpty {
                Text("Earlier").font(.system(size: 12)).foregroundStyle(p.muted).padding(.bottom, 8)
                ForEach(Array(others)) { SidebarClipRow(item: $0, p: p) }
            }
        }
        .padding(.horizontal, pad).padding(.top, 20)
    }

    /// "2048×1280 · 60 fps · HEVC": the bitrate is left out so it fits.
    private var footerInfo: String {
        let l = captureLines()
        guard l.count > 2 else { return l.first ?? "" }
        return l[1] + " · " + (l[2].components(separatedBy: " · ").first ?? l[2])
    }

    private func footer(_ p: Palette) -> some View {
        HStack(spacing: 22) {
            TextButton(title: Theme.folderLabel, color: p.muted) { close(); c.openClipsFolder() }
            TextButton(title: Theme.settingsLabel, color: p.muted) { state.settings = .video }
            Spacer(minLength: 8)
            Text(footerInfo)
                .font(.system(size: 12, design: .monospaced)).foregroundStyle(p.muted).lineLimit(1).fixedSize()
        }
        .font(.system(size: 13))
        .padding(.horizontal, pad).padding(.vertical, 16)
        .overlay(alignment: .top) { Rectangle().fill(p.line).frame(height: 1) }
    }
}

private struct SidebarAction: View {
    let title: String
    let key: String?
    var hoverKey: String? = nil
    let on: Bool
    var tint: Color = Theme.accent
    var flashOnTap = false
    let p: SidebarLayout.Palette
    let action: () -> Void
    let right: () -> Void
    @State private var hover = false
    @State private var flash = false

    var body: some View {
        VStack(spacing: 4) {
            Text(title).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                .foregroundStyle(on ? p.bg : p.text)
            if let k = hover ? (hoverKey ?? key) : key {
                Text(k).font(.system(size: 11, design: .monospaced)).foregroundStyle(on ? tint : p.muted).lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(on ? p.text : p.wash.opacity(hover ? 0.05 : 0))
        .overlay(p.wash.opacity(flash ? 0.15 : 0).allowsHitTesting(false))
        .overlay(ClickCatcher(onHover: { h in withAnimation(.easeOut(duration: 0.12)) { hover = h } }, onPress: { _ in },
                              onLeft: { fire(action) }, onRight: { fire(right) }))
    }

    private func fire(_ f: () -> Void) {
        if flashOnTap { flash = true; withAnimation(.easeOut(duration: 0.35)) { flash = false } }
        f()
    }
}

private struct SidebarClipRow: View {
    let item: ClipItem
    let p: SidebarLayout.Palette
    @ObservedObject private var lib = ClipLibrary.shared
    @State private var hover = false

    /// Shorter than the player's line: length or format, codec, size and age.
    private var rowDetails: String {
        var parts: [String] = []
        if let d = lib.durations[item.url] { parts.append(formatDuration(d)) }
        if let c = lib.codecs[item.url] { parts.append(c) }
        if let bytes = (try? item.url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize {
            parts.append(ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file))
        }
        parts.append(timeAgo(item.date))
        return parts.joined(separator: " · ")
    }

    var body: some View {
        Button { ClipPreview.shared.open(item) } label: {
            HStack(spacing: 14) {
                ZStack {
                    p.wash.opacity(0.06)
                    if let t = lib.thumbs[item.url] {
                        Image(nsImage: t).resizable().aspectRatio(contentMode: .fill).frame(width: 84, height: 52).clipped()
                    }
                }
                .frame(width: 84, height: 52)
                VStack(alignment: .leading, spacing: 3) {
                    Text(item.displayName).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                    Text(rowDetails)
                        .font(.system(size: 12, design: .monospaced)).foregroundStyle(p.muted).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(.vertical, 8)
            .background(p.wash.opacity(hover ? 0.04 : 0))
            .overlay(alignment: .top) { Rectangle().fill(p.rowLine).frame(height: 1) }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(item.url.lastPathComponent)
    }
}
