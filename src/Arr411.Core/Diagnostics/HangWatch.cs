using System.Globalization;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// Tells when the window stops answering: every few seconds a small job is posted to the thread of the window, and if it has not run after ten seconds the
/// log says so (once), then says when the window answers again. A program that is stuck looks like a crashed one to the person in front of it, and the
/// log is the only place where the difference shows.
/// </summary>
public sealed class HangWatch : IDisposable
{
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(10);

    private readonly Action<Action> _postToUi;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _period;
    private readonly TimeSpan _limit;
    private readonly object _gate = new();
    private readonly Timer? _timer;
    private DateTimeOffset _lastTick;
    private DateTimeOffset? _waitingSince;
    private bool _warned;
    private bool _stopped;

    /// <param name="postToUi">Hands a job to the thread of the window and returns at once (BeginInvoke); it throws when the window is gone.</param>
    public HangWatch(Action<Action> postToUi, Action<string> info, Action<string> warn)
        : this(postToUi, info, warn, () => DateTimeOffset.Now, DefaultPeriod, DefaultLimit, start: true)
    {
    }

    internal HangWatch(
        Action<Action> postToUi,
        Action<string> info,
        Action<string> warn,
        Func<DateTimeOffset> now,
        TimeSpan period,
        TimeSpan limit,
        bool start)
    {
        _postToUi = postToUi;
        _info = info;
        _warn = warn;
        _now = now;
        _period = period;
        _limit = limit;
        _lastTick = now();
        if (start) _timer = new Timer(_ => Tick(), null, period, period);
    }

    public void Dispose()
    {
        lock (_gate) _stopped = true;
        _timer?.Dispose();
    }

    internal void Tick()
    {
        lock (_gate)
        {
            if (_stopped) return;
            var now = _now();
            var gap = now - _lastTick;
            _lastTick = now;

            // The computer slept: the window could not answer, and nobody was waiting for it.
            if (gap > _period * 5 + _limit)
            {
                _waitingSince = null;
                _warned = false;
            }

            if (_waitingSince is { } since)
            {
                var waited = now - since;
                if (waited >= _limit && !_warned)
                {
                    _warned = true;
                    _warn(string.Create(CultureInfo.InvariantCulture, $"The window has not answered for {waited.TotalSeconds:0} s"));
                }

                return;
            }

            _waitingSince = now;
            try
            {
                _postToUi(Answered);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The window is closing: there is nothing left to watch.
                _stopped = true;
                _timer?.Dispose();
            }
        }
    }

    private void Answered()
    {
        lock (_gate)
        {
            if (_waitingSince is not { } since) return;
            var waited = _now() - since;
            _waitingSince = null;
            if (!_warned) return;
            _warned = false;
            _info(string.Create(CultureInfo.InvariantCulture, $"The window answers again after {waited.TotalSeconds:0} s"));
        }
    }
}
