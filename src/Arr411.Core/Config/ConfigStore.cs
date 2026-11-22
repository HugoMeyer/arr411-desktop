using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Logging;

namespace Arr411.Core.Config;

public sealed class ConfigChangedEventArgs : EventArgs
{
    public ConfigChangedEventArgs(string origin, string patchJson)
    {
        Origin = origin;
        PatchJson = patchJson;
    }

    /// <summary>Who made the change ("spa", "engine"...), so that a view is not told about its own edits.</summary>
    public string Origin { get; }

    /// <summary>The keys that really changed, as a JSON object.</summary>
    public string PatchJson { get; }
}

/// <summary>
/// The app settings (service addresses, API keys, qBittorrent tracking...): one JSON object, encrypted on disk.
/// Same semantics as the Android app's storage: a patch overwrites the keys it contains and leaves the others alone.
/// </summary>
public sealed class ConfigStore : IConfigReader
{
    // The copies of an unreadable settings file that are kept besides the newest one (config.dat.bad).
    private const int KeptUnreadable = 3;

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
    private readonly JsonObject _data;

    public ConfigStore(string path, IDataProtector protector)
    {
        _path = path;
        _protector = protector;
        _data = Load();
    }

    /// <summary>Raised, outside any lock, after a merge that changed something. May run on any thread.</summary>
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public string ToJson()
    {
        lock (_gate) return JsonText.Stringify(_data);
    }

    public string? GetString(string key)
    {
        lock (_gate) return _data.TryGetPropertyValue(key, out var node) ? JsonText.Str(node) : null;
    }

    /// <summary>The value of an on/off setting; null when it is missing or not a boolean.</summary>
    public bool? GetBool(string key)
    {
        lock (_gate)
        {
            return _data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
        }
    }

    /// <summary>Applies a JSON object patch. Returns false when it is invalid or changes nothing.</summary>
    public bool Merge(string patchJson, string origin)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(patchJson);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }

        return node is JsonObject patch && Merge(patch, origin);
    }

    public bool Merge(JsonObject patch, string origin)
    {
        var applied = new JsonObject();
        lock (_gate)
        {
            foreach (var (key, value) in patch)
            {
                if (_data.TryGetPropertyValue(key, out var current) && JsonNode.DeepEquals(current, value)) continue;
                _data[key] = value?.DeepClone();
                applied[key] = value?.DeepClone();
            }

            if (applied.Count == 0) return false;
            Save();
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(origin, JsonText.Stringify(applied)));
        return true;
    }

    private void Save()
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes(JsonText.Stringify(_data));
            AtomicFile.Write(_path, _protector.Protect(plain), keepBackup: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Error("Settings not saved", ex);
        }
    }

    private JsonObject Load()
    {
        if (!File.Exists(_path)) return new JsonObject();

        var data = TryRead(_path, out var failure);
        if (data is not null) return data;

        data = TryRead(_path + ".bak", out _);
        if (data is not null)
        {
            Log.Warn($"Settings file unreadable ({failure}): restored from the backup copy");
            return data;
        }

        Log.Warn($"Settings could not be read ({failure}) and there is no usable backup copy: starting empty. {SetAside()}");
        return new JsonObject();
    }

    // The unreadable file may be all that is left of a setup that cannot be opened for now (another Windows account, a profile being repaired): it is
    // kept as .bad, and the one kept before it gets the time it was written in its name. Only the newest few stay.
    private string SetAside()
    {
        var bad = _path + ".bad";
        try
        {
            if (File.Exists(bad))
            {
                var written = File.GetLastWriteTimeUtc(bad).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(bad, FreeName($"{_path}.{written}"));
            }

            File.Move(_path, bad);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "It could not be set aside: the next save replaces it.";
        }

        DeleteOldestUnreadable();
        return $"The unreadable file is kept as {Path.GetFileName(bad)}.";
    }

    private static string FreeName(string stem)
    {
        var name = stem + ".bad";
        for (var n = 2; File.Exists(name); n++) name = $"{stem}-{n}.bad";
        return name;
    }

    private void DeleteOldestUnreadable()
    {
        try
        {
            var full = Path.GetFullPath(_path);
            var older = Directory.EnumerateFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".*.bad")
                .Where(file => file.EndsWith(".bad", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file, StringComparer.Ordinal)
                .Skip(KeptUnreadable)
                .ToList();
            foreach (var file in older) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some old copies more stay: not worth more than that.
        }
    }

    private JsonObject? TryRead(string file, out string failure)
    {
        failure = "missing";
        try
        {
            if (!File.Exists(file)) return null;
            var plain = _protector.Unprotect(File.ReadAllBytes(file));
            if (JsonNode.Parse(plain) is JsonObject data) return data;

            failure = "not a settings object";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            failure = ex.GetType().Name;
            return null;
        }
    }
}
