using Arr411.Core.Platform;
using Microsoft.Win32;

namespace Arr411.Core.Tests;

// Everything happens under a scratch key of HKEY_CURRENT_USER that stands for "Software", removed afterwards: the real registrations are never touched.
public sealed class ShellRegistrationTests : IDisposable
{
    private const string ScratchParent = @"Software\Arr411Tests";
    private const string RunKey = @"Microsoft\Windows\CurrentVersion\Run";

    private readonly string _software = ScratchParent + @"\" + Guid.NewGuid().ToString("N");
    private readonly TempDir _dir = new();

    private string Exe => _dir.File("Arr411.exe");

    private string OpenCommand(string exe) => $"\"{exe}\" -- \"%1\"";

    private ShellRegistration For(string exe) => new(exe, _software);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_software, throwOnMissingSubKey: false);
        try
        {
            Registry.CurrentUser.DeleteSubKey(ScratchParent, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
            // Something else is still under it.
        }

        _dir.Dispose();
    }

    private static string? Value(string key, string name)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key);
        return subKey?.GetValue(name) as string;
    }

    private string? Read(string keyUnderSoftware, string name = "") => Value($@"{_software}\{keyUnderSoftware}", name);

    private void Set(string keyUnderSoftware, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_software}\{keyUnderSoftware}");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    // Every key, value, type and content under the scratch key, to compare two states of the registry.
    private List<string> Snapshot()
    {
        var lines = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(_software);
        if (root is not null) Walk(root, "", lines);

        return lines;
    }

    private static void Walk(RegistryKey key, string path, List<string> lines)
    {
        lines.Add($"[{path}]");
        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            lines.Add($"{name}|{key.GetValueKind(name)}|{(value is byte[] bytes ? Convert.ToHexString(bytes) : value)}");
        }

        foreach (var child in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using var subKey = key.OpenSubKey(child)!;
            Walk(subKey, path + @"\" + child, lines);
        }
    }

    [Fact]
    public void Nothing_is_registered_to_begin_with()
    {
        var registration = For(Exe);

        Assert.False(registration.IsRegistered);
        Assert.False(registration.StartsWithWindows);
        Assert.Empty(Snapshot());
    }

    [Fact]
    public void Registering_adds_the_magnet_and_torrent_handlers()
    {
        var registration = For(Exe);

        registration.Register();

        Assert.True(registration.IsRegistered);
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Torrent\shell\open\command"));
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Applications\Arr411.exe\shell\open\command"));
        Assert.Equal("", Read(@"Classes\Arr411.Magnet", "URL Protocol"));
        Assert.Equal($"\"{Exe}\",0", Read(@"Classes\Arr411.Magnet\DefaultIcon"));
        Assert.Equal($"\"{Exe}\",0", Read(@"Classes\Arr411.Torrent\DefaultIcon"));
        Assert.Equal("Arr411", Read(@"Classes\Applications\Arr411.exe", "FriendlyAppName"));
        Assert.Equal("", Read(@"Classes\Applications\Arr411.exe\SupportedTypes", ".torrent"));
    }

    [Fact]
    public void The_command_ends_the_options_before_the_link_it_is_given()
    {
        For(Exe).Register();

        // "--" first, so that a link holding a quote cannot add options of its own.
        Assert.EndsWith(@"-- ""%1""", Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.EndsWith(@"-- ""%1""", Read(@"Classes\Arr411.Torrent\shell\open\command"));
    }

    [Fact]
    public void Registering_does_not_start_the_program_with_Windows()
    {
        var registration = For(Exe);

        registration.Register();

        Assert.False(registration.StartsWithWindows);
        Assert.Null(Read(RunKey, "Arr411"));
    }

    [Fact]
    public void Windows_is_told_what_Arr411_can_open()
    {
        For(Exe).Register();

        Assert.Equal("Arr411", Read(@"Arr411\Capabilities", "ApplicationName"));
        Assert.False(string.IsNullOrWhiteSpace(Read(@"Arr411\Capabilities", "ApplicationDescription")));
        Assert.Equal("Arr411.Magnet", Read(@"Arr411\Capabilities\URLAssociations", "magnet"));
        Assert.Equal("Arr411.Torrent", Read(@"Arr411\Capabilities\FileAssociations", ".torrent"));
        Assert.Equal($@"{_software}\Arr411\Capabilities", Read("RegisteredApplications", "Arr411"));
    }

    [Fact]
    public void Torrent_files_get_Arr411_in_their_open_with_list_but_not_as_the_default()
    {
        For(Exe).Register();

        using var progIds = Registry.CurrentUser.OpenSubKey($@"{_software}\Classes\.torrent\OpenWithProgids");
        Assert.NotNull(progIds);
        Assert.Equal(["Arr411.Torrent"], progIds.GetValueNames());
        Assert.Equal(RegistryValueKind.None, progIds.GetValueKind("Arr411.Torrent"));

        // What the user chose to open .torrent files with is theirs to change, in Windows' own settings.
        using var extension = Registry.CurrentUser.OpenSubKey($@"{_software}\Classes\.torrent");
        Assert.NotNull(extension);
        Assert.Null(extension.GetValue(""));
        Assert.Null(Read(@"Classes\magnet"));
        Assert.Null(Read(@"Classes\magnet\shell\open\command"));
    }

    [Fact]
    public void Registering_again_changes_nothing()
    {
        var registration = For(Exe);
        registration.Register();
        var once = Snapshot();

        registration.Register();

        Assert.NotEmpty(once);
        Assert.Equal(once, Snapshot());
    }

    [Fact]
    public void A_program_given_by_a_relative_path_is_registered_with_its_full_path()
    {
        var relative = @"sous\Arr411.exe";

        For(relative).Register();

        Assert.Equal(OpenCommand(Path.GetFullPath(relative)), Read(@"Classes\Arr411.Magnet\shell\open\command"));
    }

    [Fact]
    public void A_trailing_backslash_on_the_software_key_is_harmless()
    {
        new ShellRegistration(Exe, _software + @"\").Register();

        Assert.Equal($@"{_software}\Arr411\Capabilities", Read("RegisteredApplications", "Arr411"));
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Magnet\shell\open\command"));
    }

    [Fact]
    public void Unregistering_removes_what_was_registered_and_nothing_else()
    {
        using (var progIds = Registry.CurrentUser.CreateSubKey($@"{_software}\Classes\.torrent\OpenWithProgids"))
        {
            progIds.SetValue("Other.Torrent", Array.Empty<byte>(), RegistryValueKind.None);
        }

        Set(@"Classes\Other.Torrent\shell\open\command", "", "other.exe");
        Set(@"Classes\Applications\Other.exe\shell\open\command", "", "other.exe");
        Set("RegisteredApplications", "Other", @"Software\Other\Capabilities");
        Set(RunKey, "Other", "other.exe");
        var before = Snapshot();
        var registration = For(Exe);
        registration.Register();
        registration.SetStartWithWindows(true);
        Assert.NotEqual(before, Snapshot());

        registration.Unregister();

        Assert.False(registration.IsRegistered);
        Assert.False(registration.StartsWithWindows);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Unregistering_when_nothing_is_registered_is_harmless()
    {
        var registration = For(Exe);

        registration.Unregister();

        Assert.False(registration.IsRegistered);
        Assert.Empty(Snapshot());
    }

    [Fact]
    public void Unregistering_also_stops_the_start_with_Windows()
    {
        var registration = For(Exe);
        registration.SetStartWithWindows(true);

        registration.Unregister();

        Assert.False(registration.StartsWithWindows);
    }

    [Fact]
    public void The_start_with_Windows_runs_the_program_in_the_background()
    {
        var registration = For(Exe);

        registration.SetStartWithWindows(true);

        Assert.True(registration.StartsWithWindows);
        Assert.Equal($"\"{Exe}\" --background", Read(RunKey, "Arr411"));
        Assert.False(registration.IsRegistered);
    }

    [Fact]
    public void The_start_with_Windows_can_be_turned_off_and_on_again()
    {
        var registration = For(Exe);
        registration.SetStartWithWindows(true);

        registration.SetStartWithWindows(false);
        Assert.False(registration.StartsWithWindows);
        Assert.Null(Read(RunKey, "Arr411"));

        registration.SetStartWithWindows(false);
        Assert.False(registration.StartsWithWindows);

        registration.SetStartWithWindows(true);
        registration.SetStartWithWindows(true);
        Assert.True(registration.StartsWithWindows);
    }

    [Fact]
    public void Turning_the_start_off_leaves_other_programs_of_the_run_list_alone()
    {
        Set(RunKey, "Other", "other.exe");
        var registration = For(Exe);
        registration.SetStartWithWindows(true);

        registration.SetStartWithWindows(false);

        Assert.Equal("other.exe", Read(RunKey, "Other"));
    }

    [Fact]
    public void Following_a_move_does_nothing_when_nothing_was_registered()
    {
        For(Exe).FollowMove();

        Assert.Empty(Snapshot());
    }

    [Fact]
    public void A_program_that_moved_is_registered_again_at_its_new_place()
    {
        var gone = _dir.File(@"ancien\Arr411.exe");
        var before = For(gone);
        before.Register();
        before.SetStartWithWindows(true);

        For(Exe).FollowMove();

        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Torrent\shell\open\command"));
        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Applications\Arr411.exe\shell\open\command"));
        Assert.Equal($"\"{Exe}\" --background", Read(RunKey, "Arr411"));
    }

    [Fact]
    public void A_program_that_is_still_where_it_was_registered_is_left_alone()
    {
        var other = _dir.File(@"autre copie\Arr411.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "");
        var before = For(other);
        before.Register();
        before.SetStartWithWindows(true);
        var state = Snapshot();

        For(Exe).FollowMove();

        Assert.Equal(state, Snapshot());
    }

    [Fact]
    public void The_same_program_is_left_alone()
    {
        var registration = For(Exe);
        registration.Register();
        registration.SetStartWithWindows(true);
        var state = Snapshot();

        registration.FollowMove();

        Assert.Equal(state, Snapshot());
    }

    [Fact]
    public void The_case_of_the_path_does_not_make_it_another_program()
    {
        var before = For(Exe.ToUpperInvariant());
        before.Register();
        before.SetStartWithWindows(true);
        var state = Snapshot();

        For(Exe).FollowMove();

        Assert.Equal(state, Snapshot());
    }

    [Fact]
    public void The_start_with_Windows_follows_a_move_without_registering_the_links()
    {
        For(_dir.File(@"ancien\Arr411.exe")).SetStartWithWindows(true);
        var current = For(Exe);

        current.FollowMove();

        Assert.Equal($"\"{Exe}\" --background", Read(RunKey, "Arr411"));
        Assert.False(current.IsRegistered);
    }

    [Fact]
    public void The_links_follow_a_move_without_starting_with_Windows()
    {
        For(_dir.File(@"ancien\Arr411.exe")).Register();
        var current = For(Exe);

        current.FollowMove();

        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.False(current.StartsWithWindows);
    }

    [Fact]
    public void A_path_with_spaces_is_quoted_and_still_recognised_after_a_move()
    {
        var spaced = _dir.File(@"Program Files\Arr 411\Arr411.exe");
        var registration = For(spaced);
        registration.Register();
        registration.SetStartWithWindows(true);
        Assert.Equal(OpenCommand(spaced), Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.Equal($"\"{spaced}\" --background", Read(RunKey, "Arr411"));

        For(Exe).FollowMove();

        Assert.Equal(OpenCommand(Exe), Read(@"Classes\Arr411.Magnet\shell\open\command"));
        Assert.Equal($"\"{Exe}\" --background", Read(RunKey, "Arr411"));
    }

    [Theory]
    [InlineData("\"C:\\disparu\\Arr411.exe\" --background")]
    [InlineData("C:\\disparu\\Arr411.exe --background")]
    [InlineData("C:\\disparu\\Arr411.exe")]
    public void A_start_with_Windows_written_another_way_is_still_followed(string written)
    {
        Set(RunKey, "Arr411", written);

        For(Exe).FollowMove();

        Assert.Equal($"\"{Exe}\" --background", Read(RunKey, "Arr411"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    public void A_start_with_Windows_that_names_no_program_is_left_alone(string written)
    {
        Set(RunKey, "Arr411", written);

        For(Exe).FollowMove();

        Assert.Equal(written, Read(RunKey, "Arr411"));
    }

    // Following a move runs at every start: a registry edited by hand must never be able to stop the app from opening.
    [Fact]
    public void A_start_with_Windows_that_is_far_too_long_to_be_a_path_is_left_alone()
    {
        var written = $"\"C:\\{new string('a', 40_000)}\\Arr411.exe\" --background";
        Set(RunKey, "Arr411", written);

        For(Exe).FollowMove();

        Assert.Equal(written, Read(RunKey, "Arr411"));
    }

    [Fact]
    public void The_real_registrations_of_the_user_are_never_touched()
    {
        var before = RealState();
        var registration = For(Exe);

        registration.Register();
        registration.SetStartWithWindows(true);
        registration.FollowMove();
        registration.Unregister();

        Assert.Equal(before, RealState());

        static string?[] RealState() =>
        [
            Value(@"Software\Classes\Arr411.Magnet\shell\open\command", ""),
            Value(@"Software\Classes\Arr411.Torrent\shell\open\command", ""),
            Value(@"Software\RegisteredApplications", "Arr411"),
            Value(@"Software\Microsoft\Windows\CurrentVersion\Run", "Arr411"),
        ];
    }
}
