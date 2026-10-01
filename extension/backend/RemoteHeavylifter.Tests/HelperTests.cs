using System.Text.Json;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Workers;

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
        Assert.Equal((false, false, true, false), needs);

        existing.Add(Paths.SpriteVtt(1));
        Assert.Null(VideoWorkSelector.Needs(new GenerateRequest(), 1, null, Paths, existing.Contains));
        Assert.Equal((true, true, true, false), VideoWorkSelector.Needs(new GenerateRequest { Overwrite = true }, 1, null, Paths, existing.Contains));
    }

    [Fact]
    public void A_phash_is_only_redone_when_overwriting()
    {
        var request = new GenerateRequest { Cover = false, Preview = false, Sprite = false, Phash = true };
        Assert.Equal((false, false, false, true), VideoWorkSelector.Needs(request, 1, null, Paths, _ => false, hasPhash: false));
        Assert.Null(VideoWorkSelector.Needs(request, 1, null, Paths, _ => false, hasPhash: true));
        Assert.Equal((false, false, false, true),
            VideoWorkSelector.Needs(request with { Overwrite = true }, 1, null, Paths, _ => false, hasPhash: true));
        Assert.True(request.AnyArtifact);
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

public sealed class WorkerRegistryTests
{
    private const string Token = "worker-token-0123456789abcdef";

    private static readonly WorkerDefinition Dialed = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "gpu-box",
        Url = "ws://gpu:8750/rpc",
        Token = Token,
        TokenHash = WorkerTokens.Hash(Token),
    };

    [Fact]
    public void The_connection_direction_follows_from_the_url()
    {
        Assert.Equal(WorkerConnections.CoveDials, Dialed.Connection);
        Assert.Equal(WorkerConnections.WorkerDials, (Dialed with { Url = null }).Connection);
    }

    [Fact]
    public void A_missing_token_keeps_the_stored_one_and_views_mask_it()
    {
        var (workers, errors) = WorkerRegistry.Merge(
            [new WorkerInput(Dialed.Id, "gpu-box", null, "ws://gpu:8750/rpc/", null, true, 4)],
            [Dialed]);
        Assert.Empty(errors);
        Assert.Equal(Token, workers[0].Token);
        Assert.Equal("ws://gpu:8750/rpc", workers[0].Url);
        var view = WorkerView.From(workers[0]);
        Assert.True(view.HasToken);
        Assert.Equal("…cdef", view.TokenHint);
        Assert.Equal(WorkerTokens.Id(Token), view.WorkerTokenId);
        Assert.DoesNotContain("0123456789", JsonSerializer.Serialize(view));
    }

    [Fact]
    public void Without_a_url_only_the_hash_is_kept()
    {
        var (workers, errors) = WorkerRegistry.Merge([new WorkerInput(null, "laptop", Token, null, null)], []);
        Assert.Empty(errors);
        Assert.Null(workers[0].Token);
        Assert.Equal(WorkerTokens.Hash(Token), workers[0].TokenHash);
        Assert.Equal(WorkerConnections.WorkerDials, workers[0].Connection);
    }

    [Fact]
    public void Adding_a_url_to_a_hash_only_worker_needs_the_token_again()
    {
        var trusted = Dialed with { Url = null, Token = null };
        var (_, errors) = WorkerRegistry.Merge([new WorkerInput(trusted.Id, "gpu-box", null, "ws://gpu:8750/rpc", null)], [trusted]);
        Assert.Contains(errors, e => e.Contains("needs the worker token"));
    }

    [Fact]
    public void Invalid_input_is_reported()
    {
        var (_, errors) = WorkerRegistry.Merge(
        [
            new WorkerInput(null, "", null, "http://x", "ftp://cove", true, 0),
            new WorkerInput(null, "a", "short", null, null),
            new WorkerInput(null, "b", Token, null, null),
            new WorkerInput(null, "B", Token + "x", null, null),
            new WorkerInput(null, "c", Token, null, null),
        ], []);
        Assert.Contains(errors, e => e.Contains("name is required"));
        Assert.Contains(errors, e => e.Contains("token is required"));
        Assert.Contains(errors, e => e.Contains("ws:// or wss://"));
        Assert.Contains(errors, e => e.Contains("Cove URL override"));
        Assert.Contains(errors, e => e.Contains("max parallel"));
        Assert.Contains(errors, e => e.Contains("at least 16 characters"));
        Assert.Contains(errors, e => e.Contains("more than one worker"));
        Assert.Contains(errors, e => e.Contains("already uses this token"));
    }
}

public sealed class WorkerTokensTests
{
    [Fact]
    public void Generated_tokens_are_long_and_distinct()
    {
        var a = WorkerTokens.Generate();
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, WorkerTokens.Generate());
    }

    [Fact]
    public void The_id_is_derived_from_the_hash()
    {
        var token = WorkerTokens.Generate();
        Assert.Equal(WorkerTokens.IdLength, WorkerTokens.Id(token).Length);
        Assert.Equal(WorkerTokens.Id(token), WorkerTokens.IdFromHash(WorkerTokens.Hash(token)));
        Assert.Matches("^[A-Z2-7]+$", WorkerTokens.Id(token));
        Assert.True(WorkerTokens.Matches(token, WorkerTokens.Hash(token)));
        Assert.False(WorkerTokens.Matches(token + "x", WorkerTokens.Hash(token)));
    }
}

public sealed class WorkerAccessTests
{
    private const string TokenA = "token-for-worker-a-0123456789";
    private const string TokenB = "token-for-worker-b-0123456789";

    private static async Task<(WorkerAccess Access, WorkerDefinition A, WorkerDefinition B)> CreateAsync(bool bEnabled = true)
    {
        var store = new StoreHolder();
        store.Set(new MemoryStore());
        var registry = new WorkerRegistry(store);
        var (workers, errors) = await registry.ReplaceAsync(
        [
            new WorkerInput(null, "a", TokenA, null, null),
            new WorkerInput(null, "b", TokenB, null, null, bEnabled),
        ]);
        Assert.Empty(errors);
        return (new WorkerAccess(registry), workers[0], workers[1]);
    }

    [Fact]
    public async Task A_worker_may_only_touch_its_own_tasks_and_requested_kinds()
    {
        var (access, a, _) = await CreateAsync();
        access.Register(new TaskAssignment("t1", a.Id, "/media/x.mp4", [ArtifactKinds.Cover], "/tmp/t1"));

        Assert.Equal(AccessDecision.Allowed, access.Authorize(TokenA, "t1", null).Decision);
        Assert.Equal(AccessDecision.Allowed, access.Authorize(TokenA, "t1", ArtifactKinds.Cover).Decision);
        Assert.Equal(AccessDecision.Forbidden, access.Authorize(TokenA, "t1", ArtifactKinds.Preview).Decision);
        Assert.Equal(AccessDecision.Forbidden, access.Authorize(TokenB, "t1", null).Decision);
        Assert.Equal(AccessDecision.Forbidden, access.Authorize(TokenA, "other", null).Decision);
        Assert.Equal(AccessDecision.Unauthorized, access.Authorize(null, "t1", null).Decision);
        Assert.Equal(AccessDecision.Unauthorized, access.Authorize("not-a-worker-token-at-all", "t1", null).Decision);

        access.Remove("t1");
        Assert.Equal(AccessDecision.Forbidden, access.Authorize(TokenA, "t1", null).Decision);
    }

    [Fact]
    public async Task A_disabled_worker_is_not_authorized()
    {
        var (access, _, b) = await CreateAsync(bEnabled: false);
        access.Register(new TaskAssignment("t1", b.Id, "/media/x.mp4", [], "/tmp/t1"));
        Assert.Equal(AccessDecision.Unauthorized, access.Authorize(TokenB, "t1", null).Decision);
    }

    private sealed class MemoryStore : Cove.Plugins.IExtensionStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>(_values));
    }
}

public sealed class ProtocolJsonTests
{
    [Fact]
    public void Task_messages_round_trip_in_camel_case()
    {
        var request = new TaskRequest("t1", 7, "http://cove/source", 123, 60, new Dictionary<string, string> { ["cover"] = "http://cove/a" },
            new CoverSpec(12, null), null, new SpriteSpec(81, 320, null, "7_sprite.jpg"));
        var json = JsonSerializer.Serialize(request, RpcChannel.JsonOptions);
        Assert.Contains("\"sourceUrl\"", json);
        Assert.Contains("\"spriteFilename\":\"7_sprite.jpg\"", json);
        Assert.DoesNotContain("\"preview\"", json);
        Assert.Equal(request.Sprite, JsonSerializer.Deserialize<TaskRequest>(json, RpcChannel.JsonOptions)!.Sprite);
    }
}
