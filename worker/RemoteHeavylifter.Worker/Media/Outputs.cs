using System.Security.Cryptography;

namespace RemoteHeavylifter.Worker.Media;

public static class Outputs
{
    /// <summary>Move a finished output into place; an empty or missing file is a failure (as in Cove).</summary>
    public static void CommitOutput(string tempPath, string finalPath)
    {
        var info = new FileInfo(tempPath);
        if (!info.Exists || info.Length == 0)
            throw new MediaException("ffmpeg produced no output");
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public static void RemoveQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Delete a scratch directory now. Windows may keep a killed ffmpeg's handles open for a moment, so retry
    /// briefly; whatever still remains goes with the task directory or the next start.</summary>
    public static void RemoveDirQuietly(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                    return;
                Thread.Sleep(50 * attempt);
            }
        }
    }

    internal static bool HasContent(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(digest);
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
