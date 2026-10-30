using System.Text;

namespace CleanRift.Core;

/// <summary>
/// Writes small state files through a temporary file and an atomic replace so a
/// crash never leaves preferences, traces or the cache half-written.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false),
                bufferSize: 16 * 1024, leaveOpen: true);
            writer.Write(content);
        });
    }

    public static void WriteAllBytes(string path, byte[] content)
    {
        Write(path, stream => stream.Write(content, 0, content.Length));
    }

    public static void Write(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Path has no directory", nameof(path));
        Directory.CreateDirectory(directory);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A leftover temporary file is harmless.
            }
        }
    }
}
