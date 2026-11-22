namespace Arr411.Desktop;

/// <summary>
/// The modes of the program that run before anything else exists: no check for another instance, no log, no web component, no window.
/// <see cref="TryRun"/> is the first thing <c>Program.Main</c> does. A mode that takes the command line for itself and ends the process
/// (a future <c>--apply-update</c>, which replaces the files of a program that cannot be running meanwhile) is added here.
/// </summary>
internal static class EarlyMode
{
    /// <summary>The exit code of the process when <paramref name="args"/> ask for one of these modes; null when they do not and the program starts as usual.</summary>
    public static int? TryRun(string[] args) => null;
}
