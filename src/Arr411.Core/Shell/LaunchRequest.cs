namespace Arr411.Core.Shell;

/// <summary>
/// What a start of the program asks for: the command line of Windows (a magnet link or a .torrent file to open, a tab to show...), or the one
/// a second start forwards to the running instance. Anything unexpected is dropped, never acted upon.
/// </summary>
public sealed class LaunchRequest
{
    /// <summary>How many magnet links / files one request may carry.</summary>
    public const int MaxItems = 20;

    /// <summary>The longest argument taken into account (a magnet link with many trackers is a few KiB).</summary>
    public const int MaxArgumentLength = 8192;

    /// <summary>The most arguments looked at.</summary>
    public const int MaxArguments = 64;

    /// <summary>The longest text taken into account when one is handed to the window (dropped on it).</summary>
    public const int MaxTextLength = 64 * 1024;

    private static readonly char[] TextSeparators = [' ', '\t', '\r', '\n'];

    public static readonly LaunchRequest Empty = new([], []);

    private LaunchRequest(IReadOnlyList<string> magnets, IReadOnlyList<string> torrentFiles)
    {
        Magnets = magnets;
        TorrentFiles = torrentFiles;
    }

    /// <summary>A tab to show (<c>--tab</c>), or null.</summary>
    public string? Tab { get; private init; }

    public IReadOnlyList<string> Magnets { get; private init; }

    /// <summary>Full paths of .torrent files.</summary>
    public IReadOnlyList<string> TorrentFiles { get; private init; }

    /// <summary>A page of c411.org to show in the C411 tab, or null.</summary>
    public string? C411Url { get; private init; }

    /// <summary>Start without showing the window (start with Windows).</summary>
    public bool Background { get; private init; }

    /// <summary>Stop the running instance.</summary>
    public bool Quit { get; private init; }

    /// <summary>Let Windows offer Arr411 for magnet links and .torrent files, without starting the app.</summary>
    public bool Register { get; private init; }

    public bool Unregister { get; private init; }

    /// <summary>How many arguments were not understood or were refused.</summary>
    public int Ignored { get; private init; }

    /// <summary>Something to hand to the web app (a magnet link or a file).</summary>
    public bool HasTorrents => Magnets.Count > 0 || TorrentFiles.Count > 0;

    /// <summary>The request asks for something the user should see.</summary>
    public bool HasWork => HasTorrents || C411Url is not null || Tab is not null;

    /// <summary>
    /// What a text handed to the window (dropped on it) asks for: its magnet links and its link to a page of c411.org, nothing else.
    /// The text comes from anywhere: none of it is an option, and none of it is a path (a path to a share of another machine would make
    /// Windows reach out to it, with the credentials of the user).
    /// </summary>
    public static LaunchRequest FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        if (text.Length > MaxTextLength) return new LaunchRequest([], []) { Ignored = 1 };

        // After "--", nothing can be taken for an option.
        var items = new List<string> { "--" };
        items.AddRange(text.Split(TextSeparators, StringSplitOptions.RemoveEmptyEntries));
        return Build(items, "", allowFiles: false, afterDoubleDash: int.MaxValue);
    }

    /// <summary>
    /// What files dropped on the window ask for: the .torrent files among them, by the full paths Explorer gave. None of them is an
    /// option, and a relative path means nothing here.
    /// </summary>
    public static LaunchRequest FromDroppedFiles(IEnumerable<string> paths) =>
        Build(["--", .. paths], "", allowFiles: true, afterDoubleDash: int.MaxValue);

    /// <summary>
    /// What the command line of the program asks for. The commands registered with Windows are <c>"exe" -- "%1"</c>: the link or the file
    /// is the one argument that follows the <c>--</c>. A quote in a link ends that argument early and the rest becomes more arguments,
    /// which nobody asked for: they are dropped, so that a link cannot make the program open a file (on a share of another machine, for
    /// one) or anything else of its own choosing. Several items are given without the <c>--</c>.
    /// </summary>
    /// <param name="args">The arguments as Windows hands them over.</param>
    /// <param name="workingDirectory">What relative paths are relative to.</param>
    /// <param name="allowFiles">Whether the path of a .torrent file is taken. Yes for what Windows hands over; no for any text that came from elsewhere.</param>
    public static LaunchRequest Parse(IEnumerable<string> args, string workingDirectory, bool allowFiles = true) =>
        Build(args, workingDirectory, allowFiles, afterDoubleDash: 1);

    /// <param name="afterDoubleDash">How many arguments are looked at once the <c>--</c> is met; the others are ignored.</param>
    private static LaunchRequest Build(IEnumerable<string> args, string workingDirectory, bool allowFiles, int afterDoubleDash)
    {
        string? tab = null;
        string? c411 = null;
        bool background = false, quit = false, register = false, unregister = false;
        var ignored = 0;
        var magnets = new List<string>();
        var files = new List<string>();

        var list = args.Take(MaxArguments + 1).ToList();
        if (list.Count > MaxArguments)
        {
            ignored += list.Count - MaxArguments;
            list.RemoveRange(MaxArguments, list.Count - MaxArguments);
        }

        // The registered commands put "--" before the link: past it, nothing is an option any more.
        var optionsEnded = false;
        var past = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i].Trim();

            // A blank argument uses up its place as well: what follows it did not come from the link either.
            if (optionsEnded && past++ >= afterDoubleDash)
            {
                if (arg.Length > 0) ignored++;
                continue;
            }

            if (arg.Length == 0) continue;
            if (arg.Length > MaxArgumentLength)
            {
                ignored++;
                continue;
            }

            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                var name = (equals < 0 ? arg : arg[..equals]).ToLowerInvariant();
                switch (name)
                {
                    case "--background":
                        background = true;
                        break;
                    case "--quit":
                        quit = true;
                        break;
                    case "--register":
                        register = true;
                        break;
                    case "--unregister":
                        unregister = true;
                        break;
                    case "--tab":
                        var value = equals >= 0 ? arg[(equals + 1)..] : i + 1 < list.Count ? list[++i] : null;
                        if (Tabs.IsTab(value?.Trim().ToLowerInvariant())) tab = value!.Trim().ToLowerInvariant();
                        else ignored++;
                        break;
                    default:
                        ignored++;
                        break;
                }

                continue;
            }

            switch (ExternalLinks.Classify(arg))
            {
                case LinkKind.Magnet when arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && magnets.Count < MaxItems:
                    magnets.Add(arg);
                    continue;
                case LinkKind.C411 when ExternalLinks.C411Page(arg) is { } page && c411 is null:
                    c411 = page.AbsoluteUri;
                    continue;
                case LinkKind.Rejected when allowFiles && TorrentPath(arg, workingDirectory) is { } path && files.Count < MaxItems:
                    files.Add(path);
                    continue;
                default:
                    ignored++;
                    break;
            }
        }

        return new LaunchRequest(magnets, files)
        {
            Tab = tab,
            C411Url = c411,
            Background = background,
            Quit = quit,
            Register = register,
            Unregister = unregister,
            Ignored = ignored,
        };
    }

    // A .torrent file by its path. Device paths (\\.\pipe\..., \\?\...) are refused: reading them could block, or reach what is not a file.
    // Only complete paths and plain relative ones are taken: \x and C:x depend on a current directory that may be another process's.
    private static string? TorrentPath(string arg, string workingDirectory)
    {
        if (!arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return null;
        if (arg.StartsWith(@"\\.\", StringComparison.Ordinal) || arg.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;

        try
        {
            string? full = null;
            if (Path.IsPathFullyQualified(arg)) full = Path.GetFullPath(arg);
            else if (!Path.IsPathRooted(arg) && Path.IsPathFullyQualified(workingDirectory)) full = Path.GetFullPath(arg, workingDirectory);

            return full is null || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
