using System.Text.Json;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Tests;

public sealed class GeneratedPathsTests
{
    [Fact]
    public void Buckets_match_the_cove_golden_values()
    {
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "parity", "cove-parity.json")));
        foreach (var entry in parity.RootElement.GetProperty("generatedPaths").EnumerateArray())
        {
            var id = entry.GetProperty("videoId").GetInt32();
            Assert.Equal(entry.GetProperty("bucket").GetString(), GeneratedPaths.Bucket(id));
        }
    }

    [Fact]
    public void Paths_follow_the_cove_layout()
    {
        var paths = new GeneratedPaths(Path.Combine("g"));
        Assert.Equal(Path.Combine("g", "screenshots", "e8", "42.jpg"), paths.Cover(42));
        Assert.Equal(Path.Combine("g", "previews", "e8", "42.mp4"), paths.Preview(42));
        Assert.Equal(Path.Combine("g", "vtt", "e8", "42_sprite.jpg"), paths.Sprite(42));
        Assert.Equal(Path.Combine("g", "vtt", "e8", "42_thumbs.vtt"), paths.SpriteVtt(42));
        Assert.Equal(Path.Combine("g", "tmp", "remote-heavylifter"), paths.TempRoot);
    }
}

public sealed class PathMapperTests
{
    private static readonly PathMapping[] Mappings =
    [
        new("D:\\media", "/mnt/media"),
        new("D:/media/Movies", "/mnt/movies/"),
        new("/srv/library", "\\\\nas\\library"),
    ];

    [Theory]
    [InlineData("D:/media/Shows/A.mkv", "/mnt/media/Shows/A.mkv")]
    [InlineData("d:/MEDIA/Shows/Case.MKV", "/mnt/media/Shows/Case.MKV")]
    [InlineData("D:/media/Movies/B.mp4", "/mnt/movies/B.mp4")]
    [InlineData("D:\\media\\Movies\\sub\\C.mp4", "/mnt/movies/sub/C.mp4")]
    [InlineData("/srv/library/x/y.mp4", "\\\\nas\\library\\x\\y.mp4")]
    [InlineData("D:/media2/Z.mp4", null)]
    [InlineData("E:/other/Z.mp4", null)]
    public void Maps_through_the_longest_whole_segment_prefix(string covePath, string? expected)
        => Assert.Equal(expected, PathMapper.Map(covePath, Mappings));

    [Fact]
    public void A_root_prefix_maps_everything()
        => Assert.Equal("/mnt/x/a.mp4", PathMapper.Map("/x/a.mp4", [new PathMapping("/", "/mnt")]));
}

public sealed class PathFilterTests
{
    [Fact]
    public void Matches_like_coves_generate_filter()
    {
        var filters = PathFilter.Normalize(["D:\\media\\Movies\\", " ", "d:/media/movies"]);
        Assert.Equal(["D:/media/Movies"], filters);
        Assert.True(PathFilter.Contains("D:/media/Movies/a.mp4", filters));
        Assert.True(PathFilter.Contains("d:/MEDIA/movies", filters));
        Assert.False(PathFilter.Contains("D:/media/Movies2/a.mp4", filters));
        Assert.True(PathFilter.Contains("anything", []));
    }
}

public sealed class SelectionPredicateTests
{
    private static readonly GeneratedPaths Paths = new("g");

    [Fact]
    public void Custom_cover_is_left_alone()
    {
        var needs = VideoWorkSelector.Needs(new GenerateRequest { Preview = false, Sprite = false }, 1, "blob-1", Paths, _ => false);
        Assert.Null(needs);
    }

    [Fact]
    public void Existing_files_are_kept_unless_overwriting()
    {
        var existing = new HashSet<string> { Paths.Cover(1), Paths.Preview(1), Paths.Sprite(1) };
        var needs = VideoWorkSelector.Needs(new GenerateRequest(), 1, null, Paths, existing.Contains);
        // The sprite only counts as present with its VTT.
        Assert.Equal((false, false, true), needs);

        existing.Add(Paths.SpriteVtt(1));
        Assert.Null(VideoWorkSelector.Needs(new GenerateRequest(), 1, null, Paths, existing.Contains));
        Assert.Equal((true, true, true), VideoWorkSelector.Needs(new GenerateRequest { Overwrite = true }, 1, null, Paths, existing.Contains));
    }
}

public sealed class VrFilterTests
{
    [Fact]
    public void Flat_videos_get_no_filter()
    {
        Assert.Null(VrFilter.OneEyeFlat(null, 640));
        Assert.Null(VrFilter.OneEyeFlat(new VrLayout("Flat", 0, "Mono"), 640));
    }

    [Fact]
    public void Vr_videos_are_reprojected_to_one_flat_eye()
    {
        Assert.Equal(
            "v360=input=he:output=flat:in_stereo=sbs:out_stereo=2d:h_fov=100:v_fov=67.67:w=1920:h=1080",
            VrFilter.OneEyeFlat(new VrLayout("Equirectangular", 180, "SideBySide"), 1920));
        Assert.Equal(
            "v360=input=e:output=flat:in_stereo=tb:out_stereo=2d:h_fov=100:v_fov=67.67:w=160:h=90",
            VrFilter.OneEyeFlat(new VrLayout("Equirectangular", 360, "TopBottom"), 160));
        Assert.StartsWith(
            "v360=input=fisheye:ih_fov=200:iv_fov=200:output=flat:in_stereo=sbs",
            VrFilter.OneEyeFlat(new VrLayout("Mkx200", 200, "SideBySide"), 640));
        Assert.Equal(
            "crop=iw/2:ih:0:0,scale=640:360,setsar=1",
            VrFilter.OneEyeFlat(new VrLayout("Flat", 0, "SideBySide"), 640));
    }

    [Fact]
    public void Vr_metadata_is_only_read_on_hosts_that_have_it()
    {
        var video = new Cove.Core.Entities.Video { IsVr = true };
        var layout = VrSupport.Layout(video, "/m/a_180_sbs.mp4", 3840, 1920);
        if (VrSupport.Available)
            Assert.NotNull(layout);
        else
            Assert.Null(layout);
    }
}

public sealed class ArtifactCommitterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rh-commit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Single_commit_respects_overwrite()
    {
        var destination = Path.Combine(_dir, "out", "ab", "1.jpg");
        Assert.True(ArtifactCommitter.CommitSingle(Write("a", "new"), destination, overwrite: false));
        Assert.False(ArtifactCommitter.CommitSingle(Write("b", "newer"), destination, overwrite: false));
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.True(ArtifactCommitter.CommitSingle(Write("c", "newest"), destination, overwrite: true));
        Assert.Equal("newest", File.ReadAllText(destination));
        Assert.Throws<InvalidDataException>(() => ArtifactCommitter.CommitSingle(Write("d", ""), destination, true));
    }

    [Fact]
    public void Sprite_pair_is_replaced_together()
    {
        var sprite = Path.Combine(_dir, "vtt", "1_sprite.jpg");
        var vtt = Path.Combine(_dir, "vtt", "1_thumbs.vtt");
        Assert.True(ArtifactCommitter.CommitSpritePair(Write("s1", "S1"), Write("v1", "V1"), sprite, vtt, overwrite: false));
        Assert.False(ArtifactCommitter.CommitSpritePair(Write("s2", "S2"), Write("v2", "V2"), sprite, vtt, overwrite: false));
        Assert.True(ArtifactCommitter.CommitSpritePair(Write("s3", "S3"), Write("v3", "V3"), sprite, vtt, overwrite: true));
        Assert.Equal(("S3", "V3"), (File.ReadAllText(sprite), File.ReadAllText(vtt)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(sprite)!, "*.backup.*"));
    }

    [Fact]
    public void A_failed_vtt_move_rolls_the_sprite_back()
    {
        var sprite = Path.Combine(_dir, "vtt", "2_sprite.jpg");
        var vtt = Path.Combine(_dir, "vtt", "2_thumbs.vtt");
        ArtifactCommitter.CommitSpritePair(Write("s1", "OLD-S"), Write("v1", "OLD-V"), sprite, vtt, overwrite: false);

        var newVtt = Write("v2", "NEW-V");
        using (File.Open(vtt, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                return; // POSIX renames succeed over an open file; the lock only blocks the move on Windows.
            Assert.ThrowsAny<IOException>(() => ArtifactCommitter.CommitSpritePair(Write("s2", "NEW-S"), newVtt, sprite, vtt, overwrite: true));
        }
        Assert.Equal(("OLD-S", "OLD-V"), (File.ReadAllText(sprite), File.ReadAllText(vtt)));
    }
}

public sealed class ServerRegistryTests
{
    private static readonly ServerDefinition Stored = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "gpu-box",
        BaseUrl = "http://gpu:8750",
        ApiKey = "super-secret-key",
    };

    [Fact]
    public void A_missing_key_keeps_the_stored_one_and_views_mask_it()
    {
        var (servers, errors) = ServerRegistry.Merge(
            [new ServerInput(Stored.Id, "gpu-box", "http://gpu:8750/", null, true, 4, [new PathMapping("D:/media", "/mnt/media")])],
            [Stored]);
        Assert.Empty(errors);
        Assert.Equal("super-secret-key", servers[0].ApiKey);
        Assert.Equal("http://gpu:8750", servers[0].BaseUrl);
        var view = ServerView.From(servers[0]);
        Assert.True(view.HasApiKey);
        Assert.Equal("…-key", view.ApiKeyHint);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(view));
    }

    [Fact]
    public void Invalid_input_is_reported()
    {
        var (_, errors) = ServerRegistry.Merge(
        [
            new ServerInput(null, "", "ftp://x", "", true, 0, [new PathMapping("", "/x")]),
            new ServerInput(null, "a", "http://a", "k", true, 1),
            new ServerInput(null, "A", "http://b", "k", true, 1),
        ], []);
        Assert.Contains(errors, e => e.Contains("name is required"));
        Assert.Contains(errors, e => e.Contains("http://"));
        Assert.Contains(errors, e => e.Contains("API key"));
        Assert.Contains(errors, e => e.Contains("max concurrency"));
        Assert.Contains(errors, e => e.Contains("path mapping"));
        Assert.Contains(errors, e => e.Contains("more than one server"));
    }
}

public sealed class ContractTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "fixtures", name));

    [Fact]
    public void Fixtures_round_trip()
    {
        var status = JsonSerializer.Deserialize<RemoteTaskStatus>(Fixture("task_status.json"), RemoteJson.Options)!;
        Assert.Equal("partial", status.Status);
        Assert.True(status.IsTerminal);
        Assert.True(status.Artifacts["cover"].Succeeded);
        Assert.Equal("preview: Invalid data found when processing input", status.Artifacts["preview"].Error);

        var info = JsonSerializer.Deserialize<RemoteInfo>(Fixture("info.json"), RemoteJson.Options)!;
        Assert.Equal(RemoteJson.ApiVersion, info.ApiVersion);
        Assert.Equal(4, info.Capacity);

        var request = JsonSerializer.Deserialize<RemoteTaskRequest>(Fixture("task_request.json"), RemoteJson.Options)!;
        var json = JsonSerializer.Serialize(request, RemoteJson.Options);
        using var expected = JsonDocument.Parse(Fixture("task_request.json"));
        using var actual = JsonDocument.Parse(json);
        foreach (var property in expected.RootElement.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null))
            Assert.True(actual.RootElement.TryGetProperty(property.Name, out _), $"missing {property.Name}");
        Assert.Equal("123_sprite.jpg", actual.RootElement.GetProperty("sprite").GetProperty("sprite_filename").GetString());
    }
}
