import Foundation
import AVFoundation

/// How much room clips take (Settings → Saving), and "Free up space": re-encoding older clips to HEVC at a
/// smaller size. Originals go to the Trash, never deleted outright, so nothing's lost until you empty it.
@MainActor
final class ClipStorage: ObservableObject {
    static let shared = ClipStorage()
    /// Clips older than this many days can be shrunk.
    nonisolated static let shrinkAfterDays = 7

    @Published private(set) var bytes: Int64 = 0
    @Published private(set) var count = 0
    @Published private(set) var measured = false
    /// Video clips Free up space would shrink, and how much room they take now.
    @Published private(set) var candidates: [URL] = []
    @Published private(set) var candidateBytes: Int64 = 0
    /// While shrinking: how far along, and how much room it has freed so far.
    @Published private(set) var progress: (done: Int, total: Int)?
    @Published private(set) var freed: Int64 = 0
    private var stopRequested = false

    var isShrinking: Bool { progress != nil }

    func measure() {
        let dir = Settings.shared.outputDirectory, prefixes = Settings.shared.appPrefixes
        let busy = Controller.shared.router.recorder?.url
        Task {
            let r = await Task.detached(priority: .utility) { Self.scan(dir, prefixes: prefixes, busy: busy) }.value
            bytes = r.bytes
            count = r.count
            candidates = r.candidates
            candidateBytes = r.candidateBytes
            measured = true
        }
    }

    nonisolated static func scan(_ dir: URL, prefixes: [String], busy: URL?, now: Date = Date())
        -> (bytes: Int64, count: Int, candidates: [URL], candidateBytes: Int64) {
        let keys: [URLResourceKey] = [.fileSizeKey, .creationDateKey, .contentModificationDateKey, .isRegularFileKey]
        guard let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: keys,
                                                                       options: [.skipsHiddenFiles]) else { return (0, 0, [], 0) }
        let cutoff = now.addingTimeInterval(-Double(shrinkAfterDays) * 86_400)
        var bytes: Int64 = 0, count = 0, candidateBytes: Int64 = 0
        var candidates: [(URL, Date)] = []
        for url in files {
            let renamed = ClipTags.get(url, .kind) != nil
            guard Cleanup.isRewindFile(url.lastPathComponent, appPrefixes: prefixes) || renamed,
                  let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
            let size = Int64(v.fileSize ?? 0)
            bytes += size
            count += 1
            let date = [v.creationDate, v.contentModificationDate].compactMap { $0 }.max() ?? now
            let video = ["mp4", "mov"].contains(url.pathExtension.lowercased())
            if video, !renamed, date < cutoff, ClipTags.get(url, .shrunk) == nil, url.standardizedFileURL != busy?.standardizedFileURL {
                candidates.append((url, date))
                candidateBytes += size
            }
        }
        return (bytes, count, candidates.sorted { $0.1 < $1.1 }.map(\.0), candidateBytes) // oldest first
    }

    func shrink() {
        guard !isShrinking, !candidates.isEmpty else { return }
        let list = candidates
        stopRequested = false
        freed = 0
        progress = (0, list.count)
        Task {
            var failures = 0
            for (i, url) in list.enumerated() {
                if stopRequested { break }
                do { freed += try await Self.shrinkOne(url) } catch {
                    failures += 1
                    NSLog("Rewind: couldn't shrink \(url.lastPathComponent): \(error.localizedDescription)")
                }
                progress = (i + 1, list.count)
            }
            let size = ByteCountFormatter.string(fromByteCount: freed, countStyle: .file)
            progress = nil
            measure()
            ClipLibrary.shared.refreshIfShown()
            Toast.show(freed > 0 ? "Freed up \(size)" : "Nothing to shrink",
                       freed > 0 ? "The originals are in the Trash: empty it to get the room back."
                                 : failures > 0 ? "\(failures) clip\(failures == 1 ? "" : "s") couldn't be shrunk." : "Those clips are already small.",
                       icon: "externaldrive.fill", tint: Theme.accent, duration: 7)
        }
    }

    func stop() { stopRequested = true }

    /// Re-encodes one clip to HEVC ("smaller file") beside it, and if that's at least a fifth smaller, puts the
    /// original in the Trash and the new file in its place (same name, dates and notes). Returns the bytes saved.
    /// `trash` puts the original in the Trash and returns where it went (replaced in the self-test).
    /// `worthIt`: the new file has to be under this fraction of the old one.
    nonisolated static func shrinkOne(_ url: URL, worthIt: Double = 0.8, trash: (URL) throws -> URL? = { url in
        var to: NSURL?
        try FileManager.default.trashItem(at: url, resultingItemURL: &to)
        return to as URL?
    }) async throws -> Int64 {
        let fm = FileManager.default
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)
        let tmp = url.deletingLastPathComponent().appendingPathComponent(".shrinking-\(UUID().uuidString).\(url.pathExtension)")
        defer { try? fm.removeItem(at: tmp) }
        try await ClipConverter.convert(url, range: CMTimeRange(start: .zero, duration: duration), format: .hevc,
                                        quality: 25, to: tmp) { _ in }
        let before = Int64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        let after = Int64((try? tmp.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        let newLength = try await AVURLAsset(url: tmp).load(.duration).seconds
        guard after > 0, Double(after) < Double(before) * worthIt, abs(newLength - duration.seconds) < 0.5 else {
            ClipTags.set(url, .shrunk, "kept") // not worth it: don't try again
            return 0
        }
        let attrs = try fm.attributesOfItem(atPath: url.path)
        ClipTags.set(tmp, .game, ClipTags.get(url, .game))
        ClipTags.set(tmp, .shrunk, "hevc")
        try fm.setAttributes([.creationDate: attrs[.creationDate] as Any, .modificationDate: attrs[.modificationDate] as Any],
                             ofItemAtPath: tmp.path)
        let trashed = try trash(url)
        do { try fm.moveItem(at: tmp, to: url) } catch {
            if let trashed { try? fm.moveItem(at: trashed, to: url) } // put the original back
            throw error
        }
        return before - after
    }
}
