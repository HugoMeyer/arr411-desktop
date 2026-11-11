using Microsoft.Win32;

namespace Arr411.E2E;

/// <summary>
/// What the app writes to the registry so that Windows lists it for magnet links and .torrent files, and starts it with Windows. The app is run with a
/// scratch key standing for "Software" in place of the real one: nothing of the user is touched, and each test puts back what it wrote.
/// </summary>
[Trait("Category", "E2E")]
public sealed class RegistrationTests(FirstRunApp fixture) : IClassFixture<FirstRunApp>
{
    // The entries of the menu of the app, counted from the top as the Down key does (the separators are skipped).
    private const int Associate = 2;
    private const int StartWithWindows = 3;

    private const string DefaultAppsLink = "ms-settings:defaultapps?registeredAppUser=Arr411";

    private AppProcess App => fixture.App;

    private string Software => App.SoftwareKey;

    private static string Exe => Path.GetFullPath(AppProcess.ExePath);

    private static string? Value(string key, string name = "")
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key);
        return subKey?.GetValue(name) as string;
    }

    private static bool Exists(string key)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key);
        return subKey is not null;
    }

    private static string[] ValueNames(string key)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key);
        return subKey?.GetValueNames() ?? [];
    }

    private async Task ChooseInMenuAsync(int position)
    {
        await App.ClickMenuAsync();
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(position);
    }

    [Fact]
    public async Task The_register_command_lists_the_app_for_magnet_links_and_torrent_files()
    {
        try
        {
            Assert.False(App.Registration.IsRegistered);

            Assert.Equal(0, await App.LaunchSecondAsync("--register"));

            Assert.True(App.Registration.IsRegistered);

            // A link or a file is given after "--": whatever it contains, it cannot add an option of its own, nor a file to open.
            var open = $"\"{Exe}\" -- \"%1\"";
            Assert.Equal(open, Value($@"{Software}\Classes\Arr411.Magnet\shell\open\command"), ignoreCase: true);
            Assert.Equal(open, Value($@"{Software}\Classes\Arr411.Torrent\shell\open\command"), ignoreCase: true);
            Assert.Equal("", Value($@"{Software}\Classes\Arr411.Magnet", "URL Protocol"));
            Assert.Equal("Arr411.Magnet", Value($@"{Software}\Arr411\Capabilities\URLAssociations", "magnet"));
            Assert.Equal("Arr411.Torrent", Value($@"{Software}\Arr411\Capabilities\FileAssociations", ".torrent"));
            Assert.Equal($@"{Software}\Arr411\Capabilities", Value($@"{Software}\RegisteredApplications", "Arr411"));
            Assert.Contains("Arr411.Torrent", ValueNames($@"{Software}\Classes\.torrent\OpenWithProgids"));
        }
        finally
        {
            await App.LaunchSecondAsync("--unregister");
        }
    }

    [Fact]
    public async Task The_app_is_listed_for_magnet_links_and_torrent_files_but_never_made_their_default()
    {
        try
        {
            Assert.Equal(0, await App.LaunchSecondAsync("--register"));

            // Choosing what opens a link is for the user to do in Windows: the app takes neither the protocol nor the file type.
            Assert.False(Exists($@"{Software}\Classes\magnet"));
            Assert.Null(Value($@"{Software}\Classes\.torrent"));
            Assert.False(Exists($@"{Software}\Microsoft\Windows\Shell\Associations"));
            Assert.False(App.Registration.StartsWithWindows);
        }
        finally
        {
            await App.LaunchSecondAsync("--unregister");
        }
    }

    [Fact]
    public async Task Registering_twice_is_the_same_as_registering_once()
    {
        try
        {
            Assert.Equal(0, await App.LaunchSecondAsync("--register"));
            Assert.Equal(0, await App.LaunchSecondAsync("--register"));

            Assert.True(App.Registration.IsRegistered);
            Assert.Single(ValueNames($@"{Software}\Classes\.torrent\OpenWithProgids"));
        }
        finally
        {
            await App.LaunchSecondAsync("--unregister");
        }
    }

    [Fact]
    public async Task The_unregister_command_removes_everything_the_register_command_wrote_and_can_be_run_again()
    {
        Assert.Equal(0, await App.LaunchSecondAsync("--register"));

        Assert.Equal(0, await App.LaunchSecondAsync("--unregister"));

        Assert.False(App.Registration.IsRegistered);
        Assert.False(Exists($@"{Software}\Classes\Arr411.Magnet"));
        Assert.False(Exists($@"{Software}\Classes\Arr411.Torrent"));
        Assert.False(Exists($@"{Software}\Classes\Applications\{Path.GetFileName(Exe)}"));
        Assert.False(Exists($@"{Software}\Arr411"));
        Assert.Null(Value($@"{Software}\RegisteredApplications", "Arr411"));
        Assert.DoesNotContain("Arr411.Torrent", ValueNames($@"{Software}\Classes\.torrent\OpenWithProgids"));

        // Nothing left to remove is not a failure.
        Assert.Equal(0, await App.LaunchSecondAsync("--unregister"));
    }

    [Fact]
    public async Task The_unregister_command_also_stops_the_start_with_Windows()
    {
        try
        {
            App.Registration.SetStartWithWindows(true);
            Assert.True(App.Registration.StartsWithWindows);

            Assert.Equal(0, await App.LaunchSecondAsync("--unregister"));

            Assert.False(App.Registration.StartsWithWindows);
        }
        finally
        {
            App.Registration.SetStartWithWindows(false);
        }
    }

    [Fact]
    public async Task The_menu_registers_the_app_and_sends_the_user_to_the_default_apps_of_Windows()
    {
        try
        {
            await ChooseInMenuAsync(Associate);

            await Wait.UntilAsync(() => App.Registration.IsRegistered, "the app being registered");
            await Wait.UntilAsync(() => App.OpenedLinks().Contains(DefaultAppsLink), "the default apps of Windows being opened for the user");
            await App.WaitForWindowCountAsync(1);
            Assert.False(App.Registration.StartsWithWindows);
        }
        finally
        {
            await App.CloseMenusAsync();
            await App.LaunchSecondAsync("--unregister");
        }
    }

    [Fact]
    public async Task The_menu_starts_the_app_with_Windows_in_the_background_and_stops_it_again()
    {
        try
        {
            await ChooseInMenuAsync(StartWithWindows);

            await Wait.UntilAsync(() => App.Registration.StartsWithWindows, "the app being set to start with Windows");
            Assert.Equal($"\"{Exe}\" --background", Value($@"{Software}\Microsoft\Windows\CurrentVersion\Run", "Arr411"), ignoreCase: true);
            await App.WaitForWindowCountAsync(1);

            await ChooseInMenuAsync(StartWithWindows);

            await Wait.UntilAsync(() => !App.Registration.StartsWithWindows, "the app being set not to start with Windows any more");
        }
        finally
        {
            await App.CloseMenusAsync();
            App.Registration.SetStartWithWindows(false);
        }
    }
}
