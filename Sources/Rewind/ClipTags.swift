import Foundation
import Darwin

/// Rewind's notes on a clip, kept in the file's extended attributes so they stay with it when it's renamed,
/// moved within the Mac or converted: which game it's from, and (for renamed clips) what kind of clip it is.
enum ClipTags {
    enum Key: String {
        /// The game (app) the clip was saved from: "Roblox", "Minecraft".
        case game = "local.rewind.game"
        /// "replay", "recording", "screenshot" or "app": set when a clip is renamed, so Rewind still knows it.
        case kind = "local.rewind.kind"
    }

    static func get(_ url: URL, _ key: Key) -> String? {
        url.withUnsafeFileSystemRepresentation { path -> String? in
            guard let path else { return nil }
            let size = getxattr(path, key.rawValue, nil, 0, 0, 0)
            guard size > 0 else { return nil }
            var data = [UInt8](repeating: 0, count: size)
            guard getxattr(path, key.rawValue, &data, size, 0, 0) == size else { return nil }
            return String(bytes: data, encoding: .utf8)
        }
    }

    static func set(_ url: URL, _ key: Key, _ value: String?) {
        url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return }
            if let value, !value.isEmpty {
                let bytes = Array(value.utf8)
                setxattr(path, key.rawValue, bytes, bytes.count, 0, 0)
            } else {
                removexattr(path, key.rawValue, 0)
            }
        }
    }

    /// A trimmed or converted copy keeps its source's game.
    static func copy(from source: URL, to dest: URL) {
        set(dest, .game, get(source, .game))
    }
}
