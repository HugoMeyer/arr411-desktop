namespace Arr411.Core.Shell;

/// <summary>
/// What a web view does when its render process stops (a crash, or a page that no longer answers): the page is loaded again, unless it keeps
/// stopping, which would only load it and stop it again and again. The third stop within a minute ends the automatic reloads; the user
/// asking for the page again (<see cref="Reset"/>) starts them over. Not thread-safe: the views call it on the UI thread.
/// </summary>
/// <param name="clock">Milliseconds that only go forward (<see cref="Environment.TickCount64"/> by default).</param>
public sealed class RenderCrashPolicy(Func<long>? clock = null)
{
    /// <summary>How many stops within <see cref="Window"/> it takes to give up.</summary>
    public const int MaxStops = 3;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Queue<long> _stops = new();

    /// <summary>Whether the page is no longer loaded again by itself. It stays so until <see cref="Reset"/>, however long ago the last stop was.</summary>
    public bool GivenUp { get; private set; }

    /// <summary>Notes a stop of the render process. True when the page may be loaded again by itself.</summary>
    public bool ShouldReload()
    {
        var now = _clock();
        while (_stops.Count > 0 && now - _stops.Peek() >= Window.TotalMilliseconds) _stops.Dequeue();

        _stops.Enqueue(now);
        if (_stops.Count >= MaxStops) GivenUp = true;

        return !GivenUp;
    }

    /// <summary>The user asked for the page again: the stops that came before no longer count.</summary>
    public void Reset()
    {
        _stops.Clear();
        GivenUp = false;
    }
}
