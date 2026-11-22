using System.Text.Json;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

/// <summary>Reads the answer of <c>releases/latest</c>.</summary>
internal static class ReleaseParser
{
    /// <summary>
    /// The release described by the answer; null when it is not one to offer (a draft, a pre-release, a tag that is no version).
    /// Throws <see cref="UpdateFailure"/> when the answer is not a release at all.
    /// </summary>
    public static CachedRelease? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            throw new UpdateFailure(UpdateText.UnreadableAnswer, "not JSON");
        }
    }

    private static CachedRelease? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new UpdateFailure(UpdateText.UnreadableAnswer, "not an object");

        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

        var tag = JsonText.Str(root, "tag_name");
        if (UpdateVersion.Clean(tag) is not { } version)
        {
            Log.Warn("Update check: the latest release has a tag that is not a version");
            return null;
        }

        var pageUrl = Address(JsonText.Str(root, "html_url")) ?? $"https://github.com/{UpdateService.Repository}/releases/tag/{Uri.EscapeDataString(tag!)}";

        string? assetUrl = null, digest = null, sumsUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = JsonText.Str(asset, "name");
                if (string.Equals(name, UpdateService.AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = Address(JsonText.Str(asset, "browser_download_url"));
                    digest = UpdateHashes.ParseDigest(JsonText.Str(asset, "digest"));
                    size = asset.TryGetProperty("size", out var sizeNode) && sizeNode.ValueKind == JsonValueKind.Number && sizeNode.TryGetInt64(out var bytes) && bytes is > 0 and <= UpdateService.MaxBytes ? bytes : 0;
                }
                else if (string.Equals(name, UpdateService.SumsName, StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = Address(JsonText.Str(asset, "browser_download_url"));
                }
            }
        }

        var notes = UpdateText.Truncate(JsonText.Str(root, "body"), UpdateService.MaxNotesChars);
        return new CachedRelease(version, tag!, notes, pageUrl, assetUrl, size, digest, sumsUrl);
    }

    // An address the app may follow; anything else is as good as none.
    internal static string? Address(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && UpdateHosts.IsAllowed(uri) ? uri.AbsoluteUri : null;

    private static bool Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
