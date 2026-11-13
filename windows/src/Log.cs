namespace Rewind;

static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind", "rewind.log");

    public static void Info(string message) => Write("info", message);
    public static void Error(string context, Exception e) => Write("error", $"{context}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break capture */ }
    }
}
