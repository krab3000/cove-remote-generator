using System.Security.Cryptography;

namespace RemoteHeavylifter.Generation;

/// <summary>
/// Where Cove expects a video's generated files. A replica of Cove's private path helpers
/// (ThumbnailService.GetThumbnailPath / GetPreviewPath / GetSpritePath / GetSpriteVttPath): the
/// sub-folder is the first two hex digits of SHA-256 over the id's 4 little-endian bytes.
/// Pinned by golden values in contract/parity/cove-parity.json.
/// </summary>
public sealed class GeneratedPaths(string root)
{
    public string Root { get; } = root;

    public static string Bucket(int videoId)
        => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)))[..2];

    public string Cover(int videoId) => Path.Combine(Root, "screenshots", Bucket(videoId), $"{videoId}.jpg");

    public string Preview(int videoId) => Path.Combine(Root, "previews", Bucket(videoId), $"{videoId}.mp4");

    public string Sprite(int videoId) => Path.Combine(Root, "vtt", Bucket(videoId), SpriteFileName(videoId));

    public string SpriteVtt(int videoId) => Path.Combine(Root, "vtt", Bucket(videoId), $"{videoId}_thumbs.vtt");

    /// <summary>The sprite's file name; the VTT cues reference it by this name.</summary>
    public static string SpriteFileName(int videoId) => $"{videoId}_sprite.jpg";

    /// <summary>Scratch space on the same volume as the destinations, so commits are plain renames.</summary>
    public string TempRoot => Path.Combine(Root, "tmp", "remote-heavylifter");
}
