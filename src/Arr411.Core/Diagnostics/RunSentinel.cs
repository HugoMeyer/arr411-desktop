using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Arr411.Core.Diagnostics;

/// <summary>What a run left behind when it did not end the way it should: it was killed, the power went, or the program died too fast to write anything.</summary>
public sealed record PreviousRun(int Pid, string Version, DateTimeOffset? Started, DateTimeOffset? LastSeen, string? Memory, string? Crash)
{
    public override string ToString()
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"The previous run did not end cleanly: version {Version}, process {Pid}, started {Format(Started)}, last seen alive {Format(LastSeen)}");
        if (Crash is not null) text += $", it recorded a crash ({Crash})";
        if (Memory is not null) text += $", memory then: {Memory}";
        return text;
    }

    private static string Format(DateTimeOffset? at) => at is { } time ? time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "never";
}

/// <summary>
/// running.txt, written when the program starts, refreshed every minute and deleted when it ends the right way. Finding it at the next start means the
/// last run was stopped from outside (killed, power cut, crash of the runtime itself): no handler could have written anything about it, but this file
/// says when it was last alive and how much memory it held then.
/// </summary>
public sealed class RunSentinel
{
    private readonly string _path;
    private readonly object _gate = new();
    private int _pid;
    private string _version = "?";
    private DateTimeOffset _started;
    private DateTimeOffset? _lastSeen;
    private string? _memory;
    private string? _crash;
    private bool _ended;

    public RunSentinel(string path) => _path = path;

    /// <summary>The previous run, when it left its file behind and is no longer running.</summary>
    public static PreviousRun? Leftover(string path) => Leftover(path, ProcessIsRunning);

    internal static PreviousRun? Leftover(string path, Func<int, DateTimeOffset?, bool> isRunning)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var at = line.IndexOf('=');
                if (at > 0) values[line[..at].Trim()] = line[(at + 1)..].Trim();
            }

            if (!values.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, CultureInfo.InvariantCulture, out var pid)) return null;
            var started = Time(values, "started");
            if (isRunning(pid, started)) return null;

            return new PreviousRun(
                pid,
                values.GetValueOrDefault("version") ?? "?",
                started,
                Time(values, "lastseen"),
                values.GetValueOrDefault("memory"),
                values.GetValueOrDefault("crashed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Begin(string version, int pid, DateTimeOffset started)
    {
        lock (_gate)
        {
            _version = version;
            _pid = pid;
            _started = started;
            _lastSeen = DateTimeOffset.Now;
            _memory = null;
            _crash = null;
            _ended = false;
            Save();
        }
    }

    public void Beat(MemorySnapshot memory)
    {
        lock (_gate)
        {
            // A beat that comes after the end (the timer is not stopped at the very same moment) must not bring the file back.
            if (_ended) return;
            _lastSeen = DateTimeOffset.Now;
            _memory = memory.ToString();
            Save();
        }
    }

    /// <summary>Notes that an exception that nobody caught is stopping the program: the file stays, and says so.</summary>
    public void Crashed(string source)
    {
        lock (_gate)
        {
            if (_ended) return;
            _crash = source;
            _lastSeen = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>The program ends the right way: nothing is left to find. After a crash the file stays, whatever else runs while the program goes down.</summary>
    public void End()
    {
        lock (_gate)
        {
            if (_crash is not null) return;
            _ended = true;
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that stays only makes the next start say so.
            }
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string>
            {
                $"pid={_pid.ToString(CultureInfo.InvariantCulture)}",
                $"version={_version}",
                $"started={_started.ToString("o", CultureInfo.InvariantCulture)}",
                $"lastseen={_lastSeen?.ToString("o", CultureInfo.InvariantCulture)}",
            };
            if (_memory is not null) lines.Add($"memory={_memory}");
            if (_crash is not null) lines.Add($"crashed={_crash}");

            // Written beside, then moved over: a kill in the middle leaves the old file whole.
            var temp = _path + ".tmp";
            File.WriteAllLines(temp, lines);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the next start would know less.
        }
    }

    private static DateTimeOffset? Time(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;

    // A process with that number and that start time: another one that got the number after the first ended would not have the same start time.
    private static bool ProcessIsRunning(int pid, DateTimeOffset? started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return started is not { } expected || Math.Abs((new DateTimeOffset(process.StartTime) - expected).TotalSeconds) < 5;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
