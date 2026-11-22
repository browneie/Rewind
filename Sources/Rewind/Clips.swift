import AppKit
import AVKit
import ImageIO
import SwiftUI

/// A saved replay, recording or screenshot in the clips folder.
struct ClipItem: Identifiable, Hashable {
    enum Kind {
        case replay, recording, screenshot, app

        var tag: String {
            switch self {
            case .replay: "replay"
            case .recording: "recording"
            case .screenshot: "screenshot"
            case .app: "app"
            }
        }

        init?(tag: String) {
            switch tag {
            case "replay": self = .replay
            case "recording": self = .recording
            case "screenshot": self = .screenshot
            case "app": self = .app
            default: return nil
            }
        }
    }

    let url: URL
    let date: Date
    var id: URL { url }

    /// "Roblox" for clips named after the captured app.
    var appName: String? { Cleanup.appPrefix(of: url.lastPathComponent) }

    /// The game the clip is from: noted when it was saved, or the app it's named after.
    var game: String? { ClipTags.get(url, .game) ?? appName }

    var kind: Kind {
        let name = url.lastPathComponent
        if name.hasPrefix("Screenshot ") || url.pathExtension.lowercased() == "png" { return .screenshot }
        if name.hasPrefix("Recording ") { return .recording }
        if name.hasPrefix("Replay ") { return .replay }
        if let kind = ClipTags.get(url, .kind).flatMap(Kind.init(tag:)) { return kind }
        return .app
    }

    /// Renamed in the overlay ("insane clutch"): shown by its name, and kept by auto-cleanup.
    var isRenamed: Bool { ClipTags.get(url, .kind) != nil }
    var isVideo: Bool { kind != .screenshot }

    /// "Replay 22.19.19", "Roblox 16.53.58 (trimmed)": the kind or app, and the time from the file name.
    var displayName: String {
        let base = url.deletingPathExtension().lastPathComponent
        guard !isRenamed, let r = base.range(of: " at ") else { return base }
        return "\(kind == .screenshot ? (appName ?? "Screenshot") : title) \(base[r.upperBound...])"
    }

    var title: String {
        if isRenamed { return url.deletingPathExtension().lastPathComponent }
        return switch kind {
        case .replay: "Replay"
        case .recording: "Recording"
        case .screenshot: appName.map { "\($0) screenshot" } ?? "Screenshot"
        case .app: appName ?? "Clip"
        }
    }
    var icon: String {
        switch kind {
        case .replay: "gobackward"
        case .recording: "record.circle"
        case .screenshot: "camera"
        case .app: "gamecontroller.fill"
        }
    }
    @MainActor var tint: Color {
        switch kind {
        case .replay: Theme.replay
        case .recording: Theme.record
        case .screenshot: Theme.screenshot
        case .app: Theme.accent
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
    /// "H.264", "HEVC", "PNG"…
    @Published private(set) var codecs: [URL: String] = [:]
    /// The games clips were saved from, most recent first, and whether some clips aren't from any game.
    @Published private(set) var games: [String] = []
    @Published private(set) var hasOther = false
    /// Which game's clips to show: nil for all, `ClipLibrary.other` for clips without a game.
    @Published var gameFilter: String? { didSet { if gameFilter != oldValue { refresh() } } }
    nonisolated static let other = "\u{0}other"
    private var scan: Task<Void, Never>?
    private var loaded: [URL: Date] = [:] // file date the metadata was loaded for

    func refreshIfShown() { if OverlayController.shared.isVisible { refresh() } }

    func refresh() {
        let dir = Settings.shared.outputDirectory
        let busy = Controller.shared.router.recorder?.url // still being written
        let prefixes = Settings.shared.appPrefixes
        let filter = gameFilter
        scan?.cancel()
        scan = Task {
            let found = await Task.detached(priority: .userInitiated) {
                ClipLibrary.recent(in: dir, excluding: busy, appPrefixes: prefixes, game: filter)
            }.value
            guard !Task.isCancelled else { return }
            items = found.items
            games = found.games
            hasOther = found.hasOther
            if let filter, filter != Self.other, !found.games.contains(filter) { gameFilter = nil } // its last clip is gone
            for item in found.items where loaded[item.url] != item.date {
                await load(item)
                guard !Task.isCancelled else { return }
            }
        }
    }

    /// The newest clips (of one game, with `game`), and every game the folder has clips of.
    nonisolated static func recent(in dir: URL, excluding busy: URL?, appPrefixes: [String] = [],
                                   game: String? = nil) -> (items: [ClipItem], games: [String], hasOther: Bool) {
        let keys: [URLResourceKey] = [.creationDateKey, .contentModificationDateKey, .isRegularFileKey]
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return ([], [], false) }
        let skip = busy?.standardizedFileURL
        let all = files.compactMap { url -> ClipItem? in
            guard Cleanup.isRewindFile(url.lastPathComponent, appPrefixes: appPrefixes) || ClipTags.get(url, .kind) != nil,
                  url.standardizedFileURL != skip,
                  let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { return nil }
            return ClipItem(url: url, date: v.creationDate ?? v.contentModificationDate ?? .distantPast)
        }
        .sorted { $0.date > $1.date }
        var games: [String] = []
        var hasOther = false
        for item in all {
            if let g = item.game { if !games.contains(g) { games.append(g) } } else { hasOther = true }
        }
        let shown = game.map { g in all.filter { g == other ? $0.game == nil : $0.game == g } } ?? all
        return (Array(shown.prefix(limit)), games, hasOther)
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
                if let format = try? await track.load(.formatDescriptions).first {
                    codecs[url] = Self.codecName(CMFormatDescriptionGetMediaSubType(format))
                }
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
            codecs[url] = url.pathExtension.uppercased()
        }
        loaded[url] = item.date
    }

    nonisolated static func codecName(_ type: FourCharCode) -> String {
        switch type {
        case kCMVideoCodecType_H264: return "H.264"
        case kCMVideoCodecType_HEVC, kCMVideoCodecType_HEVCWithAlpha: return "HEVC"
        case kCMVideoCodecType_AV1: return "AV1"
        case kCMVideoCodecType_AppleProRes422, kCMVideoCodecType_AppleProRes4444, kCMVideoCodecType_AppleProRes422HQ,
             kCMVideoCodecType_AppleProRes422LT, kCMVideoCodecType_AppleProRes422Proxy: return "ProRes"
        default:
            let s = String(bytes: [24, 16, 8, 0].map { UInt8((type >> $0) & 0xFF) }, encoding: .ascii) ?? "?"
            return s.trimmingCharacters(in: .whitespaces).uppercased()
        }
    }

    /// "4m ago · 0:08 · 1600×1000 · HEVC · 11.3 MB": what's known about a clip so far.
    func details(_ item: ClipItem, ago: Bool = true) -> String {
        var parts = ago ? [timeAgo(item.date)] : []
        if let d = durations[item.url] { parts.append(formatDuration(d)) }
        if let s = sizes[item.url] { parts.append("\(Int(s.width))×\(Int(s.height))") }
        if let c = codecs[item.url] { parts.append(c) }
        if let bytes = (try? item.url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize {
            parts.append(ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file))
        }
        return parts.joined(separator: " · ")
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

    /// Renames a clip ("insane clutch.mp4"). It stays a Rewind clip (its kind and game go with it), and
    /// auto-cleanup, which only looks for Rewind's own names, leaves it alone. Returns the new file.
    @discardableResult
    func rename(_ item: ClipItem, to name: String) -> URL? {
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        let dir = item.url.deletingLastPathComponent(), ext = item.url.pathExtension
        guard !clean.isEmpty, !clean.hasPrefix("."), clean != item.url.deletingPathExtension().lastPathComponent else { return nil }
        var dest = dir.appendingPathComponent("\(clean).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: dest.path) {
            dest = dir.appendingPathComponent("\(clean) \(n).\(ext)"); n += 1
        }
        // Noted before the move, so the file carries them: a new name can't say what kind of clip it is.
        ClipTags.set(item.url, .kind, item.kind.tag)
        if let game = item.game { ClipTags.set(item.url, .game, game) }
        do {
            try FileManager.default.moveItem(at: item.url, to: dest)
        } catch {
            ClipTags.set(item.url, .kind, nil)
            Toast.error("Couldn't rename the clip", error)
            return nil
        }
        refresh()
        return dest
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
    /// Trim's "Save as": Original (no re-encode) or a new encode, and its quality 0–100. Remembered between uses.
    @Published var exportFormat: ExportFormat = ExportFormat(rawValue: UserDefaults.standard.string(forKey: "exportFormat") ?? "") ?? .original {
        didSet { UserDefaults.standard.set(exportFormat.rawValue, forKey: "exportFormat") }
    }
    @Published var exportQuality: Int = UserDefaults.standard.object(forKey: "exportQuality") as? Int ?? 65 {
        didSet { UserDefaults.standard.set(exportQuality, forKey: "exportQuality") }
    }
    /// 0…1 while converting, nil otherwise.
    @Published private(set) var convertProgress: Double?
    /// Trim's Sound row: the clip's sounds, and which are switched on (see SoundTracks.sources).
    @Published private(set) var sounds: [ClipSound] = []
    @Published private(set) var soundsOn: Set<Int> = []
    private var soundNames: [String] = []
    /// nil keeps the clip's sound as recorded, empty saves none, several are mixed into one.
    var soundChoice: [Int]? { sounds.isEmpty ? nil : SoundTracks.choose(sounds, on: soundsOn) }

    /// Renaming the selected clip: the text being typed.
    @Published private(set) var renaming = false
    @Published var renameText = ""

    func beginRename() {
        guard let item, !isTrimming, !isSavingTrim else { return }
        renameText = item.isRenamed ? item.title : item.displayName
        renaming = true
        OverlayState.shared.capturingKeys = true // Space, arrows and Esc go to the text field
    }

    func endRename(save: Bool) {
        guard renaming else { return }
        renaming = false
        OverlayState.shared.capturingKeys = false
        guard save, let item, let dest = ClipLibrary.shared.rename(item, to: renameText) else { return }
        // Same clip, new name: keep the player where it was.
        let time = player.currentTime(), playing = player.timeControlStatus != .paused
        let date = item.date
        self.item = ClipItem(url: dest, date: date)
        if item.isVideo {
            player.replaceCurrentItem(with: AVPlayerItem(url: dest))
            player.seek(to: time, toleranceBefore: .zero, toleranceAfter: .zero)
            if playing { player.play() }
        }
    }

    func toggleSound(_ track: Int) {
        if soundsOn.contains(track) { soundsOn.remove(track) } else { soundsOn.insert(track) }
    }

    private func loadSounds(_ url: URL) {
        sounds = []
        soundsOn = []
        soundNames = []
        Task {
            let names = await SoundTracks.names(of: url)
            guard self.item?.url == url else { return }
            soundNames = names
            sounds = SoundTracks.sources(names)
            soundsOn = Set(sounds.filter(\.on).map(\.track))
        }
    }

    /// Runs `work` on a hidden copy of `source` with only the chosen sound, removed afterwards.
    fileprivate static func withSound(_ choice: [Int], names: [String], of source: URL,
                                      _ work: (URL) async throws -> Void) async throws {
        let tmp = source.deletingLastPathComponent().appendingPathComponent(".sound-\(UUID().uuidString).\(source.pathExtension)")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try await SoundTracks.rewrite(source, to: tmp, plan: SoundTracks.plan(for: choice, names: names))
        try await work(tmp)
    }
    let player = AVPlayer()
    weak var playerView: AVPlayerView?

    func open(_ item: ClipItem, play: Bool = true) {
        if self.item == item { if play { togglePlay() }; return }
        endRename(save: false)
        isTrimming = false
        self.item = item
        if item.isVideo {
            player.replaceCurrentItem(with: AVPlayerItem(url: item.url))
            if play { player.play() }
        } else {
            player.replaceCurrentItem(with: nil)
        }
    }

    func open(url: URL) {
        let date = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
        open(ClipItem(url: url, date: date))
    }

    func close() {
        endRename(save: false)
        isTrimming = false // a pending trim is ignored once the item changes
        player.pause()
        player.replaceCurrentItem(with: nil)
        item = nil
    }

    /// Loads the newest clip, paused, when nothing is selected or the selection was removed.
    func selectNewestIfNeeded() {
        let items = ClipLibrary.shared.items
        if let item, items.contains(item) { return }
        if let first = items.first { open(first, play: false) } else { close() }
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
        loadSounds(source.url)
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
        let format = exportFormat
        let choice = soundChoice, names = soundNames
        if format != .original { convert(source, range: range, format: format, sound: choice, names: names); return }
        isSavingTrim = true
        let dest = ClipTrimmer.outputURL(for: source.url)
        Task {
            do {
                if let choice {
                    try await Self.withSound(choice, names: names, of: source.url) { try await ClipTrimmer.trim($0, range: range, to: dest) }
                } else {
                    try await ClipTrimmer.trim(source.url, range: range, to: dest)
                }
                ClipTags.copy(from: source.url, to: dest)
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
    /// "Replay … (trimmed).mp4", "(HEVC)", "(trimmed, H.264)", or with " 2" etc. if that exists.
    static func outputURL(for source: URL, label: String = "trimmed") -> URL {
        let dir = source.deletingLastPathComponent()
        let ext = source.pathExtension.isEmpty ? "mp4" : source.pathExtension
        let base = source.deletingPathExtension().lastPathComponent
        var url = dir.appendingPathComponent("\(base) (\(label)).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(base) (\(label) \(n)).\(ext)"); n += 1
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

extension ClipPreview {
    /// Re-encodes the trimmed range (or the whole clip, if the handles weren't moved) into a new file.
    fileprivate func convert(_ source: ClipItem, range: CMTimeRange, format: ExportFormat, sound: [Int]?, names: [String]) {
        isSavingTrim = true
        convertProgress = 0
        let quality = exportQuality
        Task {
            let duration = (try? await AVURLAsset(url: source.url).load(.duration).seconds) ?? range.duration.seconds
            let whole = range.start.seconds < 0.05 && range.end.seconds > duration - 0.05
            let dest = ClipTrimmer.outputURL(for: source.url, label: whole ? format.label : "trimmed, \(format.label)")
            do {
                let run = { (from: URL) in
                    try await ClipConverter.convert(from, range: range, format: format, quality: quality, to: dest) { p in
                        ClipPreview.shared.convertProgress = p
                    }
                }
                if let sound { try await Self.withSound(sound, names: names, of: source.url, run) } else { try await run(source.url) }
                ClipTags.copy(from: source.url, to: dest)
                isSavingTrim = false
                convertProgress = nil
                ClipLibrary.shared.refresh()
                if self.item == source { open(url: dest) }
                let size = { (u: URL) in Int64((try? u.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0) }
                let fmt = { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }
                Toast.show("Saved as \(format.label)",
                           "\(fmt(size(dest))) (was \(fmt(size(source.url)))) · \(dest.lastPathComponent)",
                           icon: "arrow.triangle.2.circlepath", tint: Theme.accent) { OverlayController.shared.show(preview: dest) }
            } catch {
                isSavingTrim = false
                convertProgress = nil
                Toast.error("Couldn't convert the clip", error)
            }
        }
    }
}

// MARK: - Views

/// Right pane: the selected clip (newest by default) in a player, with Trim / Finder / Trash, and the
/// recent clips underneath.
struct ClipPane: View {
    let close: () -> Void
    /// Fixed width (Tiles); nil uses Settings → Player size.
    var fixedWidth: CGFloat? = nil
    /// Screen space the rest of the layout takes beside / above the pane, so the pane always fits.
    var reservedWidth: CGFloat = 0
    var reservedHeight: CGFloat = 0
    var showsClose = true
    @ObservedObject private var sharer = ClipSharer.shared
    @ObservedObject private var lib = ClipLibrary.shared
    @ObservedObject private var preview = ClipPreview.shared
    @ObservedObject private var settings = Settings.shared
    @Environment(\.squareChrome) private var square

    private var screen: CGSize { NSScreen.main?.visibleFrame.size ?? CGSize(width: 1440, height: 900) }

    /// Player width: fixed, or from Settings, kept on screen.
    private var width: CGFloat {
        if let fixedWidth { return fixedWidth }
        let room = screen.width - reservedWidth - Theme.s(90)
        return min(Theme.s(settings.playerSize), max(room, Theme.s(Theme.playerSizeRange.lowerBound)))
    }
    private let gap = Theme.s(7)
    /// Thumbnails keep one size whatever the player size.
    private let thumbW = Theme.s(88)

    var body: some View {
        let item = preview.item
        VStack(alignment: .leading, spacing: Theme.s(10)) {
            HStack(spacing: Theme.s(8)) {
                if let item {
                    Image(systemName: item.icon).foregroundStyle(item.tint).font(Theme.font(13, .semibold))
                    VStack(alignment: .leading, spacing: 0) {
                        ClipTitle(item: item, font: Theme.font(13, .semibold), text: item.title)
                        Text(lib.details(item)).font(Theme.font(10.5)).foregroundStyle(Theme.muted).lineLimit(1)
                    }
                } else {
                    Text("Recent clips").font(Theme.font(13, .semibold))
                }
                Spacer(minLength: Theme.s(8))
                if let item {
                    if item.isVideo {
                        Chip(title: preview.isSavingTrim ? "Saving…" : "Trim", icon: "scissors") { preview.beginTrim() }
                            .disabled(preview.isTrimming || preview.isSavingTrim)
                            .opacity(preview.isTrimming || preview.isSavingTrim ? 0.5 : 1)
                    }
                    Chip(title: sharer.busy == item.url ? "\(Int(sharer.progress * 100))%" : nil, icon: "doc.on.clipboard") { sharer.copy(item.url) }
                        .help("Copy to paste into Discord or anywhere")
                    Chip(title: nil, icon: "pencil") { preview.beginRename() }.help("Rename")
                    Chip(title: nil, icon: "folder") { let url = item.url; close(); reveal(url) }.help("Show in Finder")
                    Chip(title: nil, icon: "trash") { preview.close(); lib.trash(item) }.help("Move to Trash")
                }
                if showsClose { CircleButton(icon: "xmark", action: close).help("Close (Esc)") }
            }

            ZStack {
                Color.black
                if let item {
                    if item.isVideo {
                        PlayerView(player: preview.player)
                    } else if let image = NSImage(contentsOf: item.url) {
                        Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                    }
                } else {
                    VStack(spacing: Theme.s(6)) {
                        Image(systemName: "film.stack").font(.system(size: Theme.s(26))).foregroundStyle(Theme.muted)
                        Text("Clips and screenshots you save show up here.")
                            .font(Theme.font(11.5)).foregroundStyle(Theme.muted)
                    }
                }
            }
            .frame(width: width, height: mediaHeight(item))
            .clipShape(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(12), style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(12), style: .continuous).strokeBorder(Theme.line))

            if item?.isVideo == true, preview.isTrimming || preview.convertProgress != nil {
                SaveAsRow()
            } else {
                HStack {
                    Text(item?.isVideo == true ? "Space to play or pause · ← → to step a frame" : " ")
                        .font(Theme.font(10)).foregroundStyle(Theme.muted).lineLimit(1)
                    Spacer()
                }
            }

            GameFilter().frame(width: width, alignment: .leading)
            if !lib.items.isEmpty {
                let fits = max(1, Int((width + gap) / (thumbW + gap)))
                HStack(spacing: gap) {
                    ForEach(lib.items.prefix(fits)) { ClipThumb(item: $0, width: thumbW, selected: $0 == item) }
                    Spacer(minLength: 0)
                }
                .frame(width: width, alignment: .leading)
            }
        }
        .onAppear(perform: preview.selectNewestIfNeeded)
        .onChange(of: lib.items) { preview.selectNewestIfNeeded() }
    }

    /// Height for the pane's fixed width, following the clip's aspect ratio within a sane range.
    private func mediaHeight(_ item: ClipItem?) -> CGFloat {
        let natural = item.flatMap { lib.sizes[$0.url] } ?? CGSize(width: 16, height: 9)
        let aspect = natural.width > 0 && natural.height > 0 ? natural.width / natural.height : 16 / 9
        // Room left after the rest of the layout, the pane's header, hint line and thumbnails.
        let room = screen.height - reservedHeight - Theme.s(150) - thumbW * 9 / 16
        return max(min(max(width / aspect, width * 9 / 21), width * 3 / 4, room), Theme.s(140))
    }
}

private struct ClipThumb: View {
    let item: ClipItem
    let width: CGFloat
    let selected: Bool
    @Environment(\.squareChrome) private var square
    @ObservedObject private var lib = ClipLibrary.shared
    @State private var hover = false

    var body: some View {
        let height = width * 9 / 16
        let shape = RoundedRectangle(cornerRadius: square ? 0 : Theme.s(7), style: .continuous)
        Button { ClipPreview.shared.open(item) } label: {
            VStack(alignment: .leading, spacing: Theme.s(4)) {
                ZStack {
                    Color.white.opacity(0.06)
                    if let thumb = lib.thumbs[item.url] {
                        Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fill)
                    } else {
                        Image(systemName: item.icon).font(.system(size: Theme.s(14))).foregroundStyle(Theme.muted)
                    }
                    if hover && !selected {
                        Color.black.opacity(0.3)
                        Image(systemName: item.isVideo ? "play.fill" : "eye.fill")
                            .font(.system(size: Theme.s(14), weight: .bold))
                    }
                }
                .frame(width: width, height: height)
                .clipShape(shape)
                .overlay(shape.strokeBorder(selected ? item.tint : (hover ? item.tint.opacity(0.6) : Theme.line),
                                            lineWidth: selected ? Theme.s(1.5) : 1))
                .overlay(alignment: .topLeading) {
                    Image(systemName: item.icon).font(.system(size: Theme.s(7.5), weight: .bold))
                        .foregroundStyle(item.tint)
                        .padding(Theme.s(3))
                        .background(Circle().fill(.black.opacity(0.55)))
                        .padding(Theme.s(3))
                }
                .overlay(alignment: .bottomTrailing) {
                    if let d = lib.durations[item.url] {
                        Text(formatDuration(d)).font(Theme.mono(8, .bold))
                            .padding(.horizontal, Theme.s(3.5)).padding(.vertical, Theme.s(1))
                            .background(Capsule().fill(.black.opacity(0.6)))
                            .padding(Theme.s(3))
                    }
                }
                Text(timeAgo(item.date)).font(Theme.font(9.5)).foregroundStyle(selected ? .white : Theme.muted).lineLimit(1)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { h in withAnimation(.easeOut(duration: 0.12)) { hover = h } }
        .help(item.url.lastPathComponent)
    }
}

/// Under the player while trimming: what to save the trimmed part as, and how hard to compress it.
struct SaveAsRow: View {
    @ObservedObject private var preview = ClipPreview.shared
    @Environment(\.squareChrome) private var square

    var body: some View {
        VStack(alignment: .leading, spacing: Theme.s(6)) {
            if let p = preview.convertProgress {
                HStack(spacing: Theme.s(8)) {
                    ProgressView(value: p).tint(Theme.accent).frame(maxWidth: .infinity)
                    Text("Converting to \(preview.exportFormat.label)… \(Int(p * 100))%")
                        .font(Theme.font(10.5)).foregroundStyle(.secondary).monospacedDigit()
                }
            } else {
                // One line when there's room; in narrow layouts the slider drops to its own line.
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: Theme.s(8)) {
                        formats
                        quality
                        Spacer(minLength: 0)
                    }
                    VStack(alignment: .leading, spacing: Theme.s(6)) {
                        HStack(spacing: Theme.s(8)) { formats; Spacer(minLength: 0) }
                        HStack(spacing: Theme.s(8)) { quality }
                    }
                }
                if !preview.sounds.isEmpty { soundRow }
                Text(preview.exportFormat == .original
                     ? "Drag the yellow handles, then click Trim. Saves without re-encoding; the original is kept."
                     : "Drag the handles (or leave them at the ends for the whole clip), then click Trim. Re-encodes into a new file; the original is kept.")
                    .font(Theme.font(9.5)).foregroundStyle(.secondary).lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var formats: some View {
        HStack(spacing: Theme.s(8)) {
            Text("Save as").font(Theme.font(10.5, .semibold)).foregroundStyle(.secondary).lineLimit(1)
                .frame(width: labelWidth, alignment: .leading)
            HStack(spacing: Theme.s(3)) {
                ForEach(ExportFormat.allCases) { f in formatButton(f) }
            }
        }
    }

    @ViewBuilder private var quality: some View {
        if preview.exportFormat != .original {
            Slider(value: Binding(get: { Double(preview.exportQuality) },
                                  set: { preview.exportQuality = Int($0.rounded()) }), in: 0...100)
                .tint(Theme.accent).controlSize(.small).frame(minWidth: Theme.s(90))
            Text(ClipConverter.word(for: preview.exportQuality))
                .font(Theme.font(10.5, .semibold)).lineLimit(1).fixedSize()
        }
    }

    /// "Save as" and "Sound" are as wide as each other, so the chips after them line up.
    private var labelWidth: CGFloat { Theme.s(44) }

    private var soundRow: some View {
        HStack(alignment: .top, spacing: Theme.s(8)) {
            Text("Sound").font(Theme.font(10.5, .semibold)).foregroundStyle(.secondary).lineLimit(1)
                .frame(width: labelWidth, alignment: .leading).padding(.top, Theme.s(3))
            FlowLayout(spacing: Theme.s(4)) {
                ForEach(preview.sounds) { soundChip($0) }
                Text(soundNote).font(Theme.font(9.5)).foregroundStyle(.secondary).padding(.leading, Theme.s(2)).padding(.vertical, Theme.s(3.5))
            }
        }
    }

    private var soundNote: String {
        switch preview.soundChoice {
        case nil: "as recorded"
        case let c? where c.isEmpty: "no sound"
        case let c? where c.count == 1: "only this"
        default: "mixed when saved"
        }
    }

    private func soundChip(_ s: ClipSound) -> some View {
        let on = preview.soundsOn.contains(s.track)
        let icon = s.isMic ? (on ? "mic.fill" : "mic.slash.fill") : (on ? "speaker.wave.2.fill" : "speaker.slash.fill")
        return Button { preview.toggleSound(s.track) } label: {
            HStack(spacing: Theme.s(4)) {
                Image(systemName: icon).font(.system(size: Theme.s(8.5), weight: .semibold))
                    .foregroundStyle(on ? Theme.accent : Color.secondary)
                Text(s.name).font(Theme.font(10, .semibold)).lineLimit(1).truncationMode(.middle)
                    .strikethrough(!on).foregroundStyle(on ? Color.primary : Color.secondary)
                    .frame(maxWidth: Theme.s(110), alignment: .leading).fixedSize(horizontal: true, vertical: false)
            }
            .padding(.horizontal, Theme.s(7)).padding(.vertical, Theme.s(3))
            .background(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(10)).fill(on ? Theme.accent.opacity(0.22) : Color.primary.opacity(0.05)))
            .overlay(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(10)).strokeBorder(on ? Theme.accent.opacity(0.9) : Color.primary.opacity(0.15)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(on ? "Leave \(s.name) out of the saved clip" : "Put \(s.name) in the saved clip")
    }

    private func formatButton(_ f: ExportFormat) -> some View {
        let on = preview.exportFormat == f
        return Button { preview.exportFormat = f } label: {
            Text(f.label).font(Theme.font(10, .semibold)).lineLimit(1).fixedSize()
                .padding(.horizontal, Theme.s(7)).padding(.vertical, Theme.s(3))
                .background(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(10)).fill(on ? Theme.accent.opacity(0.3) : Color.primary.opacity(0.08)))
                .overlay(RoundedRectangle(cornerRadius: square ? 0 : Theme.s(10)).strokeBorder(on ? Theme.accent : .clear))
        }
        .buttonStyle(.plain)
        .help(f == .original ? "Keep the original video, no re-encoding" : "Re-encode as \(f.label)")
    }
}

struct PlayerView: NSViewRepresentable {
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

/// Lays its views out left to right, wrapping onto new lines when the row is full (the trimmer's sound chips).
struct FlowLayout: Layout {
    var spacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews: subviews)
        let height = rows.last.map { $0.y + $0.height } ?? 0
        let widest = rows.map(\.width).max() ?? 0
        return CGSize(width: proposal.width ?? widest, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for row in arrange(width: bounds.width, subviews: subviews) {
            for (i, x) in row.items {
                subviews[i].place(at: CGPoint(x: bounds.minX + x, y: bounds.minY + row.y), proposal: .unspecified)
            }
        }
    }

    private struct Row { var y: CGFloat; var height: CGFloat = 0; var width: CGFloat = 0; var items: [(Int, CGFloat)] = [] }

    private func arrange(width: CGFloat, subviews: Subviews) -> [Row] {
        var rows = [Row(y: 0)]
        for (i, view) in subviews.enumerated() {
            let size = view.sizeThatFits(.unspecified)
            if rows[rows.count - 1].width > 0, rows[rows.count - 1].width + spacing + size.width > width {
                let last = rows[rows.count - 1]
                rows.append(Row(y: last.y + last.height + spacing))
            }
            let x = rows[rows.count - 1].width == 0 ? 0 : rows[rows.count - 1].width + spacing
            rows[rows.count - 1].items.append((i, x))
            rows[rows.count - 1].width = x + size.width
            rows[rows.count - 1].height = max(rows[rows.count - 1].height, size.height)
        }
        return rows
    }
}

/// "All · Roblox · Minecraft · Other": which game's clips the gallery shows. Only there once clips come from
/// more than one game (or a filter is on).
struct GameFilter: View {
    @ObservedObject private var lib = ClipLibrary.shared
    @Environment(\.squareChrome) private var square

    var body: some View {
        let choices: [(String?, String)] = [(nil, "All")] + lib.games.map { ($0, $0) } + (lib.hasOther ? [(ClipLibrary.other, "Other")] : [])
        if choices.count > 2 || lib.gameFilter != nil {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    ForEach(choices, id: \.1) { value, label in chip(label, value) }
                }
            }
        }
    }

    private func chip(_ label: String, _ value: String?) -> some View {
        let on = lib.gameFilter == value
        return Button { lib.gameFilter = value } label: {
            Text(label).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                .foregroundStyle(on ? Color.primary : Color.secondary)
                .padding(.horizontal, 9).padding(.vertical, 3.5)
                .background(RoundedRectangle(cornerRadius: square ? 0 : 10).fill(on ? Theme.accent.opacity(0.25) : Color.primary.opacity(0.06)))
                .overlay(RoundedRectangle(cornerRadius: square ? 0 : 10).strokeBorder(on ? Theme.accent.opacity(0.9) : .clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(value == nil ? "Clips from every game" : value == ClipLibrary.other ? "Clips not from a game" : "Only \(label) clips")
    }
}

/// A clip's name, or a text field while it's being renamed (Return saves, Esc cancels).
struct ClipTitle: View {
    let item: ClipItem
    let font: Font
    var text: String? = nil
    @ObservedObject private var preview = ClipPreview.shared
    @FocusState private var focused: Bool

    var body: some View {
        if preview.renaming && preview.item == item {
            TextField("Name", text: $preview.renameText)
                .textFieldStyle(.plain).font(font).lineLimit(1)
                .focused($focused)
                .onSubmit { preview.endRename(save: true) }
                .onExitCommand { preview.endRename(save: false) }
                .onAppear { focused = true }
                .padding(.horizontal, 5).padding(.vertical, 1)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.1)))
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Theme.accent.opacity(0.8)))
                .frame(minWidth: 160)
                .help("Return to save, Esc to cancel")
        } else {
            Text(text ?? item.displayName).font(font).lineLimit(1)
        }
    }
}
