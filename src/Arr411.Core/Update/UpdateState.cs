using System.Text.Json.Nodes;

namespace Arr411.Core.Update;

/// <summary>The values of <see cref="UpdateState.Status"/>.</summary>
public static class UpdateStatus
{
    public const string Idle = "idle";
    public const string Checking = "checking";
    public const string UpToDate = "uptodate";
    public const string Downloading = "downloading";
    public const string Ready = "ready";
    public const string Installing = "installing";
    public const string Error = "error";
}

/// <summary>The values of the <c>autoUpdate</c> setting.</summary>
public static class UpdateMode
{
    public const string Auto = "auto";
    public const string Notify = "notify";
    public const string Off = "off";

    public const string ConfigKey = "autoUpdate";

    /// <summary>The mode a setting stands for: anything that is not one of the three (missing, other text, other type) is <see cref="Auto"/>.</summary>
    public static string From(string? setting)
    {
        var value = setting?.Trim().ToLowerInvariant();
        return value is Auto or Notify or Off ? value : Auto;
    }
}

/// <summary>
/// What the update service knows, in the shape the web app expects (the shared update contract of the Android, Chrome and Desktop apps):
/// <see cref="ToJson"/> keeps the order and the names of the contract.
/// </summary>
/// <param name="Supported">False when updates are switched off for this run (ARR411_UPDATE=off).</param>
/// <param name="Current">The version that runs.</param>
/// <param name="Latest">The newest version found, null until a check succeeded.</param>
/// <param name="Available">The newest version is above the one that runs.</param>
/// <param name="Progress">0 to 1 while a download runs, else null.</param>
/// <param name="Notes">The start of the release notes; may be empty.</param>
/// <param name="Url">The page of the release (or the page of the latest one when none is known).</param>
/// <param name="CheckedAt">When the last check succeeded, in milliseconds since 1970; null when none did.</param>
/// <param name="CanInstall">False when the app cannot replace itself here (or the release carries nothing to verify): the web app offers the page instead.</param>
/// <param name="Error">What went wrong, in French; null when nothing did.</param>
/// <param name="Mode">The <c>autoUpdate</c> setting.</param>
public sealed record UpdateState(
    bool Supported,
    string Current,
    string? Latest,
    bool Available,
    string Status,
    double? Progress,
    string Notes,
    string Url,
    long? CheckedAt,
    bool CanInstall,
    string? Error,
    string Mode)
{
    public JsonObject ToJson() => new()
    {
        ["supported"] = Supported,
        ["current"] = Current,
        ["latest"] = Latest,
        ["available"] = Available,
        ["status"] = Status,
        ["progress"] = Progress,
        ["notes"] = Notes,
        ["url"] = Url,
        ["checkedAt"] = CheckedAt,
        ["canInstall"] = CanInstall,
        ["error"] = Error,
        ["mode"] = Mode,
    };
}
