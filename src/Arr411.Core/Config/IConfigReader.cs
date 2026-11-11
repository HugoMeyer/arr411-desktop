namespace Arr411.Core.Config;

/// <summary>Read access to the app settings.</summary>
public interface IConfigReader
{
    /// <summary>The string stored under a top-level key, or null when it is absent or not a string.</summary>
    string? GetString(string key);
}
