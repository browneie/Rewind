import AppKit
import AVKit
import ImageIO
import SwiftUI

/// A saved replay, recording or screenshot in the clips folder.
struct ClipItem: Identifiable, Hashable {
    enum Kind { case replay, recording, screenshot }

    let url: URL
    let date: Date
    var id: URL { url }

    var kind: Kind {
        let name = url.lastPathComponent
        if name.hasPrefix("Screenshot ") { return .screenshot }
        return name.hasPrefix("Recording ") ? .recording : .replay
    }
    var isVideo: Bool { kind != .screenshot }

    var title: String {
        switch kind {
        case .replay: "Replay"
        case .recording: "Recording"
        case .screenshot: "Screenshot"
        }
    }
    var icon: String {
        switch kind {
        case .replay: "arrow.counterclockwise"
        case .recording: "record.circle"
        case .screenshot: "camera"
        }
    }
    var tint: Color {
        switch kind {
        case .replay: Theme.replay
        case .recording: Theme.record
        case .screenshot: Theme.screenshot
        }
    }
}

/// The most recent clips, with thumbnails, lengths and sizes loaded in the background.
@MainActor
final class ClipLibrary: ObservableObject {
    static let shared = ClipLibrary()
    nonisolated static let limit = 5

    @Published private(set) var items: [ClipItem] = []
    @Published private(set) var thumbs: [URL: NSImage] = [:]
    @Published private(set) var durations: [URL: Double] = [:]
    @Published private(set) var sizes: [URL: CGSize] = [:]
    private var scan: Task<Void, Never>?
    private var loaded: [URL: Date] = [:] // file date the metadata was loaded for

    func refreshIfShown() { if OverlayController.shared.isVisible { refresh() } }

    func refresh() {
        let dir = Settings.shared.outputDirectory
        let busy = Controller.shared.router.recorder?.url // still being written
        scan?.cancel()
        scan = Task {
            let found = await Task.detached(priority: .userInitiated) {
                ClipLibrary.recent(in: dir, excluding: busy)
            }.value
            guard !Task.isCancelled else { return }
            items = found
            for item in found where loaded[item.url] != item.date {
                await load(item)
                guard !Task.isCancelled else { return }
            }
        }
    }

    nonisolated static func recent(in dir: URL, excluding busy: URL?) -> [ClipItem] {
        let keys: [URLResourceKey] = [.creationDateKey, .contentModificationDateKey, .isRegularFileKey]
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        let skip = busy?.standardizedFileURL
        return files.compactMap { url -> ClipItem? in
            guard Cleanup.isRewindFile(url.lastPathComponent), url.standardizedFileURL != skip,
                  let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { return nil }
            return ClipItem(url: url, date: v.creationDate ?? v.contentModificationDate ?? .distantPast)
        }
        .sorted { $0.date > $1.date }
        .prefix(limit)
        .map { $0 }
    }

    private func load(_ item: ClipItem) async {
        let url = item.url
        if item.isVideo {
            let asset = AVURLAsset(url: url)
            let duration = (try? await asset.load(.duration))?.seconds ?? 0
            durations[url] = duration
            if let track = try? await asset.loadTracks(withMediaType: .video).first,
               let (size, transform) = try? await track.load(.naturalSize, .preferredTransform) {
                let r = CGRect(origin: .zero, size: size).applying(transform)
                sizes[url] = CGSize(width: abs(r.width), height: abs(r.height))
            }
            let gen = AVAssetImageGenerator(asset: asset)
            gen.appliesPreferredTrackTransform = true
            gen.maximumSize = CGSize(width: 360, height: 360)
            gen.requestedTimeToleranceAfter = CMTime(seconds: 1, preferredTimescale: 600)
            let at = CMTime(seconds: min(1, duration / 2), preferredTimescale: 600)
            if let r = try? await gen.image(at: at) {
                thumbs[url] = NSImage(cgImage: r.image, size: .zero)
            }
        } else {
            let (image, size) = await Task.detached(priority: .userInitiated) { ClipLibrary.imageThumb(url) }.value
            if let image { thumbs[url] = NSImage(cgImage: image, size: .zero) }
            if let size { sizes[url] = size }
        }
        loaded[url] = item.date
    }

    nonisolated private static func imageThumb(_ url: URL) -> (CGImage?, CGSize?) {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return (nil, nil) }
        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
        let size = (props?[kCGImagePropertyPixelWidth] as? Int).flatMap { w in
            (props?[kCGImagePropertyPixelHeight] as? Int).map { CGSize(width: w, height: $0) }
        }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: 360,
            kCGImageSourceCreateThumbnailWithTransform: true,
        ]
        return (CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary), size)
    }

    func trash(_ item: ClipItem) {
        do {
            try FileManager.default.trashItem(at: item.url, resultingItemURL: nil)
            Toast.show("Moved to the Trash", item.url.lastPathComponent, icon: "trash.fill", tint: .gray)
        } catch {
            Toast.error("Couldn't move to the Trash", error)
        }
        refresh()
    }
}

/// What the overlay is previewing, and the player for it.
@MainActor
final class ClipPreview: ObservableObject {
    static let shared = ClipPreview()
    @Published private(set) var item: ClipItem?
    @Published private(set) var isTrimming = false
    @Published private(set) var isSavingTrim = false
    let player = AVPlayer()
    weak var playerView: AVPlayerView?

    func open(_ item: ClipItem) {
        self.item = item
        if item.isVideo {
            player.replaceCurrentItem(with: AVPlayerItem(url: item.url))
            player.play()
        } else {
            player.replaceCurrentItem(with: nil)
        }
    }

    func open(url: URL) {
        let date = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
        open(ClipItem(url: url, date: date))
    }

    func close() {
        isTrimming = false // a pending trim is ignored once the item changes
        player.pause()
        player.replaceCurrentItem(with: nil)
        item = nil
    }

    func togglePlay() {
        guard player.currentItem != nil else { return }
        if player.timeControlStatus == .paused {
            // Start over if it ended.
            if let item = player.currentItem, item.currentTime() >= item.duration { player.seek(to: .zero) }
            player.play()
        } else {
            player.pause()
        }
    }

    func step(_ frames: Int) {
        player.pause()
        player.currentItem?.step(byCount: frames)
    }

    // MARK: Trimming

    var canTrim: Bool { item?.isVideo == true && !isTrimming && !isSavingTrim && playerView?.canBeginTrimming == true }

    /// Shows the player's trim bar; on "Trim", saves the chosen part as a new file next to the original.
    func beginTrim() {
        guard canTrim, let view = playerView, let source = item else { return }
        player.pause()
        isTrimming = true
        view.window?.makeFirstResponder(view) // so Return / Esc go to the trim bar
        view.beginTrimming { [weak self] result in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.isTrimming = false
                guard result == .okButton, self.item == source, let current = self.player.currentItem else { return }
                let start = current.reversePlaybackEndTime.isValid ? current.reversePlaybackEndTime : .zero
                let end = current.forwardPlaybackEndTime.isValid ? current.forwardPlaybackEndTime : current.duration
                guard end > start else { return }
                self.saveTrim(source, range: CMTimeRange(start: start, end: end))
            }
        }
    }

    private func saveTrim(_ source: ClipItem, range: CMTimeRange) {
        isSavingTrim = true
        let dest = ClipTrimmer.outputURL(for: source.url)
        Task {
            do {
                try await ClipTrimmer.trim(source.url, range: range, to: dest)
                isSavingTrim = false
                ClipLibrary.shared.refresh()
                if self.item == source { open(url: dest) }
                Toast.show("Trimmed clip saved", "\(formatDuration(range.duration.seconds)) · \(dest.lastPathComponent)",
                           icon: "scissors", tint: Theme.accent) { OverlayController.shared.show(preview: dest) }
            } catch {
                isSavingTrim = false
                Toast.error("Couldn't trim the clip", error)
            }
        }
    }
}

/// Cuts a time range out of a clip without re-encoding (like ffmpeg's -c copy).
enum ClipTrimmer {
    /// "Replay … (trimmed).mp4", or "(trimmed 2)" etc. if that exists.
    static func outputURL(for source: URL) -> URL {
        let dir = source.deletingLastPathComponent()
        let ext = source.pathExtension.isEmpty ? "mp4" : source.pathExtension
        let base = source.deletingPathExtension().lastPathComponent
        var url = dir.appendingPathComponent("\(base) (trimmed).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(base) (trimmed \(n)).\(ext)"); n += 1
        }
        return url
    }

    static func trim(_ source: URL, range: CMTimeRange, to dest: URL) async throws {
        let asset = AVURLAsset(url: source)
        guard let export = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetPassthrough) else {
            throw RewindError("This clip can't be trimmed.")
        }
        export.timeRange = range
        export.shouldOptimizeForNetworkUse = true
        // Write to a hidden temp name first, so a half-written file never shows up as a clip.
        let tmp = dest.deletingLastPathComponent().appendingPathComponent(".trimming-\(UUID().uuidString).\(dest.pathExtension)")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try await export.export(to: tmp, as: dest.pathExtension.lowercased() == "mov" ? .mov : .mp4)
        try FileManager.default.moveItem(at: tmp, to: dest)
    }
}

// MARK: - Views

/// Row of the latest clips under the overlay's tiles.
struct RecentClipsRow: View {
    @ObservedObject private var lib = ClipLibrary.shared
    let width: CGFloat
    private let gap = Theme.s(8)

    var body: some View {
        let thumbW = (width - gap * CGFloat(ClipLibrary.limit - 1)) / CGFloat(ClipLibrary.limit)
        VStack(alignment: .leading, spacing: Theme.s(8)) {
            Text("Recent clips").font(Theme.font(12, .semibold)).foregroundStyle(Theme.muted)
            if lib.items.isEmpty {
                Text("Clips and screenshots you save show up here.")
                    .font(Theme.font(11.5)).foregroundStyle(Theme.muted)
                    .frame(width: width, height: thumbW * 9 / 16 + Theme.s(20))
                    .background(RoundedRectangle(cornerRadius: Theme.s(10), style: .continuous).fill(Color.white.opacity(0.04)))
            } else {
                HStack(spacing: gap) {
                    ForEach(lib.items) { ClipThumb(item: $0, width: thumbW) }
                    Spacer(minLength: 0)
                }
                .frame(width: width, alignment: .leading)
            }
        }
    }
}

private struct ClipThumb: View {
    let item: ClipItem
    let width: CGFloat
    @ObservedObject private var lib = ClipLibrary.shared
    @State private var hover = false

    var body: some View {
        let height = width * 9 / 16
        let shape = RoundedRectangle(cornerRadius: Theme.s(8), style: .continuous)
        Button { ClipPreview.shared.open(item) } label: {
            VStack(alignment: .leading, spacing: Theme.s(5)) {
                ZStack {
                    Color.white.opacity(0.06)
                    if let thumb = lib.thumbs[item.url] {
                        Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fill)
                    } else {
                        Image(systemName: item.icon).font(.system(size: Theme.s(16))).foregroundStyle(Theme.muted)
                    }
                    if hover {
                        Color.black.opacity(0.3)
                        Image(systemName: item.isVideo ? "play.fill" : "eye.fill")
                            .font(.system(size: Theme.s(16), weight: .bold))
                    }
                }
                .frame(width: width, height: height)
                .clipShape(shape)
                .overlay(shape.strokeBorder(hover ? item.tint.opacity(0.8) : Theme.line))
                .overlay(alignment: .topLeading) {
                    Image(systemName: item.icon).font(.system(size: Theme.s(8.5), weight: .bold))
                        .foregroundStyle(item.tint)
                        .padding(Theme.s(3.5))
                        .background(Circle().fill(.black.opacity(0.55)))
                        .padding(Theme.s(4))
                }
                .overlay(alignment: .bottomTrailing) {
                    if let d = lib.durations[item.url] {
                        Text(formatDuration(d)).font(Theme.mono(9, .bold))
                            .padding(.horizontal, Theme.s(4)).padding(.vertical, Theme.s(1.5))
                            .background(Capsule().fill(.black.opacity(0.6)))
                            .padding(Theme.s(4))
                    }
                }
                Text(timeAgo(item.date)).font(Theme.font(10.5)).foregroundStyle(Theme.muted).lineLimit(1)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { h in withAnimation(.easeOut(duration: 0.12)) { hover = h } }
        .help(item.url.lastPathComponent)
    }
}

/// The overlay's preview mode: video player or screenshot, with Back, Show in Finder and Trash.
struct ClipPreviewPane: View {
    let item: ClipItem
    let close: () -> Void
    @ObservedObject private var lib = ClipLibrary.shared
    @ObservedObject private var preview = ClipPreview.shared

    var body: some View {
        let size = media
        VStack(alignment: .leading, spacing: Theme.s(14)) {
            HStack(spacing: Theme.s(12)) {
                CircleButton(icon: "chevron.left") { preview.close() }.help("Back to the overlay")
                VStack(alignment: .leading, spacing: Theme.s(2)) {
                    HStack(spacing: Theme.s(7)) {
                        Image(systemName: item.icon).foregroundStyle(item.tint)
                        Text(item.title)
                    }
                    .font(Theme.font(16, .semibold))
                    Text(details).font(Theme.font(11.5)).foregroundStyle(Theme.muted).lineLimit(1)
                }
                Spacer(minLength: Theme.s(16))
                if item.isVideo {
                    Chip(title: preview.isSavingTrim ? "Saving…" : "Trim", icon: "scissors") { preview.beginTrim() }
                        .disabled(preview.isTrimming || preview.isSavingTrim)
                        .opacity(preview.isTrimming || preview.isSavingTrim ? 0.5 : 1)
                }
                Chip(title: "Show in Finder", icon: "folder") { let url = item.url; close(); reveal(url) }
                Chip(title: "Move to Trash", icon: "trash") { let it = item; preview.close(); lib.trash(it) }
                CircleButton(icon: "xmark", action: close).help("Close (Esc)")
            }
            ZStack {
                Color.black
                if item.isVideo {
                    PlayerView(player: preview.player)
                } else if let image = NSImage(contentsOf: item.url) {
                    Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                }
            }
            .frame(width: size.width, height: size.height)
            .clipShape(RoundedRectangle(cornerRadius: Theme.s(14), style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Theme.s(14), style: .continuous).strokeBorder(Theme.line))
            if item.isVideo {
                Text(preview.isTrimming
                     ? "Drag the yellow handles, then click Trim. The original clip is kept."
                     : "Space to play or pause · ← → to step a frame")
                    .font(Theme.font(11)).foregroundStyle(Theme.muted)
            }
        }
    }

    /// Fits the media's aspect ratio into at most 70% of the screen.
    private var media: CGSize {
        let screen = NSScreen.main?.visibleFrame.size ?? CGSize(width: 1440, height: 900)
        let maxW = min(Theme.s(640), screen.width * 0.7), maxH = screen.height * 0.6
        let natural = lib.sizes[item.url] ?? CGSize(width: 16, height: 9)
        let aspect = natural.width > 0 && natural.height > 0 ? natural.width / natural.height : 16 / 9
        var w = maxW, h = maxW / aspect
        if h > maxH { h = maxH; w = maxH * aspect }
        return CGSize(width: max(w, Theme.s(320)), height: h)
    }

    private var details: String {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        var parts = [f.string(from: item.date)]
        if let d = lib.durations[item.url] { parts.append(formatDuration(d)) }
        if let s = lib.sizes[item.url] { parts.append("\(Int(s.width))×\(Int(s.height))") }
        if let bytes = (try? item.url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize {
            parts.append(ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file))
        }
        return parts.joined(separator: " · ")
    }
}

private struct PlayerView: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> AVPlayerView {
        let v = AVPlayerView()
        v.player = player
        ClipPreview.shared.playerView = v
        v.controlsStyle = .floating
        v.showsFrameSteppingButtons = true
        v.showsFullScreenToggleButton = false
        v.allowsPictureInPicturePlayback = false
        v.videoGravity = .resizeAspect
        return v
    }

    func updateNSView(_ v: AVPlayerView, context: Context) {
        if v.player !== player { v.player = player }
        ClipPreview.shared.playerView = v
    }
}

func timeAgo(_ date: Date, now: Date = Date()) -> String {
    let s = Int(now.timeIntervalSince(date))
    if s < 60 { return "Just now" }
    if s < 3600 { return "\(s / 60)m ago" }
    if s < 86_400 { return "\(s / 3600)h ago" }
    return "\(s / 86_400)d ago"
}
