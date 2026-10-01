using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RemoteHeavylifter.Generation;

/// <summary>
/// Picks the videos a run must generate, mirroring Cove's own generate job: the primary file only, the
/// selective-generate folder filter, custom covers left alone, and existing files kept unless overwriting.
/// </summary>
public sealed class VideoWorkSelector(DbContext db)
{
    private const int PageSize = 500;

    /// <summary>Which of the requested artifacts a video still needs; null when it needs none.</summary>
    /// <param name="hasPhash">Any of the video's files already has a phash (Cove's own rule for "has one").</param>
    public static (bool Cover, bool Preview, bool Sprite, bool Phash)? Needs(
        GenerateRequest request,
        int videoId,
        string? imageBlobId,
        GeneratedPaths paths,
        Func<string, bool> exists,
        bool hasPhash = false)
    {
        var cover = request.Cover && string.IsNullOrWhiteSpace(imageBlobId)
            && (request.Overwrite || !exists(paths.Cover(videoId)));
        var preview = request.Preview && (request.Overwrite || !exists(paths.Preview(videoId)));
        var sprite = request.Sprite
            && (request.Overwrite || !(exists(paths.Sprite(videoId)) && exists(paths.SpriteVtt(videoId))));
        var phash = request.Phash && (request.Overwrite || !hasPhash);
        return cover || preview || sprite || phash ? (cover, preview, sprite, phash) : null;
    }

    public async Task<SelectionResult> SelectAsync(
        GenerateRequest request,
        GeneratedPaths paths,
        CancellationToken ct)
    {
        var filters = PathFilter.Normalize(request.Paths);
        // The task-list job replays stored options, so an out-of-range width falls back instead of failing every video.
        var spriteWidth = SpriteSettings.IsValidWidth(request.SpriteWidth) ? request.SpriteWidth : SpriteSettings.DefaultWidth;
        var work = new List<WorkItem>();
        var settled = new List<SettledItem>();
        var examined = 0;
        int? afterId = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.Set<Video>()
                .AsNoTracking()
                .Where(video => afterId == null || video.Id > afterId)
                .OrderBy(video => video.Id)
                .Take(PageSize)
                .Select(video => new
                {
                    video.Id,
                    video.Title,
                    video.PrimaryFileId,
                    video.ImageBlobId,
                    video.IsVr,
                })
                .ToListAsync(ct);
            if (page.Count == 0)
                break;
            afterId = page[^1].Id;

            // VR layouts only exist on newer Cove builds; load those few videos whole and read them late-bound.
            var vrVideos = new Dictionary<int, Video>();
            if (VrSupport.Available && page.Any(v => v.IsVr))
            {
                var vrIds = page.Where(v => v.IsVr).Select(v => v.Id).ToList();
                vrVideos = await db.Set<Video>().AsNoTracking().Where(v => vrIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
            }

            var primaryIds = page.Where(v => v.PrimaryFileId != null).Select(v => v.PrimaryFileId!.Value).ToList();
            var files = await db.Set<VideoFile>()
                .AsNoTracking()
                .Where(file => primaryIds.Contains(file.Id))
                .Select(file => new
                {
                    file.Id,
                    file.Path,
                    file.Basename,
                    file.Duration,
                    file.Width,
                    file.Height,
                    file.Size,
                    file.SourceUnreadableAt,
                    file.SourceUnreadableReason,
                    file.SourceUnreadableSize,
                })
                .ToDictionaryAsync(file => file.Id, ct);

            // Cove counts a video as hashed when any of its files has a phash, not just the primary one.
            var hashed = new HashSet<int>();
            if (request.Phash)
            {
                var pageIds = page.Select(v => v.Id).ToList();
                hashed = (await db.Set<VideoFile>()
                        .AsNoTracking()
                        .Where(file => file.VideoId != null && pageIds.Contains(file.VideoId.Value))
                        .Where(file => db.Set<FileFingerprint>().Any(fp => fp.FileId == file.Id && fp.Type == FingerprintTypes.Phash && fp.Value != ""))
                        .Select(file => file.VideoId!.Value)
                        .Distinct()
                        .ToListAsync(ct))
                    .ToHashSet();
            }

            foreach (var video in page)
            {
                if (video.PrimaryFileId is not { } fileId || !files.TryGetValue(fileId, out var file))
                    continue;
                if (!PathFilter.Contains(file.Path, filters))
                    continue;

                examined++;
                if (Needs(request, video.Id, video.ImageBlobId, paths, File.Exists, hashed.Contains(video.Id)) is not { } needs)
                    continue;
                var (cover, preview, sprite, phash) = needs;

                var label = string.IsNullOrWhiteSpace(video.Title) ? file.Basename : video.Title!;
                var sourcePath = FilesystemPaths.ToNativePath(file.Path);
                if (!File.Exists(sourcePath))
                {
                    settled.Add(new SettledItem(video.Id, label, JobUnitOutcome.Skipped, "Source file is unavailable"));
                    continue;
                }
                if (file.SourceUnreadableAt.HasValue && file.SourceUnreadableSize == file.Size)
                {
                    settled.Add(new SettledItem(video.Id, label, JobUnitOutcome.Failed,
                        file.SourceUnreadableReason ?? "Source file is unreadable"));
                    continue;
                }
                // A zero duration is fine: the worker probes it itself.
                var vr = vrVideos.TryGetValue(video.Id, out var full)
                    ? VrSupport.Layout(full, file.Path, file.Width, file.Height)
                    : null;

                work.Add(new WorkItem
                {
                    VideoId = video.Id,
                    Label = label,
                    CovePath = file.Path,
                    Duration = file.Duration,
                    Cover = cover,
                    Preview = preview,
                    Sprite = sprite,
                    Phash = phash,
                    CoverFilter = VrFilter.OneEyeFlat(vr, 1920),
                    PreviewScale = VrFilter.OneEyeFlat(vr, PreviewSettings.Width) ?? $"scale={PreviewSettings.Width}:-2",
                    SpriteFilter = VrFilter.OneEyeFlat(vr, spriteWidth),
                    SpriteWidth = spriteWidth,
                    SourcePath = sourcePath,
                    FileId = file.Id,
                    SourceSize = file.Size,
                });
            }
        }

        return new SelectionResult(work, settled, examined);
    }

    /// <summary>One readable primary video file, for testing that a worker can read through Cove.</summary>
    public async Task<(string Path, long Size)?> SampleSourceAsync(CancellationToken ct)
    {
        var candidates = await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.VideoId != null)
            .OrderBy(file => file.Size)
            .Select(file => new { file.Path, file.Size })
            .Take(20)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var path = FilesystemPaths.ToNativePath(candidate.Path);
            if (File.Exists(path))
                return (path, candidate.Size);
        }
        return null;
    }
}
