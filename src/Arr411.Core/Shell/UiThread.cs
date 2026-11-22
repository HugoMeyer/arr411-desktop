namespace Arr411.Core.Shell;

/// <summary>
/// Gets work onto the thread of the window. Made on that thread, it runs at once what is asked from that thread, and posts what is asked from
/// any other: by then the window may be closing, which leaves nothing to do. <c>Control.InvokeRequired</c> cannot tell this before the window
/// has a handle (it says no, and the work would run on the wrong thread).
/// </summary>
public sealed class UiThread
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<Action> _post;

    /// <param name="post">Has the window run the action on its thread, later. Throws <see cref="InvalidOperationException"/> or <see cref="ObjectDisposedException"/> when it cannot.</param>
    public UiThread(Action<Action> post) => _post = post;

    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread;

    public void Run(Action action)
    {
        if (IsCurrent)
        {
            action();
            return;
        }

        try
        {
            _post(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing: nobody is left to take it.
        }
    }
}
