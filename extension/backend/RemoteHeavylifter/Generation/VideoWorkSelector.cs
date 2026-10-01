using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Generation;

/// <summary>
/// Picks the videos a run must generate, mirroring Cove's own generate job: the primary file only, the
/// selective-generate folder filter, custom covers left alone, and existing files kept unless overwriting.
/// </summary>
public sealed class VideoWorkSelector(DbContext db)
{
    private const int PageSize = 500;

    /// <summary>Which of the requested artifacts a video still needs; null when it needs none.</summary>
    public static (bool Cover, bool Preview, bool Sprite)? Needs(
        GenerateRequest request,
        int videoId,
        string? imageBlobId,
        GeneratedPaths paths,
        Func<string, bool> exists)
    {
        var cover = request.Cover && string.IsNullOrWhiteSpace(imageBlobId)
            && (request.Overwrite || !exists(paths.Cover(videoId)));
        var preview = request.Preview && (request.Overwrite || !exists(paths.Preview(videoId)));
        var sprite = request.Sprite
            && (request.Overwrite || !(exists(paths.Sprite(videoId)) && exists(paths.SpriteVtt(videoId))));
        return cover || preview || sprite ? (cover, preview, sprite) : null;
    }

    public async Task<SelectionResult> SelectAsync(
        GenerateRequest request,
        GeneratedPaths paths,
        IReadOnlyList<ServerDefinition> servers,
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

            foreach (var video in page)
            {
                if (video.PrimaryFileId is not { } fileId || !files.TryGetValue(fileId, out var file))
                    continue;
                if (!PathFilter.Contains(file.Path, filters))
                    continue;

                examined++;
                if (Needs(request, video.Id, video.ImageBlobId, paths, File.Exists) is not { } needs)
                    continue;
                var (cover, preview, sprite) = needs;

                var label = string.IsNullOrWhiteSpace(video.Title) ? file.Basename : video.Title!;
                if (!File.Exists(FilesystemPaths.ToNativePath(file.Path)))
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
                // A zero duration is fine: the server probes it itself.
                var remotePaths = new Dictionary<Guid, string>();
                foreach (var server in servers)
                {
                    if (PathMapper.Map(file.Path, server.Mappings) is { } remote)
                        remotePaths[server.Id] = remote;
                }
                if (remotePaths.Count == 0)
                {
                    settled.Add(new SettledItem(video.Id, label, JobUnitOutcome.Failed,
                        $"No path mapping on the selected servers covers {file.Path}"));
                    continue;
                }

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
                    CoverFilter = VrFilter.OneEyeFlat(vr, 1920),
                    PreviewScale = VrFilter.OneEyeFlat(vr, PreviewSettings.Width) ?? $"scale={PreviewSettings.Width}:-2",
                    SpriteFilter = VrFilter.OneEyeFlat(vr, spriteWidth),
                    SpriteWidth = spriteWidth,
                    RemotePaths = remotePaths,
                });
            }
        }

        return new SelectionResult(work, settled, examined);
    }

    /// <summary>A few primary-file paths under <paramref name="covePrefix"/>, for testing a mapping.</summary>
    public async Task<IReadOnlyList<string>> SamplePathsAsync(string covePrefix, int count, CancellationToken ct)
    {
        var prefix = PathMapper.Normalize(covePrefix);
        var like = prefix + "/";
        var paths = await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.VideoId != null && file.Path.StartsWith(like))
            .OrderBy(file => file.Id)
            .Select(file => file.Path)
            .Take(count)
            .ToListAsync(ct);
        if (paths.Count == 0)
        {
            var lower = like.ToLowerInvariant();
            paths = await db.Set<VideoFile>()
                .AsNoTracking()
                .Where(file => file.VideoId != null && file.Path.ToLower().StartsWith(lower))
                .OrderBy(file => file.Id)
                .Select(file => file.Path)
                .Take(count)
                .ToListAsync(ct);
        }
        return paths;
    }
}
