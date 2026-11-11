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

        var data = TryRead(_path);
        if (data is not null) return data;

        data = TryRead(_path + ".bak");
        if (data is not null)
        {
            Log.Warn("Settings restored from the backup copy");
            return data;
        }

        Log.Warn("Settings could not be read: starting empty (the old file is kept as config.dat.bad)");
        try
        {
            File.Move(_path, _path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place: the next save replaces it.
        }

        return new JsonObject();
    }

    private JsonObject? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var plain = _protector.Unprotect(File.ReadAllBytes(file));
            return JsonNode.Parse(plain) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
