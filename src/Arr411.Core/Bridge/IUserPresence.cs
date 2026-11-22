namespace Arr411.Core.Bridge;

/// <summary>
/// What the window can tell about the person at the keyboard. It comes from Windows and not from the page, so the page cannot fake any of it:
/// the bridge of the C411 page asks it before it does something for the page. Both calls are made on the UI thread.
/// </summary>
public interface IUserPresence
{
    /// <summary>
    /// The user is working in this app: its window is the one in front and the last key or mouse input is not older than <paramref name="window"/>.
    /// </summary>
    bool HasRecentGesture(TimeSpan window);

    /// <summary>
    /// The user just dropped something on the window: the pointer is over it and the last input is not older than <paramref name="window"/>.
    /// A drag from another program does not bring the window to the front, so the window in front says nothing about a drop.
    /// </summary>
    bool HasRecentDrop(TimeSpan window);
}
