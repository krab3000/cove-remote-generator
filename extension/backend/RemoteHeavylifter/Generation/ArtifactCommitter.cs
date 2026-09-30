namespace RemoteHeavylifter.Generation;

/// <summary>
/// Moves downloaded artifacts into Cove's generated folder the way Cove itself commits them
/// (ThumbnailService.TryCommitGeneratedFile / CommitGeneratedSpriteFiles): a non-empty file only,
/// and the sprite + VTT pair swapped together with a rollback if the second move fails.
/// </summary>
public static class ArtifactCommitter
{
    /// <summary>False when the destination exists and must be kept (no overwrite).</summary>
    public static bool CommitSingle(string downloadedPath, string destinationPath, bool overwrite)
    {
        EnsureNonEmpty(downloadedPath);
        if (!overwrite && File.Exists(destinationPath))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(downloadedPath, destinationPath, overwrite: true);
        return true;
    }

    public static bool CommitSpritePair(string downloadedSprite, string downloadedVtt, string destinationSprite, string destinationVtt, bool overwrite)
    {
        EnsureNonEmpty(downloadedSprite);
        EnsureNonEmpty(downloadedVtt);
        if (!overwrite && File.Exists(destinationSprite) && File.Exists(destinationVtt))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationSprite)!);
        var suffix = $".backup.{Guid.NewGuid():N}";
        var spriteBackup = destinationSprite + suffix;
        var vttBackup = destinationVtt + suffix;
        var spriteExisted = File.Exists(destinationSprite);
        var vttExisted = File.Exists(destinationVtt);
        var replacementStarted = false;
        var cleanupBackups = false;

        try
        {
            if (spriteExisted)
                File.Copy(destinationSprite, spriteBackup);
            if (vttExisted)
                File.Copy(destinationVtt, vttBackup);

            File.Move(downloadedSprite, destinationSprite, overwrite: true);
            replacementStarted = true;
            File.Move(downloadedVtt, destinationVtt, overwrite: true);
            cleanupBackups = true;
            return true;
        }
        catch (Exception commitException)
        {
            if (!replacementStarted)
            {
                cleanupBackups = true;
                throw;
            }

            Exception? rollbackException = null;
            try { Restore(destinationSprite, spriteBackup, spriteExisted); }
            catch (Exception ex) { rollbackException = ex; }
            try { Restore(destinationVtt, vttBackup, vttExisted); }
            catch (Exception ex) { rollbackException = rollbackException is null ? ex : new AggregateException(rollbackException, ex); }

            if (rollbackException is not null)
                throw new AggregateException("Sprite/VTT replacement and rollback both failed.", commitException, rollbackException);
            cleanupBackups = true;
            throw;
        }
        finally
        {
            // After a failed rollback the backups may be the only copies of the previous pair.
            if (cleanupBackups)
            {
                TryDelete(spriteBackup);
                TryDelete(vttBackup);
            }
        }
    }

    private static void EnsureNonEmpty(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException($"Downloaded artifact {Path.GetFileName(path)} is missing or empty.");
    }

    private static void Restore(string destination, string backup, bool existed)
    {
        if (existed)
        {
            if (!File.Exists(backup))
                throw new IOException($"Generated asset backup is missing: {backup}");
            File.Move(backup, destination, overwrite: true);
        }
        else if (File.Exists(destination))
        {
            File.Delete(destination);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a private backup file.
        }
    }
}
