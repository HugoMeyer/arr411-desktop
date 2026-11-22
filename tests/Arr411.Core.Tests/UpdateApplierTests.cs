using System.ComponentModel;
using System.Diagnostics;
using Arr411.Core.Update;

namespace Arr411.Core.Tests;

/// <summary>The half of the update that runs once the app has ended: the swap of the files, and what is done when the swap cannot be done.</summary>
public sealed class UpdateApplierTests : IDisposable
{
    private static readonly byte[] OldBytes = FakeGitHub.Bytes(4096, 1);
    private static readonly byte[] NewBytes = FakeGitHub.Bytes(8192, 2);

    private readonly TempDir _dir = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<TimeSpan> _delays = [];
    private readonly List<int> _waits = [];

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        Directory.CreateDirectory(Updates);
        File.WriteAllBytes(Target, OldBytes);
        File.WriteAllBytes(Self, NewBytes);
    }

    private string Target => Path.Combine(_dir.Root, "app", "Arr411.exe");

    private string Updates => Path.Combine(_dir.Root, "data", "updates");

    private string Self => Path.Combine(Updates, "Arr411-0.3.0.exe");

    public void Dispose() => _dir.Dispose();

    private string[] Args(params string[] more) =>
        new[] { "--apply-update", "--target", Target, "--wait-pid", "4242", "--from", "0.2.1" }.Concat(more).ToArray();

    private ApplyEnvironment Env(Func<int, TimeSpan, bool>? waitForExit = null, Func<ProcessStartInfo, bool>? start = null, Action<TimeSpan>? delay = null) => new()
    {
        SelfPath = Self,
        UpdatesFolder = Updates,
        Version = "0.3.0",
        WaitForExit = waitForExit ?? ((id, _) =>
        {
            _waits.Add(id);
            return true;
        }),
        Delay = delay ?? (span => _delays.Add(span)),
        Start = start ?? (info =>
        {
            _started.Add(info);
            return true;
        }),
        RetryDelay = TimeSpan.FromMilliseconds(250),
    };

    private (string? Version, string Message) Report() =>
        UpdateFiles.TakeError(Updates) ?? throw new InvalidOperationException("the applier left no report");

    private void Record(UpdateRecord record) => UpdateFiles.Save(Updates, record.ToJson());

    [Fact]
    public void The_new_program_takes_the_place_of_the_old_one_which_is_kept_beside_it()
    {
        var code = UpdateApplier.Run(Args(), Env());

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
        Assert.Equal(OldBytes, File.ReadAllBytes(Target + ".old"));
        Assert.False(File.Exists(Target + ".new"));
        Assert.True(File.Exists(Self));
        Assert.Equal(new[] { 4242 }, _waits);
        Assert.Empty(_started);
        Assert.Empty(_delays);
        Assert.False(File.Exists(Path.Combine(Updates, "last-error.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_new_version_is_started_where_the_old_one_was_when_the_app_asked_for_it(bool background)
    {
        var code = UpdateApplier.Run(background ? Args("--restart", "--background") : Args("--restart"), Env());

        Assert.Equal(0, code);
        var info = Assert.Single(_started);
        Assert.Equal(Target, info.FileName);
        Assert.Equal(Path.GetDirectoryName(Target), info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        var expected = background ? new[] { "--updated-from", "0.2.1", "--background" } : new[] { "--updated-from", "0.2.1" };
        Assert.Equal(expected, info.ArgumentList.ToArray());
    }

    [Fact]
    public void A_restart_without_a_known_origin_carries_no_origin()
    {
        var code = UpdateApplier.Run(["--apply-update", "--target", Target, "--wait-pid", "4242", "--restart"], Env());

        Assert.Equal(0, code);
        Assert.Empty(Assert.Single(_started).ArgumentList);
    }

    [Fact]
    public void An_app_that_has_already_ended_is_not_waited_for()
    {
        int id;
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            process.WaitForExit();
            id = process.Id;
        }

        var env = new ApplyEnvironment
        {
            SelfPath = Self,
            UpdatesFolder = Updates,
            Version = "0.3.0",
            WaitLimit = TimeSpan.FromSeconds(5),
            Start = info =>
            {
                _started.Add(info);
                return true;
            },
        };

        var code = UpdateApplier.Run(["--apply-update", "--target", Target, "--wait-pid", id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--from", "0.2.1"], env);

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_app_that_does_not_end_is_left_alone(bool cannotBeWaitedFor)
    {
        var env = Env(waitForExit: (_, _) => cannotBeWaitedFor ? throw new Win32Exception(5) : false);

        var code = UpdateApplier.Run(Args("--restart"), env);

        Assert.Equal(1, code);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".old"));
        Assert.False(File.Exists(Target + ".new"));
        Assert.Empty(_started);
        var report = Report();
        Assert.Equal("0.3.0", report.Version);
        Assert.Equal(UpdateText.NotClosed, report.Message);
    }

    [Theory]
    [InlineData("locked", false)]
    [InlineData("locked", true)]
    [InlineData("read-only", false)]
    [InlineData("read-only", true)]
    public void A_program_that_cannot_be_replaced_is_left_as_it_was(string how, bool restart)
    {
        FileStream? held = how == "locked" ? new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        if (how == "read-only") File.SetAttributes(Target, FileAttributes.ReadOnly);

        try
        {
            var code = UpdateApplier.Run(restart ? Args("--restart") : Args(), Env());

            Assert.Equal(1, code);
            Assert.Equal(OldBytes, File.ReadAllBytes(Target));
            Assert.False(File.Exists(Target + ".new"));
            Assert.False(File.Exists(Target + ".old"));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250) }, _delays);
            var report = Report();
            Assert.Equal("0.3.0", report.Version);
            Assert.Equal(UpdateText.ApplyFailed("0.3.0"), report.Message);
            if (restart)
            {
                var info = Assert.Single(_started);
                Assert.Equal(Target, info.FileName);
                Assert.Empty(info.ArgumentList);
            }
            else
            {
                Assert.Empty(_started);
            }
        }
        finally
        {
            held?.Dispose();
            if (how == "read-only") File.SetAttributes(Target, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_program_that_is_in_use_for_a_moment_is_replaced_at_the_next_attempt()
    {
        var held = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var code = UpdateApplier.Run(Args(), Env(delay: span =>
            {
                _delays.Add(span);
                held.Dispose();
            }));

            Assert.Equal(0, code);
            Assert.Single(_delays);
            Assert.Equal(NewBytes, File.ReadAllBytes(Target));
            Assert.Equal(OldBytes, File.ReadAllBytes(Target + ".old"));
            Assert.False(File.Exists(Path.Combine(Updates, "last-error.txt")));
        }
        finally
        {
            held.Dispose();
        }
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-pid")]
    [InlineData("bad-pid")]
    [InlineData("relative")]
    [InlineData("not-exe")]
    [InlineData("itself")]
    [InlineData("other-name")]
    [InlineData("device")]
    public void Arguments_that_make_no_sense_are_refused_and_nothing_is_touched(string kind)
    {
        var other = Path.Combine(_dir.Root, "app", "Other.exe");
        File.WriteAllBytes(other, OldBytes);
        string[] args = kind switch
        {
            "missing-target" => ["--apply-update", "--wait-pid", "4242"],
            "missing-pid" => ["--apply-update", "--target", Target],
            "bad-pid" => ["--apply-update", "--target", Target, "--wait-pid", "soon"],
            "relative" => ["--apply-update", "--target", @"app\Arr411.exe", "--wait-pid", "4242"],
            "not-exe" => ["--apply-update", "--target", Path.ChangeExtension(Target, ".dll"), "--wait-pid", "4242"],
            "itself" => ["--apply-update", "--target", Self, "--wait-pid", "4242"],
            "other-name" => ["--apply-update", "--target", other, "--wait-pid", "4242"],
            _ => ["--apply-update", "--target", @"\\.\C:\Windows\notepad.exe", "--wait-pid", "4242"],
        };

        var code = UpdateApplier.Run(args, Env());

        Assert.Equal(2, code);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.Equal(OldBytes, File.ReadAllBytes(other));
        Assert.Equal(NewBytes, File.ReadAllBytes(Self));
        Assert.Equal(UpdateText.BadApplyArguments, Report().Message);
        Assert.Empty(_started);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_program_with_another_name_is_replaced_when_the_app_said_it_was_that_one()
    {
        var renamed = Path.Combine(_dir.Root, "app", "Mon Arr411.exe");
        File.WriteAllBytes(renamed, OldBytes);
        Record(new UpdateRecord { Applying = new ApplyAttempt("0.3.0", renamed) });

        var code = UpdateApplier.Run(["--apply-update", "--target", renamed, "--wait-pid", "4242"], Env());

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(renamed));
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    public void A_program_that_is_not_the_one_that_was_downloaded_is_not_installed(string differs)
    {
        var hash = differs == "hash" ? FakeGitHub.HashOf(OldBytes) : FakeGitHub.HashOf(NewBytes);
        var size = differs == "size" ? NewBytes.Length + 1 : NewBytes.Length;
        Record(new UpdateRecord { Staged = new StagedUpdate("0.3.0", "Arr411-0.3.0.exe", hash, size) });

        var code = UpdateApplier.Run(Args("--restart"), Env());

        Assert.Equal(1, code);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".new"));
        var report = Report();
        Assert.Equal("0.3.0", report.Version);
        Assert.Equal(UpdateText.StagedChanged, report.Message);
        var info = Assert.Single(_started);
        Assert.Equal(Target, info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void A_program_that_is_the_one_that_was_downloaded_is_installed()
    {
        Record(new UpdateRecord { Staged = new StagedUpdate("0.3.0", "Arr411-0.3.0.exe", FakeGitHub.HashOf(NewBytes), NewBytes.Length) });

        var code = UpdateApplier.Run(Args(), Env());

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public void A_program_that_was_removed_in_the_meantime_is_put_back_by_the_new_one()
    {
        File.Delete(Target);

        var code = UpdateApplier.Run(Args("--restart"), Env());

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".old"));
        Assert.False(File.Exists(Target + ".new"));
        Assert.Single(_started);
    }

    [Fact]
    public void The_old_program_of_an_earlier_swap_makes_room_for_the_new_one()
    {
        File.WriteAllBytes(Target + ".old", FakeGitHub.Bytes(100, 5));

        var code = UpdateApplier.Run(Args(), Env());

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
        Assert.Equal(OldBytes, File.ReadAllBytes(Target + ".old"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_app_that_cannot_be_started_again_does_not_undo_the_update(bool throws)
    {
        var env = Env(start: _ => throws ? throw new Win32Exception(2) : false);

        var code = UpdateApplier.Run(Args("--restart"), env);

        Assert.Equal(0, code);
        Assert.Equal(NewBytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public void A_start_that_is_not_an_update_runs_the_app_as_usual()
    {
        Assert.Null(UpdateApplier.TryRun([]));
        Assert.Null(UpdateApplier.TryRun(["--background"]));
        Assert.Null(UpdateApplier.TryRun(["magnet:?xt=urn:btih:abc", "--apply-update"]));
        Assert.Null(UpdateApplier.TryRun(["--updated-from", "0.2.1"]));
    }
}
