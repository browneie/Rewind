import Foundation

/// Moves old Rewind clips and screenshots to the Trash. Only touches files Rewind named itself
/// ("Replay …", "Recording …", "Screenshot …"), never anything else in the folder.
enum Cleanup {
    static let dayOptions = [1, 3, 7, 14, 30, 60, 90]

    struct Result { var count = 0; var bytes: Int64 = 0 }

    static let builtInPrefixes = ["Replay", "Recording", "Screenshot"]

    /// True for files Rewind saved: "Replay …", "Recording …", "Screenshot …", or "<APP> <date> at <time>…"
    /// for an app name Rewind has used before (see Settings.appPrefixes).
    static func isRewindFile(_ name: String, appPrefixes: [String] = []) -> Bool {
        let ext = (name as NSString).pathExtension.lowercased()
        guard ["mp4", "mov", "png"].contains(ext) else { return false }
        if builtInPrefixes.contains(where: { name.hasPrefix($0 + " ") }) { return true }
        return appPrefixes.contains { p in
            name.hasPrefix(p + " ") && startsWithStamp(name.dropFirst(p.count + 1))
        }
    }

    static func startsWithStamp<S: StringProtocol>(_ s: S) -> Bool {
        s.range(of: #"^\d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}"#, options: .regularExpression) != nil
    }

    /// "ROBLOX" for "ROBLOX 2026-11-13 at 16.53.58.mp4"; nil for built-in names or no date stamp.
    static func appPrefix(of name: String) -> String? {
        guard !builtInPrefixes.contains(where: { name.hasPrefix($0 + " ") }),
              let r = name.range(of: #" \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}"#, options: .regularExpression),
              r.lowerBound > name.startIndex else { return nil }
        return String(name[..<r.lowerBound])
    }

    /// Trashes Rewind files in `dir` saved more than `days` days before `now`, except `keep`.
    static func run(in dir: URL, olderThan days: Int, now: Date = Date(), keep: Set<URL> = [], appPrefixes: [String] = [],
                    remove: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) -> Result {
        let cutoff = now.addingTimeInterval(-Double(days) * 86_400)
        let keys: [URLResourceKey] = [.creationDateKey, .contentModificationDateKey, .fileSizeKey, .isRegularFileKey]
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: keys,
                                                      options: [.skipsHiddenFiles]) else { return Result() }
        let kept = Set(keep.map(\.standardizedFileURL))
        var result = Result()
        for url in files where isRewindFile(url.lastPathComponent, appPrefixes: appPrefixes) && !kept.contains(url.standardizedFileURL) {
            guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
            // Use the later of the two dates, so a clip you edited recently isn't treated as old.
            let date = [v.creationDate, v.contentModificationDate].compactMap { $0 }.max() ?? now
            guard date < cutoff else { continue }
            if (try? remove(url)) != nil {
                result.count += 1
                result.bytes += Int64(v.fileSize ?? 0)
            }
        }
        return result
    }
}
