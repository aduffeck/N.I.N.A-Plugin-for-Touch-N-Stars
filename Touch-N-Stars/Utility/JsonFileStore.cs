using NINA.Core.Utility;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TouchNStars.Utility;

/// <summary>
/// Writes small JSON files so that a power cut while saving leaves either the old or the new file,
/// and reads them with a fallback to the save before. Same scheme as pins' internal guider stores
/// (StoreFile): a temporary file flushed to disk, then File.Replace keeping the old file as .bkp.
///
/// The callers serialize their own read-modify-write under one lock per file; this class does not lock.
/// </summary>
public static class JsonFileStore
{
    public static string BackupPath(string path) => path + ".bkp";

    /// <summary>
    /// The file at <paramref name="path"/>, else the backup of its last save, else <paramref name="empty"/>.
    /// A file that cannot be parsed is moved aside as *.unreadable-&lt;time&gt;; a backup that was read instead is
    /// written back as the file, so the next read and the next save start from it. A file or backup that cannot
    /// be read (I/O, permissions) throws: its content may be fine, and the saves after an empty or backup read
    /// would replace it.
    /// </summary>
    public static T Read<T>(string path, Func<string, T> parse, Func<T> empty, string what)
    {
        if (File.Exists(path))
        {
            string text = File.ReadAllText(path);
            try
            {
                return parse(text);
            }
            catch (Exception ex)
            {
                MoveAside(path, what, ex);
            }
        }

        var backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            return empty();
        }

        string content = File.ReadAllText(backup);
        T result;
        try
        {
            result = parse(content);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Touch-N-Stars: could not read the backup of the {what} either, starting empty: {ex.Message}");
            return empty();
        }

        Logger.Warning($"Touch-N-Stars: using the {what} of the save before");
        if (!File.Exists(path))
        {
            try
            {
                File.Copy(backup, path);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Touch-N-Stars: could not restore the {what} from its backup: {ex.Message}");
            }
        }

        return result;
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tmp, path, BackupPath(path));
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void MoveAside(string path, string what, Exception reason)
    {
        var aside = $"{path}.unreadable-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        Logger.Warning($"Touch-N-Stars: could not read the {what} ({reason.Message}); kept as {Path.GetFileName(aside)}");
        try
        {
            File.Move(path, aside, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Touch-N-Stars: could not move the unreadable {what} aside: {ex.Message}");
        }
    }
}
