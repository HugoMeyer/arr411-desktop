namespace Arr411.Core.Update;

/// <summary>What the web app sees of the updater (see <see cref="Bridge.SpaBridge"/>): the state, a way to follow it, and the two things it may ask for.</summary>
public interface IUpdateFeed
{
    UpdateState State { get; }

    /// <summary>Raised after every change of the state, on any thread.</summary>
    event EventHandler<UpdateState>? StateChanged;

    /// <summary>Looks for a new version now. At most one such request a minute is taken into account. Returns at once.</summary>
    void CheckNow();

    /// <summary>Installs the new version: downloads it first when it is not yet, then restarts into it. Returns at once.</summary>
    void Install();
}

public enum UpdateNoticeKind
{
    /// <summary>A new version exists and the app does not fetch it by itself: the user decides.</summary>
    Available,

    /// <summary>A new version is downloaded and checked: it is installed at the next start or exit.</summary>
    Ready,

    /// <summary>A swap did not work.</summary>
    Failed,

    /// <summary>The app was just updated.</summary>
    Updated,
}

/// <summary>Something the user is told about, once: a title and a text, in French.</summary>
public sealed record UpdateNotice(UpdateNoticeKind Kind, string Version, string Title, string Text);
