using System.Diagnostics;
using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class ChildProcessesTests
{
    private static Process StartChild()
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 20 127.0.0.1 >nul");
        return Process.Start(start)!;
    }

    [Fact]
    public void A_process_the_program_started_is_found_and_the_program_itself_is_not()
    {
        using var child = StartChild();
        try
        {
            var found = ChildProcesses.Descendants(Environment.ProcessId);

            Assert.Contains(child.Id, found);
            Assert.DoesNotContain(Environment.ProcessId, found);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void The_children_of_a_child_are_found_too_and_what_they_hold_is_added_up()
    {
        using var child = StartChild();
        try
        {
            // cmd.exe starts ping.exe: the grandchild may take a moment to be there.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            ChildProcesses taken;
            do
            {
                taken = ChildProcesses.Take();
                if (taken.Count >= 2) break;
                Thread.Sleep(100);
            }
            while (DateTime.UtcNow < deadline);

            Assert.True(taken.Count >= 2, taken.ToString());
            Assert.True(taken.PrivateBytes > 0);
            Assert.True(taken.Largest.Count is > 0 and <= 3);
            Assert.Equal(taken.Largest.OrderByDescending(size => size), taken.Largest);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void A_process_that_does_not_exist_has_no_descendants()
    {
        Assert.Empty(ChildProcesses.Descendants(int.MaxValue - 5));
        Assert.Equal(0, ChildProcesses.Take(int.MaxValue - 5).Count);
    }

    [Fact]
    public void It_is_told_in_megabytes()
    {
        Assert.Equal("no child process", new ChildProcesses(0, 0, []).ToString());
        Assert.Equal(
            "3 child processes hold 420 MB (private) in all, the biggest: 300, 100 MB",
            new ChildProcesses(3, 420L * 1024 * 1024, [300L * 1024 * 1024, 100L * 1024 * 1024]).ToString());
    }
}
