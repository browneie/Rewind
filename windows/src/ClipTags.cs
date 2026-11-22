namespace Rewind;

/// Rewind's notes on a clip, kept in small NTFS alternate data streams on the file ("Replay ….mp4:rewind.game"),
/// so they stay with it when it's renamed or moved on the same drive: which game it's from, and (for renamed
/// clips) what kind of clip it is. Drives without NTFS (FAT, exFAT) just don't keep them.
static class ClipTags
{
    public const string Game = "rewind.game", Kind = "rewind.kind", Shrunk = "rewind.shrunk";

    public static string? Get(string path, string key)
    {
        try
        {
            var value = File.ReadAllText(path + ":" + key).Trim();
            return value.Length > 0 ? value : null;
        }
        catch { return null; }
    }

    public static void Set(string path, string key, string? value)
    {
        try
        {
            // Writing a stream counts as changing the file; keep its date so cleanup and "4m ago" don't move.
            var written = File.GetLastWriteTimeUtc(path);
            if (string.IsNullOrEmpty(value)) File.Delete(path + ":" + key);
            else File.WriteAllText(path + ":" + key, value);
            File.SetLastWriteTimeUtc(path, written);
        }
        catch (Exception e) { Log.Error($"clip note {key}", e); }
    }

    /// A trimmed or converted copy keeps its source's game.
    public static void Copy(string from, string to) => Set(to, Game, Get(from, Game));
}
