using System.Globalization;

namespace Arr411.Core.Update;

/// <summary>The arguments the app gives the applier (<c>--apply-update --target &lt;exe&gt; --wait-pid &lt;pid&gt; --from &lt;version&gt; [--restart] [--background]</c>), read the strict way.</summary>
/// <param name="Problem">What is wrong with them; null when they can be acted on (the target still has to be checked: <see cref="CheckTarget"/>).</param>
internal sealed record ApplyRequest(string? Target, int? WaitPid, string? From, bool Restart, bool Background, string? Problem)
{
    public static ApplyRequest Parse(IReadOnlyList<string> args)
    {
        string? target = null, from = null, problem = null;
        int? waitPid = null;
        var restart = false;
        var background = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var equals = arg.IndexOf('=');
            switch (equals >= 0 ? arg[..equals] : arg)
            {
                case "--target":
                    target = Value(args, ref i, equals);
                    break;
                case "--wait-pid":
                    if (int.TryParse(Value(args, ref i, equals), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0) waitPid = pid;
                    else problem ??= "--wait-pid is not a process id";
                    break;
                case "--from":
                    from = UpdateVersion.Clean(Value(args, ref i, equals));
                    if (from is null) problem ??= "--from is not a version";
                    break;
                case "--restart":
                    restart = true;
                    break;
                case "--background":
                    background = true;
                    break;
            }
        }

        problem ??= target is null ? "--target is missing" : waitPid is null ? "--wait-pid is missing" : null;
        return new ApplyRequest(target, waitPid, from, restart, background, problem);
    }

    /// <summary>
    /// The program to replace, when it is one the applier may replace: a full path to an <c>.exe</c> (no device path, no stream), not the applier itself,
    /// named like the app or exactly the file the app said it was running when it handed the update over. Otherwise the reason it is not.
    /// </summary>
    public string? CheckTarget(string self, string? recordedTarget, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(Target)) return "no target";

        if (Target.StartsWith(@"\\.\", StringComparison.Ordinal) || Target.StartsWith(@"\\?\", StringComparison.Ordinal)) return "device path";

        if (!Path.IsPathFullyQualified(Target)) return "relative path";

        try
        {
            target = Path.GetFullPath(Target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "invalid path";
        }

        if (target.IndexOf(':', 2) >= 0 && !target.StartsWith(@"\\", StringComparison.Ordinal)) return "stream or device name";

        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "not an .exe";

        if (string.Equals(target, self, StringComparison.OrdinalIgnoreCase)) return "the applier itself";

        var named = string.Equals(Path.GetFileName(target), UpdateService.AssetName, StringComparison.OrdinalIgnoreCase);
        return named || SamePath(recordedTarget, target) ? null : "not the program that was running";
    }

    private static bool SamePath(string? recorded, string target)
    {
        if (string.IsNullOrEmpty(recorded) || !Path.IsPathFullyQualified(recorded)) return false;

        try
        {
            return string.Equals(Path.GetFullPath(recorded), target, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? Value(IReadOnlyList<string> args, ref int i, int equals) =>
        equals >= 0 ? args[i][(equals + 1)..] : i + 1 < args.Count ? args[++i] : null;
}
