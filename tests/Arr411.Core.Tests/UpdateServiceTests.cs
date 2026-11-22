using System.Net;
using Arr411.Core.Update;

namespace Arr411.Core.Tests;

/// <summary>Looking for a release, following the settings, and what the user is told (the download itself is in <see cref="UpdateDownloadTests"/>).</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly UpdateHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void A_new_service_knows_nothing_and_asks_nothing()
    {
        var state = _h.Create().State;

        Assert.True(state.Supported);
        Assert.Equal("0.2.1", state.Current);
        Assert.Null(state.Latest);
        Assert.False(state.Available);
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.Null(state.Progress);
        Assert.Equal("", state.Notes);
        Assert.Equal("https://github.com/HugoMeyer/arr411-desktop/releases/latest", state.Url);
        Assert.Null(state.CheckedAt);
        Assert.False(state.CanInstall);
        Assert.Null(state.Error);
        Assert.Equal("auto", state.Mode);
        Assert.Empty(_h.GitHub.Requests);
    }

    [Fact]
    public void Starting_publishes_the_state_once_and_asks_nothing_before_the_first_check()
    {
        _h.Boot();

        Assert.Single(_h.States);
        Assert.Empty(_h.GitHub.Requests);
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_and_checked_in_the_automatic_mode()
    {
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        var state = service.State;
        Assert.Equal(UpdateStatus.Ready, state.Status);
        Assert.Equal("0.3.0", state.Latest);
        Assert.True(state.Available);
        Assert.True(state.CanInstall);
        Assert.Null(state.Progress);
        Assert.Null(state.Error);
        Assert.Equal("Corrections et améliorations.", state.Notes);
        Assert.Equal(_h.GitHub.PageAddress, state.Url);
        Assert.Equal(_h.Clock.GetUtcNow().ToUnixTimeMilliseconds(), state.CheckedAt);
        Assert.Equal(_h.GitHub.Program, await File.ReadAllBytesAsync(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
        Assert.Equal(UpdateHarness.OldProgram, await File.ReadAllBytesAsync(_h.ExePath));
        Assert.Empty(_h.Started);
    }

    [Fact]
    public async Task The_requests_go_to_the_release_and_to_nothing_else()
    {
        await _h.Boot().CheckAsync(manual: false);

        Assert.Equal(new[] { FakeGitHub.ApiAddress, _h.GitHub.ProgramAddress, FakeGitHub.ProgramCdn }, _h.GitHub.Targets);
    }

    [Fact]
    public async Task The_sums_file_of_the_release_is_fetched_before_the_program()
    {
        _h.GitHub.Sums = FakeGitHub.SumsFor(_h.GitHub.Program);

        await _h.Boot().CheckAsync(manual: false);

        var expected = new[] { FakeGitHub.ApiAddress, _h.GitHub.SumsAddress, FakeGitHub.SumsCdn, _h.GitHub.ProgramAddress, FakeGitHub.ProgramCdn };
        Assert.Equal(expected, _h.GitHub.Targets);
    }

    [Fact]
    public async Task The_questions_to_GitHub_say_who_asks_and_carry_no_credentials()
    {
        await _h.Boot().CheckAsync(manual: false);

        var api = _h.GitHub.Requests[0];
        Assert.Equal("application/vnd.github+json", api.Header("Accept"));
        Assert.Equal("2022-11-28", api.Header("X-GitHub-Api-Version"));
        Assert.Null(api.Header("If-None-Match"));
        Assert.All(_h.GitHub.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("Arr411-Desktop/0.2.1", request.Header("User-Agent"));
            Assert.Null(request.Header("Authorization"));
            Assert.Null(request.Header("Cookie"));
        });
        Assert.All(_h.GitHub.Requests.Skip(1), request => Assert.Equal("application/octet-stream", request.Header("Accept")));
    }

    [Fact]
    public async Task The_state_goes_from_checking_to_downloading_with_a_growing_progress_to_ready()
    {
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        var states = _h.States;
        var statuses = states.Select(state => state.Status).Distinct().ToArray();
        Assert.Equal(new[] { UpdateStatus.Idle, UpdateStatus.Checking, UpdateStatus.Downloading, UpdateStatus.Ready }, statuses);
        for (var i = 1; i < states.Count; i++) Assert.NotEqual(states[i - 1], states[i]);

        var progress = states.Where(state => state.Status == UpdateStatus.Downloading).Select(state => state.Progress!.Value).ToArray();
        Assert.Equal(0, progress[0]);
        Assert.Equal(1, progress[^1]);
        Assert.Equal(progress.OrderBy(value => value), progress);
        Assert.All(states.Where(state => state.Status != UpdateStatus.Downloading), state => Assert.Null(state.Progress));
    }

    [Fact]
    public async Task The_user_is_told_once_that_the_update_is_ready_when_nobody_asked_for_the_check()
    {
        var service = _h.Boot();

        await service.CheckAsync(manual: false);
        await service.CheckAsync(manual: false);

        var notice = Assert.Single(_h.Notices);
        Assert.Equal(UpdateNoticeKind.Ready, notice.Kind);
        Assert.Equal("0.3.0", notice.Version);
        Assert.Equal("Mise à jour prête", notice.Title);
        Assert.Equal(UpdateText.Ready("0.3.0"), notice.Text);
        Assert.Equal(1, _h.GitHub.ProgramRequests);
    }

    [Fact]
    public async Task A_check_the_user_asked_for_downloads_without_a_notice()
    {
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task The_notify_mode_tells_the_user_once_and_downloads_nothing()
    {
        _h.SetMode("notify");
        var service = _h.Boot();

        await service.CheckAsync(manual: false);
        await service.CheckAsync(manual: false);

        var state = service.State;
        Assert.Equal("notify", state.Mode);
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.True(state.Available);
        Assert.True(state.CanInstall);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        var notice = Assert.Single(_h.Notices);
        Assert.Equal(UpdateNoticeKind.Available, notice.Kind);
        Assert.Equal("Mise à jour disponible", notice.Title);
        Assert.Equal(UpdateText.Available("0.3.0"), notice.Text);
    }

    [Fact]
    public async Task The_notice_of_an_available_version_is_not_repeated_after_a_restart()
    {
        _h.SetMode("notify");
        await _h.Boot().CheckAsync(manual: false);

        await _h.Reboot().CheckAsync(manual: false);

        Assert.Single(_h.Notices);
    }

    [Fact]
    public async Task A_check_the_user_asked_for_in_the_notify_mode_does_not_notify()
    {
        _h.SetMode("notify");
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.True(service.State.Available);
        Assert.Empty(_h.Notices);
    }

    [Theory]
    [InlineData("0.2.1")]
    [InlineData("0.2.1.0")]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    public async Task A_release_that_is_not_newer_leaves_the_app_up_to_date(string version)
    {
        _h.GitHub.Version = version;
        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        var state = service.State;
        Assert.Equal(UpdateStatus.UpToDate, state.Status);
        Assert.False(state.Available);
        Assert.NotNull(state.CheckedAt);
        Assert.Null(state.Error);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Empty(_h.Notices);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("tag")]
    public async Task A_draft_a_prerelease_or_an_odd_tag_is_nothing_to_offer(string kind)
    {
        switch (kind)
        {
            case "draft":
                _h.GitHub.Draft = true;
                break;
            case "prerelease":
                _h.GitHub.Prerelease = true;
                break;
            default:
                _h.GitHub.Tag = "nightly";
                break;
        }

        var service = _h.Boot();

        await service.CheckAsync(manual: false);

        var state = service.State;
        Assert.Equal(UpdateStatus.UpToDate, state.Status);
        Assert.Null(state.Latest);
        Assert.False(state.Available);
        Assert.NotNull(state.CheckedAt);
        Assert.Null(state.Error);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task The_second_check_asks_with_the_etag_and_an_answer_of_not_modified_changes_nothing_but_the_time()
    {
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        var first = service.State;

        _h.Clock.Advance(TimeSpan.FromHours(6));
        await service.CheckAsync(manual: false);

        var latestQuestion = _h.GitHub.Requests.Last(request => request.Target == FakeGitHub.ApiAddress);
        Assert.Equal("W/\"release-1\"", latestQuestion.Header("If-None-Match"));
        Assert.Equal(2, _h.GitHub.ApiRequests);
        Assert.Equal(1, _h.GitHub.ProgramRequests);
        Assert.Equal(first with { CheckedAt = first.CheckedAt + (long)TimeSpan.FromHours(6).TotalMilliseconds }, service.State);
    }

    [Fact]
    public async Task A_newer_release_replaces_the_one_that_was_downloaded()
    {
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        Assert.True(File.Exists(_h.StagedFile("0.3.0")));

        _h.GitHub.Version = "0.4.0";
        _h.GitHub.Etag = "W/\"release-2\"";
        _h.GitHub.Program = FakeGitHub.Bytes(150 * 1024, 9);
        await service.CheckAsync(manual: false);

        Assert.Equal("0.4.0", service.State.Latest);
        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Equal(_h.GitHub.Program, await File.ReadAllBytesAsync(_h.StagedFile("0.4.0")));
        Assert.Equal(new[] { "0.3.0", "0.4.0" }, _h.Notices.Select(notice => notice.Version).ToArray());
        Assert.All(_h.Notices, notice => Assert.Equal(UpdateNoticeKind.Ready, notice.Kind));
    }

    [Fact]
    public async Task An_answer_of_not_modified_with_nothing_kept_is_an_error()
    {
        _h.GitHub.Override = request => request.Target == FakeGitHub.ApiAddress ? StubHandler.Reply(HttpStatusCode.NotModified) : null;
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.UnreadableAnswer, service.State.Error);
    }

    [Fact]
    public async Task The_user_cannot_make_the_app_ask_twice_within_a_minute()
    {
        _h.GitHub.Version = "0.2.1";
        var service = _h.Boot();
        await service.CheckAsync(manual: true);
        var before = service.State;
        var published = _h.States.Count;

        _h.Clock.Advance(TimeSpan.FromSeconds(59));
        await service.CheckAsync(manual: true);

        Assert.Equal(1, _h.GitHub.ApiRequests);
        Assert.Equal(before, service.State);
        Assert.Equal(published + 1, _h.States.Count);
        Assert.Equal(before, _h.States[^1]);

        _h.Clock.Advance(TimeSpan.FromSeconds(2));
        await service.CheckAsync(manual: true);

        Assert.Equal(2, _h.GitHub.ApiRequests);
    }

    [Fact]
    public async Task The_checks_of_the_schedule_are_not_limited_by_the_cooldown()
    {
        _h.GitHub.Version = "0.2.1";
        var service = _h.Boot();

        await service.CheckAsync(manual: true);
        await service.CheckAsync(manual: false);
        await service.CheckAsync(manual: false);

        Assert.Equal(3, _h.GitHub.ApiRequests);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task An_answer_that_says_too_many_questions_is_told_as_such(int status)
    {
        _h.GitHub.ApiStatus = status;
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.RateLimited, service.State.Error);
        Assert.Null(service.State.CheckedAt);
    }

    [Fact]
    public async Task A_repository_without_release_is_told_as_such()
    {
        _h.GitHub.ApiStatus = 404;
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateText.NoRelease, service.State.Error);
    }

    [Fact]
    public async Task Any_other_answer_is_told_with_its_number()
    {
        _h.GitHub.ApiStatus = 500;
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateText.HttpError(500), service.State.Error);
        Assert.Contains("500", service.State.Error);
    }

    [Fact]
    public async Task A_network_that_cannot_be_reached_is_told_as_such()
    {
        _h.GitHub.ApiThrows = new HttpRequestException("no route to host");
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.CannotReach, service.State.Error);
    }

    [Fact]
    public async Task A_GitHub_that_does_not_answer_in_time_is_told_as_such()
    {
        _h.ApiTimeout = TimeSpan.FromMilliseconds(300);
        _h.GitHub.HangApi = true;
        var service = _h.Boot();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.NoAnswer, service.State.Error);
    }

    [Fact]
    public async Task The_next_check_that_works_clears_the_error()
    {
        _h.GitHub.ApiStatus = 500;
        var service = _h.Boot();
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Error, service.State.Status);

        _h.GitHub.ApiStatus = 200;
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Null(service.State.Error);
    }

    [Fact]
    public async Task A_check_of_the_schedule_that_fails_is_shown_when_nothing_is_ready()
    {
        var service = _h.Boot();
        _h.GitHub.ApiStatus = 500;

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.HttpError(500), service.State.Error);
    }

    [Fact]
    public async Task A_check_of_the_schedule_that_fails_is_not_shown_while_an_update_is_ready()
    {
        var service = await _h.BootReadyAsync();
        _h.GitHub.ApiStatus = 500;

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Null(service.State.Error);
    }

    [Fact]
    public async Task A_check_the_user_asked_for_that_fails_is_shown_even_while_an_update_is_ready()
    {
        var service = await _h.BootReadyAsync();
        _h.GitHub.ApiStatus = 500;

        await service.CheckAsync(manual: true);

        var state = service.State;
        Assert.Equal(UpdateStatus.Error, state.Status);
        Assert.Equal(UpdateText.HttpError(500), state.Error);
        Assert.Equal("0.3.0", state.Latest);
        Assert.True(state.Available);
        Assert.True(state.CanInstall);
    }

    [Fact]
    public void The_first_check_is_scheduled_for_thirty_seconds_and_then_every_six_hours()
    {
        _h.Boot();

        var timer = Assert.Single(_h.Clock.LiveTimers);
        Assert.Equal(TimeSpan.FromSeconds(30), timer.DueTime);
        Assert.Equal(TimeSpan.FromHours(6), timer.Period);
    }

    [Fact]
    public void Starting_twice_does_not_schedule_twice()
    {
        var service = _h.Boot();

        service.Start();
        service.Start("0.1.0");

        Assert.Single(_h.Clock.Timers);
    }

    [Fact]
    public async Task The_timer_makes_a_check_that_downloads_and_tells_the_user()
    {
        _h.Boot();

        _h.Clock.LiveTimers[0].Fire();

        await Wait.Until(() => _h.Notices.Count == 1);
        Assert.Equal(UpdateStatus.Ready, _h.Service!.State.Status);
        Assert.Equal(UpdateNoticeKind.Ready, _h.Notices[0].Kind);
    }

    [Fact]
    public void Turning_updates_off_stops_the_timer_and_turning_them_on_again_starts_one()
    {
        var service = _h.Boot();

        _h.SetMode("off");

        Assert.Empty(_h.Clock.LiveTimers);
        Assert.Equal("off", service.State.Mode);

        _h.SetMode("notify");

        Assert.Single(_h.Clock.LiveTimers);
        Assert.Equal(2, _h.Clock.Timers.Count);
        Assert.Equal("notify", service.State.Mode);
    }

    [Fact]
    public async Task In_the_off_mode_nothing_is_scheduled_but_the_user_can_still_check()
    {
        _h.SetMode("off");
        var service = _h.Boot();
        Assert.Empty(_h.Clock.Timers);

        await service.CheckAsync(manual: true);

        var state = service.State;
        Assert.Equal("off", state.Mode);
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.True(state.Available);
        Assert.True(state.CanInstall);
        Assert.Equal(0, _h.GitHub.ProgramRequests);
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task In_the_off_mode_an_update_is_installed_only_on_request()
    {
        _h.SetMode("off");
        var service = _h.Boot();
        await service.CheckAsync(manual: true);

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Started);

        await service.InstallAsync();

        var started = Assert.Single(_h.Started);
        Assert.Contains("--restart", started.ArgumentList);
        Assert.Equal(1, _h.RestartRequests);
    }

    [Fact]
    public async Task Switching_to_the_automatic_mode_downloads_the_release_that_is_known()
    {
        _h.SetMode("notify");
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        Assert.Equal(0, _h.GitHub.ProgramRequests);

        _h.SetMode("auto");

        await Wait.Until(() => service.State.Status == UpdateStatus.Ready);
        Assert.Equal(1, _h.GitHub.ApiRequests);
        Assert.Equal(_h.GitHub.Program, await File.ReadAllBytesAsync(_h.StagedFile("0.3.0")));
        Assert.Equal(new[] { UpdateNoticeKind.Available }, _h.Notices.Select(notice => notice.Kind).ToArray());
    }

    [Fact]
    public async Task Switching_away_from_the_automatic_mode_stops_a_download_nobody_asked_for()
    {
        _h.GitHub.NeverAnswerDownload = true;
        var service = _h.Boot();
        var check = service.CheckAsync(manual: false);
        await Wait.Until(() => _h.GitHub.Targets.Contains(FakeGitHub.ProgramCdn));
        Assert.Equal(UpdateStatus.Downloading, service.State.Status);

        _h.SetMode("notify");
        await check;

        var state = service.State;
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.Null(state.Error);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Empty(_h.Partials);
    }

    [Fact]
    public async Task Switching_away_from_the_automatic_mode_does_not_stop_a_download_the_user_asked_for()
    {
        _h.SetMode("notify");
        _h.GitHub.NeverAnswerDownload = true;
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        var install = service.InstallAsync();
        await Wait.Until(() => _h.GitHub.Targets.Contains(FakeGitHub.ProgramCdn));

        _h.SetMode("off");
        await Task.Delay(200);

        Assert.False(install.IsCompleted);
        Assert.Equal(UpdateStatus.Downloading, service.State.Status);

        service.Dispose();
        await install;
        Assert.Equal(0, _h.RestartRequests);
    }

    [Fact]
    public async Task A_disabled_service_does_nothing_at_all()
    {
        _h.Disabled = true;
        var service = _h.Boot();

        service.CheckNow();
        service.Install();
        await service.CheckAsync(manual: true);
        await service.CheckAsync(manual: false);
        await service.InstallAsync();
        await Task.Delay(100);

        var state = service.State;
        Assert.False(state.Supported);
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.False(state.CanInstall);
        Assert.Single(_h.States);
        Assert.Empty(_h.Clock.Timers);
        Assert.Empty(_h.GitHub.Requests);
        Assert.False(Directory.Exists(_h.UpdatesFolder));
        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task A_check_while_another_one_runs_is_dropped()
    {
        _h.GitHub.HangApi = true;
        var service = _h.Boot();
        var first = service.CheckAsync(manual: true);
        await Wait.Until(() => _h.GitHub.ApiRequests == 1);

        await service.CheckAsync(manual: false);
        var published = _h.States.Count;
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAsync(manual: true);

        Assert.Equal(1, _h.GitHub.ApiRequests);
        Assert.Equal(published + 1, _h.States.Count);
        Assert.Equal(UpdateStatus.Checking, service.State.Status);

        service.Dispose();
        await first;
    }

    [Fact]
    public async Task CheckNow_and_Install_return_at_once_and_do_the_work_behind()
    {
        _h.SetMode("notify");
        var service = _h.Boot();

        service.CheckNow();
        await Wait.Until(() => service.State.Available && service.State.Status == UpdateStatus.Idle);

        service.Install();
        await Wait.Until(() => _h.RestartRequests == 1);

        Assert.Equal(UpdateStatus.Installing, service.State.Status);
        Assert.Single(_h.Started);
    }

    [Fact]
    public async Task A_service_that_was_disposed_does_nothing_more()
    {
        var service = _h.Boot();
        service.Dispose();

        await service.CheckAsync(manual: true);
        await service.InstallAsync();
        _h.SetMode("notify");
        service.Dispose();

        Assert.Empty(_h.GitHub.Requests);
        Assert.Empty(_h.Clock.LiveTimers);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_stop_the_others_or_the_update()
    {
        var service = _h.Boot(subscribeFirst: first =>
        {
            first.StateChanged += (_, _) => throw new InvalidOperationException("a listener that fails");
            first.Notice += (_, _) => throw new InvalidOperationException("a listener that fails");
            first.RestartRequested += (_, _) => throw new InvalidOperationException("a listener that fails");
        });

        await service.CheckAsync(manual: false);
        await service.InstallAsync();

        Assert.Contains(_h.States, state => state.Status == UpdateStatus.Ready);
        Assert.Contains(_h.States, state => state.Status == UpdateStatus.Installing);
        Assert.Single(_h.Started);
    }
}
