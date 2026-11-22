import AppKit
import AVFoundation

/// Copies a clip to the clipboard so it can be pasted into Discord (or anywhere that takes files).
/// Clips over the size limit in Settings → Saving are re-encoded to fit first, into Rewind's cache;
/// the original is never changed.
@MainActor
final class ClipSharer: ObservableObject {
    static let shared = ClipSharer()
    /// The clip being shrunk, and how far along it is (0…1).
    @Published private(set) var busy: URL?
    @Published private(set) var progress: Double = 0

    /// Settings' choices, in MB; 0 is "No limit".
    nonisolated static let presetsMB = [0, 10, 50, 500, 1000]

    nonisolated static func label(mb: Int) -> String {
        if mb == 0 { return "No limit" }
        return mb >= 1000 && mb % 1000 == 0 ? "\(mb / 1000) GB" : "\(mb) MB"
    }

    /// "Copy", or "Fitting… 43%" while that clip is being shrunk.
    func buttonTitle(for url: URL) -> String {
        busy == url ? "Fitting… \(Int(progress * 100))%" : "Copy"
    }

    func copy(_ url: URL) {
        guard busy == nil else { return }
        let mb = Settings.shared.shareLimitMB
        let limit = Int64(mb) * 1_000_000
        let size = Self.fileSize(url)
        let isVideo = ["mp4", "mov", "m4v"].contains(url.pathExtension.lowercased())
        guard limit > 0, size > limit, isVideo else {
            Self.putOnClipboard(url)
            Toast.show("Copied to the clipboard", "\(Self.format(size)) · paste it into Discord or anywhere",
                       icon: "doc.on.clipboard.fill", tint: Theme.accent)
            return
        }
        busy = url
        progress = 0
        Task {
            do {
                let dest = try await Self.fit(url, limit: limit) { p in ClipSharer.shared.progress = p }
                busy = nil
                Self.putOnClipboard(dest)
                Toast.show("Copied to the clipboard",
                           "Shrunk to \(Self.format(Self.fileSize(dest))) (was \(Self.format(size))) to fit \(Self.label(mb: mb)). The original is kept.",
                           icon: "doc.on.clipboard.fill", tint: Theme.accent, duration: 5)
            } catch {
                busy = nil
                Toast.error("Couldn't copy the clip", error)
            }
        }
    }

    // MARK: Fitting

    /// Re-encodes `url` to H.264 (which plays everywhere) at a bitrate that fits `limit` bytes, scaling it down
    /// if that bitrate is too low for its size. Tries once more, lower, if the encoder overshoots.
    nonisolated static func fit(_ url: URL, limit: Int64, progress: @escaping @MainActor (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)
        guard duration.seconds > 0 else { throw RewindError("This clip is empty.") }
        var audio = 0.0
        if let track = try await asset.loadTracks(withMediaType: .audio).first { // only the clip's own sound is kept
            let rate = Double(try await track.load(.estimatedDataRate))
            audio += rate > 0 ? rate : 192_000
        }
        // 4% for the MP4 container, and a little room for the encoder's average drifting.
        let budget = Double(limit) * 8 * 0.92 / duration.seconds
        var video = budget - audio
        guard video >= 150_000 else {
            throw RewindError("It's too long to fit in \(label(mb: Int(limit / 1_000_000))). Trim it first, or raise the limit in Settings → Saving.")
        }

        let dir = try cacheFolder()
        let dest = dir.appendingPathComponent(url.lastPathComponent) // same name, so it shows up nicely where it's pasted
        let range = CMTimeRange(start: .zero, duration: duration)
        for attempt in 0..<2 {
            try? FileManager.default.removeItem(at: dest)
            // Only the clip's own sound: what plays anyway, and the other tracks would only take up room.
            try await ClipConverter.convert(url, range: range, format: .h264, quality: 0, to: dest, bitrate: Int(video), audio: [0]) { p in
                progress(attempt == 0 ? p * 0.95 : 0.95 + p * 0.05)
            }
            let size = fileSize(dest)
            if size <= limit { return dest }
            video *= Double(limit) / Double(size) * 0.9
        }
        throw RewindError("Couldn't get it under \(label(mb: Int(limit / 1_000_000))).")
    }

    /// ~/Library/Caches/<bundle id>/Shared, cleared of files from earlier days.
    nonisolated private static func cacheFolder() throws -> URL {
        let fm = FileManager.default
        let base = try fm.url(for: .cachesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let dir = base.appendingPathComponent(Bundle.main.bundleIdentifier ?? "local.rewind.recorder").appendingPathComponent("Shared")
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let dayAgo = Date().addingTimeInterval(-86_400)
        for f in (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [] {
            if let d = try? f.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, d < dayAgo {
                try? fm.removeItem(at: f)
            }
        }
        return dir
    }

    // MARK: Helpers

    /// The file itself, the way Finder's Copy puts it there, so apps that take files (Discord, Messages, Slack,
    /// browsers) attach it on paste.
    private static func putOnClipboard(_ url: URL) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects([url as NSURL])
        pb.addTypes([NSPasteboard.PasteboardType("NSFilenamesPboardType")], owner: nil)
        pb.setPropertyList([url.path], forType: NSPasteboard.PasteboardType("NSFilenamesPboardType"))
    }

    nonisolated static func fileSize(_ url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
    }

    private static func format(_ bytes: Int64) -> String { ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file) }
}
