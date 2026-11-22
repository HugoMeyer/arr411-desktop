using Arr411.Core.Update;

namespace Arr411.Core.Tests;

/// <summary>Handing the update to the applier, what the next start learns of the swap, the clean-up, and what the service remembers from one run to the next.</summary>
public sealed class UpdateApplyTests : IDisposable
{
    private readonly UpdateHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string[] Arguments(params string[] more) =>
        new[] { "--apply-update", "--target", _h.ExePath, "--wait-pid", "4242", "--from", "0.2.1" }.Concat(more).ToArray();

    // An update handed over, and an app that starts again without having heard from the applier.
    private async Task<UpdateService> BootAfterAnApplierThatNeverReportedAsync()
    {
        var first = await _h.BootReadyAsync();
        Assert.True(first.TryApplyAtStart(hasIntake: false, background: false));
        return _h.Reboot();
    }

    [Fact]
    public async Task A_ready_update_is_handed_to_the_applier_when_the_app_starts_with_nothing_to_open()
    {
        var service = await _h.BootReadyAsync();

        Assert.True(service.TryApplyAtStart(hasIntake: false, background: false));

        var started = Assert.Single(_h.Started);
        Assert.Equal(_h.StagedFile("0.3.0"), started.FileName);
        Assert.Equal(_h.UpdatesFolder, started.WorkingDirectory);
        Assert.False(started.UseShellExecute);
        Assert.True(started.CreateNoWindow);
        Assert.Equal(Arguments("--restart"), started.ArgumentList.ToArray());
        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.Single(_h.Started);
    }

    [Fact]
    public async Task What_was_handed_over_is_written_down_before_the_applier_starts()
    {
        var service = await _h.BootReadyAsync();
        ApplyAttempt? written = null;
        _h.OnStart = _ => written = _h.Record.Applying;

        service.TryApplyAtStart(hasIntake: false, background: false);

        Assert.Equal(new ApplyAttempt("0.3.0", _h.ExePath), written);
        Assert.Equal(written, _h.Record.Applying);
    }

    [Fact]
    public async Task A_quiet_start_is_followed_by_a_quiet_start()
    {
        var service = await _h.BootReadyAsync();

        Assert.True(service.TryApplyAtStart(hasIntake: false, background: true));

        Assert.Equal(Arguments("--restart", "--background"), Assert.Single(_h.Started).ArgumentList.ToArray());
    }

    [Fact]
    public async Task A_start_that_has_something_to_open_is_never_replaced()
    {
        var service = await _h.BootReadyAsync();

        Assert.False(service.TryApplyAtStart(hasIntake: true, background: false));

        Assert.Empty(_h.Started);
        Assert.Equal(UpdateStatus.Ready, service.State.Status);
    }

    [Fact]
    public async Task Quitting_hands_the_update_over_without_asking_for_a_restart()
    {
        var service = await _h.BootReadyAsync();

        Assert.True(service.TryApplyAtExit());

        Assert.Equal(Arguments(), Assert.Single(_h.Started).ArgumentList.ToArray());
        Assert.False(service.TryApplyAtExit());
        Assert.Single(_h.Started);
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("off")]
    public async Task An_update_is_applied_by_itself_only_in_the_automatic_mode(string mode)
    {
        var service = await _h.BootReadyAsync();

        _h.SetMode(mode);

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Started);
        Assert.Equal(UpdateStatus.Ready, service.State.Status);
    }

    [Fact]
    public async Task A_build_that_must_not_apply_updates_keeps_them_ready()
    {
        _h.AutoApply = false;
        var service = await _h.BootReadyAsync();

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Started);
        Assert.Equal(UpdateStatus.Ready, service.State.Status);
    }

    [Fact]
    public async Task A_folder_that_stopped_being_writable_stops_the_update_just_before_the_hand_over()
    {
        var service = await _h.BootReadyAsync();

        _h.CanWrite = false;

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Started);
    }

    [Fact]
    public async Task A_service_that_has_not_read_its_disk_applies_nothing()
    {
        (await _h.BootReadyAsync()).Dispose();
        var second = _h.Create();

        Assert.False(second.TryApplyAtStart(hasIntake: false, background: false));
        Assert.Empty(_h.Started);

        second.Initialize();

        Assert.True(second.TryApplyAtStart(hasIntake: false, background: false));
        Assert.Single(_h.Started);
    }

    [Fact]
    public void Nothing_is_applied_when_nothing_was_downloaded()
    {
        var service = _h.Boot();

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Empty(_h.Started);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_applier_that_could_not_be_started_is_reported_and_the_next_try_is_possible(bool throws)
    {
        var service = await _h.BootReadyAsync();
        _h.StartResult = false;
        _h.StartThrows = throws;

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.LaunchFailed, service.State.Error);
        Assert.Null(_h.Record.Applying);
        Assert.Equal("0.3.0", _h.Record.Staged?.Version);

        _h.StartResult = true;
        _h.StartThrows = false;

        Assert.True(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.Equal(new ApplyAttempt("0.3.0", _h.ExePath), _h.Record.Applying);
    }

    [Fact]
    public async Task A_staged_file_that_was_changed_on_disk_is_never_handed_over()
    {
        var service = await _h.BootReadyAsync();
        var staged = _h.StagedFile("0.3.0");
        var bytes = await File.ReadAllBytesAsync(staged);
        bytes[10] ^= 0xFF;
        await File.WriteAllBytesAsync(staged, bytes);

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));

        Assert.Empty(_h.Started);
        Assert.Equal(UpdateText.StagedChanged, service.State.Error);
        Assert.False(File.Exists(staged));
        Assert.Null(_h.Record.Staged);
        Assert.Null(_h.Record.Applying);

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Equal(_h.GitHub.Program, await File.ReadAllBytesAsync(staged));
        Assert.Equal(2, _h.GitHub.ProgramRequests);
        Assert.Single(_h.Notices);
    }

    [Fact]
    public async Task A_staged_file_that_is_not_the_size_it_was_is_dropped_at_the_next_start_and_downloaded_again()
    {
        await _h.BootReadyAsync();
        await File.AppendAllTextAsync(_h.StagedFile("0.3.0"), "x");

        var next = _h.Reboot();

        Assert.Equal(UpdateStatus.Idle, next.State.Status);
        Assert.True(next.State.Available);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.Null(_h.Record.Staged);

        await next.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, next.State.Status);
        Assert.Equal(2, _h.GitHub.ProgramRequests);
        Assert.Equal(_h.GitHub.Program, await File.ReadAllBytesAsync(_h.StagedFile("0.3.0")));
    }

    [Fact]
    public async Task The_user_can_install_a_release_the_app_only_announced()
    {
        _h.SetMode("notify");
        var service = _h.Boot();
        await service.CheckAsync(manual: false);
        Assert.Equal(0, _h.GitHub.ProgramRequests);

        await service.InstallAsync();

        Assert.Equal(1, _h.GitHub.ProgramRequests);
        Assert.Equal(Arguments("--restart"), Assert.Single(_h.Started).ArgumentList.ToArray());
        Assert.Equal(1, _h.RestartRequests);
        Assert.Equal(UpdateStatus.Installing, service.State.Status);
        Assert.Equal(new[] { UpdateNoticeKind.Available }, _h.Notices.Select(notice => notice.Kind).ToArray());
    }

    [Fact]
    public async Task An_update_that_is_ready_is_installed_without_downloading_it_again()
    {
        var service = await _h.BootReadyAsync();

        await service.InstallAsync();

        Assert.Equal(1, _h.GitHub.ProgramRequests);
        Assert.Single(_h.Started);
        Assert.Equal(1, _h.RestartRequests);
    }

    [Fact]
    public async Task The_installation_is_started_once()
    {
        var service = await _h.BootReadyAsync();
        await service.InstallAsync();

        await service.InstallAsync();

        Assert.Single(_h.Started);
        Assert.Equal(1, _h.RestartRequests);
        Assert.Equal(UpdateStatus.Installing, service.State.Status);
    }

    [Fact]
    public async Task There_is_nothing_to_install_when_nothing_newer_is_known()
    {
        var service = _h.Boot();

        await service.InstallAsync();

        Assert.Empty(_h.GitHub.Requests);

        _h.GitHub.Version = "0.2.1";
        await service.CheckAsync(manual: false);
        await service.InstallAsync();

        Assert.Empty(_h.Started);
        Assert.Equal(0, _h.RestartRequests);
        Assert.Null(service.State.Error);
    }

    [Fact]
    public async Task Installing_when_the_folder_stopped_being_writable_says_so_and_starts_nothing()
    {
        var service = await _h.BootReadyAsync();
        _h.CanWrite = false;

        await service.InstallAsync();

        Assert.Equal(UpdateText.CannotInstallHere, service.State.Error);
        Assert.Empty(_h.Started);
        Assert.Equal(0, _h.RestartRequests);
    }

    [Fact]
    public async Task An_update_handed_over_and_never_installed_is_reported_once()
    {
        var service = await BootAfterAnApplierThatNeverReportedAsync();

        var state = service.State;
        Assert.Equal(UpdateStatus.Error, state.Status);
        Assert.Equal(UpdateText.ApplyFailed("0.3.0"), state.Error);
        Assert.True(state.Available);
        Assert.True(state.CanInstall);
        Assert.Equal("0.3.0", _h.Record.Failed);
        Assert.Null(_h.Record.Applying);
        var notice = Assert.Single(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Failed);
        Assert.Equal("0.3.0", notice.Version);
        Assert.Equal("Mise à jour impossible", notice.Title);
        Assert.Equal(UpdateText.ApplyFailed("0.3.0"), notice.Text);

        var again = _h.Reboot();

        Assert.Equal(UpdateStatus.Ready, again.State.Status);
        Assert.Null(again.State.Error);
        Assert.Single(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Failed);
    }

    [Fact]
    public async Task An_update_that_failed_to_install_is_not_applied_again_by_itself_but_the_user_can_still_ask_for_it()
    {
        var service = await BootAfterAnApplierThatNeverReportedAsync();
        var handedOver = _h.Started.Count;

        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
        Assert.Equal(handedOver, _h.Started.Count);

        await service.InstallAsync();

        Assert.Equal(handedOver + 1, _h.Started.Count);
        Assert.Equal(1, _h.RestartRequests);
    }

    [Fact]
    public async Task A_check_of_the_schedule_does_not_hide_the_error_of_a_swap_that_failed()
    {
        var service = await BootAfterAnApplierThatNeverReportedAsync();

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Error, service.State.Status);
        Assert.Equal(UpdateText.ApplyFailed("0.3.0"), service.State.Error);
    }

    [Fact]
    public async Task A_check_the_user_asked_for_clears_the_error_of_a_swap_but_not_the_guard()
    {
        var service = await BootAfterAnApplierThatNeverReportedAsync();

        await service.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Null(service.State.Error);
        Assert.False(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.False(service.TryApplyAtExit());
    }

    [Fact]
    public async Task A_newer_release_than_the_one_that_failed_is_applied_as_usual()
    {
        var service = await BootAfterAnApplierThatNeverReportedAsync();
        _h.GitHub.Version = "0.4.0";
        _h.GitHub.Etag = "W/\"release-2\"";
        _h.GitHub.Program = FakeGitHub.Bytes(150 * 1024, 9);

        await service.CheckAsync(manual: false);

        Assert.Equal("0.4.0", service.State.Latest);
        Assert.Equal(UpdateStatus.Ready, service.State.Status);
        Assert.Null(service.State.Error);
        Assert.True(service.TryApplyAtStart(hasIntake: false, background: false));
        Assert.Equal(new ApplyAttempt("0.4.0", _h.ExePath), _h.Record.Applying);
    }

    [Fact]
    public async Task What_the_applier_says_is_shown_cleaned_and_only_once()
    {
        var service = await _h.BootReadyAsync();
        Assert.True(service.TryApplyAtStart(hasIntake: false, background: false));
        UpdateFiles.WriteError(_h.UpdatesFolder, "0.3.0", "<b>Fichier verrouillé</b>");

        var next = _h.Reboot();

        Assert.Equal("b Fichier verrouillé /b", next.State.Error);
        Assert.Equal("0.3.0", _h.Record.Failed);
        var notice = Assert.Single(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Failed);
        Assert.Equal("b Fichier verrouillé /b", notice.Text);
        Assert.False(File.Exists(_h.ErrorPath));

        var again = _h.Reboot();

        Assert.Null(again.State.Error);
        Assert.Single(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Failed);
    }

    [Fact]
    public void A_report_about_a_version_that_is_not_newer_than_the_running_one_is_ignored_and_removed()
    {
        Directory.CreateDirectory(_h.UpdatesFolder);
        UpdateFiles.WriteError(_h.UpdatesFolder, "0.2.0", "old news");

        var service = _h.Boot();

        Assert.Null(service.State.Error);
        Assert.Empty(_h.Notices);
        Assert.False(File.Exists(_h.ErrorPath));
    }

    [Fact]
    public async Task The_new_version_tells_the_user_it_was_updated_and_cleans_up_after_the_swap()
    {
        var service = await _h.BootReadyAsync();
        Assert.True(service.TryApplyAtStart(hasIntake: false, background: false));
        await File.WriteAllBytesAsync(_h.ExePath + ".old", UpdateHarness.OldProgram);

        var next = _h.Reboot(current: "0.3.0", updatedFrom: "0.2.1");

        var state = next.State;
        Assert.Null(state.Error);
        Assert.Equal(UpdateStatus.Idle, state.Status);
        Assert.Null(state.Latest);
        Assert.False(state.Available);
        var notice = Assert.Single(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Updated);
        Assert.Equal("0.3.0", notice.Version);
        Assert.Equal(UpdateText.UpdatedTitle, notice.Title);
        Assert.Equal(UpdateText.Updated("0.3.0"), notice.Text);
        Assert.DoesNotContain(_h.Notices, candidate => candidate.Kind == UpdateNoticeKind.Failed);
        Assert.False(File.Exists(_h.StagedFile("0.3.0")));
        Assert.False(File.Exists(_h.ExePath + ".old"));
        Assert.Null(_h.Record.Applying);
        Assert.Null(_h.Record.Staged);
        Assert.Null(_h.Record.Failed);
    }

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.4.0")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void The_user_is_not_told_of_an_update_that_did_not_happen(string? updatedFrom)
    {
        _h.Current = "0.3.0";

        _h.Boot(updatedFrom);

        Assert.Empty(_h.Notices);
    }

    [Fact]
    public void What_a_swap_leaves_behind_is_removed_when_the_app_starts()
    {
        var old = _h.ExePath + ".old";
        var copy = _h.ExePath + ".new";
        var stale = Path.Combine(_h.AppFolder, UpdateProbe.Prefix + "stale");
        var fresh = Path.Combine(_h.AppFolder, UpdateProbe.Prefix + "fresh");
        var download = Path.Combine(_h.UpdatesFolder, "Arr411-0.2.5.exe");
        var partial = Path.Combine(_h.UpdatesFolder, "Arr411-0.2.6.exe.part");
        Directory.CreateDirectory(_h.UpdatesFolder);
        foreach (var file in new[] { old, copy, stale, fresh, download, partial }) File.WriteAllText(file, "left behind");

        File.SetLastWriteTimeUtc(stale, _h.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(11));

        _h.Boot();

        Assert.All(new[] { old, copy, stale, download, partial }, file => Assert.False(File.Exists(file), file));
        Assert.True(File.Exists(fresh));
        Assert.Equal(UpdateHarness.OldProgram, File.ReadAllBytes(_h.ExePath));
    }

    [Fact]
    public void Nothing_is_removed_beside_a_program_that_is_not_the_app()
    {
        using var other = new UpdateHarness("dotnet.exe");
        var old = other.ExePath + ".old";
        File.WriteAllText(old, "not ours");

        other.Boot();

        Assert.True(File.Exists(old));
    }

    [Fact]
    public async Task A_file_that_is_still_in_use_is_removed_a_little_later()
    {
        var old = _h.ExePath + ".old";
        await File.WriteAllTextAsync(old, "old");
        var held = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _h.Boot();

            Assert.True(File.Exists(old));
            var retry = Assert.Single(_h.Clock.LiveTimers, timer => timer.DueTime == TimeSpan.FromSeconds(10));
            Assert.Equal(Timeout.InfiniteTimeSpan, retry.Period);

            held.Dispose();
            retry.Fire();

            Assert.False(File.Exists(old));
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task A_downloaded_update_survives_a_restart_of_the_app()
    {
        await _h.BootReadyAsync();

        var next = _h.Reboot();

        var state = next.State;
        Assert.Equal(UpdateStatus.Ready, state.Status);
        Assert.Equal("0.3.0", state.Latest);
        Assert.True(state.CanInstall);
        Assert.NotNull(state.CheckedAt);

        await next.CheckAsync(manual: false);

        Assert.Equal(1, _h.GitHub.ProgramRequests);
        Assert.Equal("W/\"release-1\"", _h.GitHub.Requests.Last(request => request.Target == FakeGitHub.ApiAddress).Header("If-None-Match"));
    }

    [Fact]
    public async Task What_another_version_learned_of_the_releases_is_forgotten_but_the_download_is_kept()
    {
        await _h.BootReadyAsync();

        var next = _h.Reboot(current: "0.2.2");

        Assert.Null(next.State.Latest);
        Assert.Equal(UpdateStatus.Idle, next.State.Status);

        await next.CheckAsync(manual: false);

        Assert.Null(_h.GitHub.Requests.Last(request => request.Target == FakeGitHub.ApiAddress).Header("If-None-Match"));
        Assert.Equal(UpdateStatus.Ready, next.State.Status);
        Assert.Equal(1, _h.GitHub.ProgramRequests);
    }

    [Fact]
    public async Task A_state_file_that_is_not_readable_is_a_blank_start()
    {
        Directory.CreateDirectory(_h.UpdatesFolder);
        await File.WriteAllTextAsync(_h.StatePath, "{ this is not json");

        var service = _h.Boot();

        Assert.Equal(UpdateStatus.Idle, service.State.Status);
        Assert.Null(service.State.Latest);
        Assert.Null(service.State.Error);
    }
}
