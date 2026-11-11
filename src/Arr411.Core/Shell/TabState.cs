namespace Arr411.Core.Shell;

/// <summary>What the window has to do after a tab change.</summary>
/// <param name="Changed">The tab on screen changed: show the right view, update the bar, remember it.</param>
/// <param name="TellSpa">The web app must be told to switch too.</param>
public readonly record struct TabChange(bool Changed, bool TellSpa)
{
    public static readonly TabChange None = new(false, false);
}

/// <summary>
/// Which tab is shown, and which one the web app believes is shown (same rules as the Android MainActivity).
/// The web app draws every tab except C411; the window and the web app must stay in step. UI thread only.
/// </summary>
public sealed class TabState
{
    private string _spaTab = "";

    public string Current { get; private set; } = "";

    /// <summary>The tab shown before the current one.</summary>
    public string Previous { get; private set; } = Tabs.C411;

    public bool SpaReady { get; private set; }

    /// <param name="tellSpa">False when the web app asked for the change itself (it already knows).</param>
    public TabChange Show(string name, bool tellSpa)
    {
        if (!Tabs.IsTab(name)) return TabChange.None;

        var changed = name != Current;
        if (changed)
        {
            if (Current.Length > 0) Previous = Current;
            Current = name;
        }

        var notify = false;
        if (!tellSpa)
        {
            _spaTab = name;
        }
        else if (SpaReady && name != _spaTab)
        {
            _spaTab = name;
            notify = true;
        }

        return new TabChange(changed, notify);
    }

    /// <summary>The web app asks which tab to open on.</summary>
    public string TabForSpa()
    {
        _spaTab = Current;
        return Current;
    }

    /// <summary>A page of the web app starts loading.</summary>
    public void OnSpaStarted() => SpaReady = false;

    /// <summary>The web app finished booting. Returns true when it must be told to switch to <paramref name="tab"/>.</summary>
    public bool OnSpaReady(out string tab)
    {
        SpaReady = true;
        tab = Current;
        if (Current == _spaTab) return false;
        _spaTab = Current;
        return true;
    }

    /// <summary>Where "back" goes when the web app has nothing left to go back to.</summary>
    public string BackTarget() => Current == Tabs.Settings && Previous != Tabs.Settings ? Previous : Tabs.C411;
}
