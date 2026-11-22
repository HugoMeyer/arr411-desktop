namespace Arr411.Core.Shell;

/// <summary>
/// What arrives before whoever handles it is ready (a launch handed over while the window has no handle yet) waits here, in the order it came,
/// until <see cref="Release"/> takes it. From then on nothing waits any more: <see cref="TryHold"/> says no, and the item is the caller's.
/// Safe to use from any thread.
/// </summary>
public sealed class EarlyQueue<T>
{
    private readonly object _gate = new();
    private List<T>? _held = [];

    /// <summary>Keeps the item until <see cref="Release"/>. False once released.</summary>
    public bool TryHold(T item)
    {
        lock (_gate)
        {
            if (_held is null) return false;

            _held.Add(item);
            return true;
        }
    }

    /// <summary>Everything that waited, in order. Nothing the second time.</summary>
    public IReadOnlyList<T> Release()
    {
        lock (_gate)
        {
            var held = _held;
            _held = null;
            return held ?? [];
        }
    }
}
