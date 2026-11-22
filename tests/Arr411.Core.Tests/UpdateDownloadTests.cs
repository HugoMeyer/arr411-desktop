using Arr411.Core.Update;

namespace Arr411.Core.Tests;

/// <summary>Where the program may come from and what has to be true of it before it is kept.</summary>
public sealed class UpdateDownloadTests : IDisposable
{
    private readonly UpdateHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("https://evil.example/Arr411.exe")]
    [InlineData("http://release-assets.githubusercontent.com/Arr411.exe")]
    [InlineData("https://release-assets.githubusercontent.com:8443/Arr411.exe")]
    [InlineData("https://user:secret@release-assets.githubusercontent.com/Arr411.exe")]
    [InlineData("https://github.com.evil.example/Arr411.exe")]
    [InlineData("https://raw.githubusercontent.com/HugoMeyer/arr411-desktop/main/Arr411.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://github.com/Arr411.exe")]
    public async Task A_redirect_to_anywhere_but_GitHub_is_refused_without_a_request(string address)
    {
        _h.GitHub.AssetRedirect = address;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(new[] { FakeGitHub.ApiAddress, _h.GitHub.ProgramAddress }, _h.GitHub.Targets);
        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.Refused, service.State.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
    }

    [Fact]
    public async Task A_redirect_with_a_relative_address_is_followed_on_the_same_host()
    {
        _h.GitHub.AssetRedirect = "/hop/1";
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        var expected = new[] { FakeGitHub.ApiAddress, _h.GitHub.ProgramAddress, "https://github.com/hop/1", FakeGitHub.ProgramCdn };
        Assert.Equal(expected, _h.GitHub.Targets);
    }

    [Fact]
    public async Task Five_redirects_are_followed()
    {
        _h.GitHub.Hops = 4;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Equal(7, _h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task A_sixth_redirect_is_one_too_many()
    {
        _h.GitHub.Hops = 5;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.TooManyRedirects, service.State.Error);
        Assert.Equal(7, _h.GitHub.Requests.Count);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
    }

    [Fact]
    public async Task A_redirect_without_a_destination_is_a_failed_download()
    {
        _h.GitHub.Override = request => request.Target == _h.GitHub.ProgramAddress ? StubHandler.Reply(System.Net.HttpStatusCode.Found) : null;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.DownloadFailed, service.State.Error);
    }

    [Fact]
    public async Task A_release_whose_program_lives_elsewhere_is_only_announced()
    {
        _h.GitHub.ProgramUrl = "https://evil.example/Arr411.exe";
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        var state = service.State;
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.True(state.Available);
        Assert.False(state.CanInstall);
        Assert.Equal(UpdateNoticeKind.Available, Assert.Single(_h.Notices).Kind);

        await service.InstallAsync();

        Assert.Equal(UpdateText.CannotInstallHere, service.State.Error);
        Assert.Single(_h.GitHub.Requests);
        Assert.Empty(_h.Started);
        Assert.Equal(0, _h.RestartRequests);
    }

    [Fact]
    public async Task A_download_that_is_not_the_announced_program_is_refused_and_not_tried_again_by_itself()
    {
        var tampered = (byte[])_h.GitHub.Program.Clone();
        tampered[1000] ^= 0xFF;
        _h.GitHub.Served = tampered;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.WrongHash, service.State.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);

        await service.CheckAsync(manual: false);

        Assert.Equal(1, _h.GitHub.ProgramRequests);

        await service.InstallAsync();

        Assert.Equal(2, _h.GitHub.ProgramRequests);
        Assert.Equal(UpdateText.WrongHash, service.State.Error);
        Assert.Empty(_h.Started);
        Assert.Equal(0, _h.RestartRequests);

        _h.GitHub.Served = null;
        await service.InstallAsync();

        Assert.Single(_h.Started);
        Assert.Equal(1, _h.RestartRequests);
    }

    [Fact]
    public async Task A_release_whose_digest_and_sums_file_disagree_is_refused_before_the_program_is_asked_for()
    {
        _h.GitHub.Sums = FakeGitHub.SumsFor(FakeGitHub.Bytes(1000, 3));
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.WrongHash, service.State.Error);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Equal(new[] { FakeGitHub.ApiAddress, _h.GitHub.SumsAddress, FakeGitHub.SumsCdn }, _h.GitHub.Targets);
    }

    [Fact]
    public async Task A_release_with_only_a_sums_file_is_verified_by_it()
    {
        _h.GitHub.HasDigest = false;
        _h.GitHub.Sums = $"{FakeGitHub.HashOf(_h.GitHub.Program)} *Arr411.exe\r\n";
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        var expected = new[] { FakeGitHub.ApiAddress, _h.GitHub.SumsAddress, FakeGitHub.SumsCdn, _h.GitHub.ProgramAddress, FakeGitHub.ProgramCdn };
        Assert.Equal(expected, _h.GitHub.Targets);
    }

    [Fact]
    public async Task A_program_that_is_not_the_one_of_the_sums_file_is_refused()
    {
        _h.GitHub.HasDigest = false;
        _h.GitHub.Sums = FakeGitHub.SumsFor(FakeGitHub.Bytes(1000, 3));
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.WrongHash, service.State.Error);
        Assert.Equal(1, _h.GitHub.ProgramRequests);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
    }

    [Fact]
    public async Task A_sums_file_with_no_line_for_the_program_is_unusable()
    {
        _h.GitHub.Sums = FakeGitHub.SumsFor(_h.GitHub.Program, "Other.exe");
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.SumsUnusable, service.State.Error);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task A_sums_file_that_cannot_be_fetched_stops_the_download(int status)
    {
        _h.GitHub.Sums = FakeGitHub.SumsFor(_h.GitHub.Program);
        _h.GitHub.SumsStatus = status;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(status is 403 or 429 ? UpdateText.RateLimited : UpdateText.DownloadFailed, service.State.Error);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
    }

    [Fact]
    public async Task A_release_with_nothing_to_verify_the_program_by_cannot_be_installed()
    {
        _h.GitHub.HasDigest = false;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.False(service.State.CanInstall);
        Assert.True(service.State.Available);
        Assert.Equal(UpdateNoticeKind.Available, Assert.Single(_h.Notices).Kind);
        Assert.Equal(0, _h.GitHub.ProgramRequests);

        await service.InstallAsync();

        Assert.Equal(UpdateText.CannotInstallHere, service.State.Error);
        Assert.Empty(_h.Started);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_download_that_is_not_the_announced_size_is_refused(bool shorter, bool announcesItsLength)
    {
        var program = _h.GitHub.Program;
        _h.GitHub.Served = shorter ? program[..^1] : program.Append((byte)0).ToArray();
        _h.GitHub.ServeWithoutLength = !announcesItsLength;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.WrongSize, service.State.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(209_715_201L)]
    public async Task A_release_that_announces_an_impossible_size_cannot_be_installed(long size)
    {
        _h.GitHub.Size = size;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.True(service.State.Available);
        Assert.False(service.State.CanInstall);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Equal(UpdateNoticeKind.Available, Assert.Single(_h.Notices).Kind);
    }

    [Fact]
    public async Task A_folder_the_app_cannot_write_to_means_no_automatic_update_but_a_notice()
    {
        _h.CanWrite = false;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.False(service.State.CanInstall);
        Assert.True(service.State.Available);
        Assert.Equal(UpdateStatus.Idle, service.State.Status);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Equal(UpdateNoticeKind.Available, Assert.Single(_h.Notices).Kind);

        _h.CanWrite = true;
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Equal(new[] { UpdateNoticeKind.Available, UpdateNoticeKind.Ready }, _h.Notices.Select(notice => notice.Kind).ToArray());
    }

    [Fact]
    public async Task A_program_that_is_not_the_app_is_never_replaced()
    {
        using var other = new UpdateHarness("dotnet.exe");
        var service = other.Boot();

        await service.CheckAsync(manual: false);

        Assert.False(service.State.CanInstall);
        Assert.True(service.State.Available);
        Assert.Equal(UpdateNoticeKind.Available, Assert.Single(other.Notices).Kind);
        Assert.Equal(0, other.GitHub.ProgramRequests);

        await service.InstallAsync();

        Assert.Equal(UpdateText.CannotInstallHere, service.State.Error);
        Assert.Empty(other.Started);
    }

    [Fact]
    public async Task A_download_that_goes_silent_is_given_up_and_tried_again_later()
    {
        _h.StallTimeout = TimeSpan.FromMilliseconds(300);
        _h.GitHub.StallDownloadBody = true;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.Stalled, service.State.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);

        _h.GitHub.StallDownloadBody = false;
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Equal(2, _h.GitHub.ProgramRequests);
    }

    [Fact]
    public async Task A_connection_that_breaks_during_the_download_leaves_nothing_behind()
    {
        _h.GitHub.BreakDownloadBody = true;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateText.DownloadFailed, service.State.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
    }

    [Fact]
    public async Task A_download_that_fails_for_a_reason_that_may_pass_is_tried_again_by_the_next_check()
    {
        _h.GitHub.AssetStatus = 503;
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateText.DownloadFailed, service.State.Error);

        _h.GitHub.AssetStatus = 200;
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Equal(2, _h.GitHub.ProgramRequests);
    }
}
