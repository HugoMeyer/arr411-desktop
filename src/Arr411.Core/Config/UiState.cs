using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Config;

/// <summary>What the window remembers between two runs (last tab, position...). Plain JSON, nothing sensitive.</summary>
public sealed class UiState
{
    private const int MaxCoordinate = 100_000;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly JsonObject _data;

    public UiState(string path)
    {
        _path = path;
        _data = Read(path);
    }

    /// <summary>The tab shown when the app was last used (never "settings").</summary>
    public string? LastTab
    {
        get
        {
            lock (_gate) return JsonText.Str(_data["lastTab"]);
        }
        set
        {
            lock (_gate)
            {
                if (JsonText.Str(_data["lastTab"]) == value) return;
                _data["lastTab"] = value;
                Save();
            }
        }
    }

    /// <summary>Whether closing the window keeps the app running in the notification area, to go on watching the downloads.</summary>
    public bool CloseToTray
    {
        get => Flag("closeToTray", true);
        set => SetFlag("closeToTray", value);
    }

    /// <summary>Whether the user was already told once that the app goes on in the notification area.</summary>
    public bool TrayHintShown
    {
        get => Flag("trayHintShown", false);
        set => SetFlag("trayHintShown", value);
    }

    /// <summary>Where the window was when it was last closed or hidden.</summary>
    public WindowBounds? Window
    {
        get
        {
            lock (_gate)
            {
                if (_data["window"] is not JsonObject window) return null;
                if (Int(window, "x", -MaxCoordinate) is not { } x || Int(window, "y", -MaxCoordinate) is not { } y) return null;
                if (Int(window, "width", 1) is not { } width || Int(window, "height", 1) is not { } height) return null;

                return new WindowBounds(x, y, width, height, window["maximized"] is JsonValue m && m.TryGetValue<bool>(out var maximized) && maximized);
            }
        }
        set
        {
            lock (_gate)
            {
                if (Window == value) return;
                _data["window"] = value is { } b
                    ? new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height, ["maximized"] = b.Maximized }
                    : null;
                Save();
            }
        }
    }

    private bool Flag(string key, bool fallback)
    {
        lock (_gate) return _data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
    }

    private void SetFlag(string key, bool value)
    {
        lock (_gate)
        {
            if (_data[key] is JsonValue current && current.TryGetValue<bool>(out var stored) && stored == value) return;
            _data[key] = value;
            Save();
        }
    }

    private static int? Int(JsonObject node, string key, int minimum) =>
        node[key] is JsonValue value && value.TryGetValue<int>(out var number) && number >= minimum && number <= MaxCoordinate ? number : null;

    private void Save()
    {
        try
        {
            AtomicFile.Write(_path, System.Text.Encoding.UTF8.GetBytes(JsonText.Stringify(_data)), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("UI state not saved: " + ex.Message);
        }
    }

    private static JsonObject Read(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject obj ? obj : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new JsonObject();
        }
    }
}
