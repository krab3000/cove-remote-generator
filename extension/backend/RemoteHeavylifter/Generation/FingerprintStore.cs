using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteHeavylifter.Generation;

public static class FingerprintTypes
{
    public const string Phash = "phash";
}

/// <summary>Where a run stores the fingerprints workers compute; faked in tests.</summary>
public interface IFingerprintStore
{
    Task SavePhashAsync(int fileId, string phash, CancellationToken ct);
}

/// <summary>
/// Upserts a file fingerprint the way Cove's own FileFingerprintWriter does: one row per (file, type), the value
/// replaced in place. Duplicate detection and scrapers then use it exactly like a hash Cove computed itself.
/// </summary>
public sealed partial class FingerprintStore(IExtensionServiceScopeFactory scopes) : IFingerprintStore
{
    public async Task SavePhashAsync(int fileId, string phash, CancellationToken ct)
    {
        // A 64-bit hash as Go's %x prints it: 1-16 lowercase hex digits. Anything else is not a phash Cove can compare.
        if (!PhashFormat().IsMatch(phash))
            throw new InvalidDataException($"not a phash: '{phash}'");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var fingerprints = db.Set<FileFingerprint>();
        // Runs inside a job, not a user request; the row is addressed by its file, so content filters don't apply.
        var existing = await fingerprints.IgnoreQueryFilters()
            .FirstOrDefaultAsync(fp => fp.FileId == fileId && fp.Type == FingerprintTypes.Phash, ct);
        if (existing is not null)
            existing.Value = phash;
        else
            fingerprints.Add(new FileFingerprint { FileId = fileId, Type = FingerprintTypes.Phash, Value = phash });
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex("^[0-9a-f]{1,16}$")]
    private static partial Regex PhashFormat();
}
