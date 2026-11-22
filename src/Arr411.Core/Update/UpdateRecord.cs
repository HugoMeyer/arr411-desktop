using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arr411.Core.Update;

/// <summary>What the check keeps of a release: enough to show it and to download it without asking GitHub again.</summary>
internal sealed record CachedRelease(string Version, string Tag, string Notes, string PageUrl, string? AssetUrl, long Size, string? Digest, string? SumsUrl);

/// <summary>An update that was downloaded and verified: where it is, and the hash it had then.</summary>
internal sealed record StagedUpdate(string Version, string File, string Sha256, long Size);

/// <summary>A swap that was started: the next start tells from the version it runs whether it worked.</summary>
internal sealed record ApplyAttempt(string Version, string Target);

/// <summary>
/// What the updater remembers between two runs (<c>updates\state.json</c>, next to the staged file), kept out of the settings which the web app
/// edits. Anything missing or unreadable reads as nothing known.
/// </summary>
internal sealed class UpdateRecord
{
    /// <summary>The version that wrote it: what is cached about a release only holds for that one.</summary>
    public string? App { get; set; }

    public string? Etag { get; set; }

    /// <summary>The release <see cref="Etag"/> is about.</summary>
    public CachedRelease? Release { get; set; }

    public long? CheckedAt { get; set; }

    public StagedUpdate? Staged { get; set; }

    /// <summary>A version whose swap did not work: it is not applied by itself again.</summary>
    public string? Failed { get; set; }

    public ApplyAttempt? Applying { get; set; }

    /// <summary>What the user was told by a notification already ("available:0.3.0"), so that it is told once.</summary>
    public string? Notified { get; set; }

    public static UpdateRecord Parse(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject root) return new UpdateRecord();

            // The file is the user's to edit: every value is checked as if it came from the network.
            var record = new UpdateRecord
            {
                App = Version(root, "app"),
                Etag = Short(Text(root, "etag")),
                CheckedAt = Number(root, "checkedAt"),
                Failed = Version(root, "failed"),
                Notified = Short(Text(root, "notified")),
            };
            if (root["release"] is JsonObject release && Version(release, "version") is { } version && Short(Text(release, "tag")) is { } tag)
            {
                var size = Number(release, "size") ?? 0;
                record.Release = new CachedRelease(
                    version,
                    tag,
                    UpdateText.Truncate(Text(release, "notes"), UpdateService.MaxNotesChars),
                    ReleaseParser.Address(Text(release, "url")) ?? "",
                    ReleaseParser.Address(Text(release, "assetUrl")),
                    size is > 0 and <= UpdateService.MaxBytes ? size : 0,
                    Text(release, "digest") is { } digest && UpdateHashes.IsSha256(digest) ? digest.ToLowerInvariant() : null,
                    ReleaseParser.Address(Text(release, "sumsUrl")));
            }

            if (root["staged"] is JsonObject staged
                && Version(staged, "version") is { } stagedVersion
                && Text(staged, "file") == UpdateFiles.StagedName(stagedVersion)
                && Text(staged, "sha256") is { } sha
                && UpdateHashes.IsSha256(sha)
                && Number(staged, "size") is { } stagedSize and > 0 and <= UpdateService.MaxBytes)
            {
                record.Staged = new StagedUpdate(stagedVersion, UpdateFiles.StagedName(stagedVersion), sha.ToLowerInvariant(), stagedSize);
            }

            if (root["applying"] is JsonObject applying && Version(applying, "version") is { } applyVersion && Text(applying, "target") is { } target)
            {
                record.Applying = new ApplyAttempt(applyVersion, target);
            }

            return record;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return new UpdateRecord();
        }
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["app"] = App,
            ["etag"] = Etag,
            ["checkedAt"] = CheckedAt,
            ["failed"] = Failed,
            ["notified"] = Notified,
        };
        if (Release is { } release)
        {
            root["release"] = new JsonObject
            {
                ["version"] = release.Version,
                ["tag"] = release.Tag,
                ["notes"] = release.Notes,
                ["url"] = release.PageUrl,
                ["assetUrl"] = release.AssetUrl,
                ["size"] = release.Size,
                ["digest"] = release.Digest,
                ["sumsUrl"] = release.SumsUrl,
            };
        }

        if (Staged is { } staged)
        {
            root["staged"] = new JsonObject
            {
                ["version"] = staged.Version,
                ["file"] = staged.File,
                ["sha256"] = staged.Sha256,
                ["size"] = staged.Size,
            };
        }

        if (Applying is { } applying)
        {
            root["applying"] = new JsonObject { ["version"] = applying.Version, ["target"] = applying.Target };
        }

        return JsonText.Stringify(root);
    }

    private static string? Text(JsonObject node, string name) => node.TryGetPropertyValue(name, out var value) ? JsonText.Str(value) : null;

    private static string? Version(JsonObject node, string name) => UpdateVersion.Clean(Text(node, name));

    // A value that goes into a header or a file name is short and has no control character.
    private static string? Short(string? text) => text is { Length: > 0 and <= 200 } && !text.Any(char.IsControl) ? text : null;

    private static long? Number(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue number && number.TryGetValue<long>(out var result) ? result : null;
}
