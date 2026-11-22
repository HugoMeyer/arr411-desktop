using Arr411.Core.Bridge;

namespace Arr411.Desktop;

/// <summary>What Windows says about the user, for the bridge of the C411 page (see <see cref="IUserPresence"/>): the page can fake none of it.</summary>
internal sealed class WindowsUserPresence : IUserPresence
{
#if DEBUG
    /// <summary>Debug builds only: set to 1, the user is always there (the tab still has to be on screen). For the end-to-end tests, whose pages post messages by themselves.</summary>
    public const string GestureVariable = "ARR411_PAGE_GESTURE";

    private static readonly bool Always = Environment.GetEnvironmentVariable(GestureVariable) == "1";
#endif

    private readonly Func<IntPtr> _window;

    /// <param name="window">The handle of the main window: a drop is the user's when the pointer is over it.</param>
    public WindowsUserPresence(Func<IntPtr> window) => _window = window;

    public bool HasRecentGesture(TimeSpan window)
    {
#if DEBUG
        if (Always) return true;
#endif
        return NativeMethods.IsForegroundWindowOurs() && InputIsRecent(window);
    }

    public bool HasRecentDrop(TimeSpan window)
    {
#if DEBUG
        if (Always) return true;
#endif
        return InputIsRecent(window) && NativeMethods.TopLevelWindowUnderPointer() is var under && under != IntPtr.Zero && under == _window();
    }

    private static bool InputIsRecent(TimeSpan window) => NativeMethods.TryGetLastInputAge(out var age) && age <= window;
}
