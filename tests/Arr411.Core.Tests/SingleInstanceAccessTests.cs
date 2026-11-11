using System.Security.AccessControl;
using System.Security.Principal;
using Arr411.Core.Platform;

namespace Arr411.Core.Tests;

/// <summary>
/// A running instance whose mutex this start is not allowed to open (the app was started elevated, the other one is not):
/// it is not one that can be replaced, and not one that can be ignored either.
/// </summary>
public class SingleInstanceAccessTests
{
    private static string NewName() => "Arr411.test." + Guid.NewGuid().ToString("N");

    private static Mutex ClosedToEveryone(string name)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Deny));
        return MutexAcl.Create(false, @"Local\" + name, out _, security);
    }

    [Fact]
    public void A_name_held_by_a_mutex_that_cannot_be_opened_is_not_taken_over()
    {
        var name = NewName();
        using var theirs = ClosedToEveryone(name);

        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void A_mutex_that_cannot_be_opened_still_counts_as_a_running_instance()
    {
        var name = NewName();
        using var theirs = ClosedToEveryone(name);

        Assert.True(SingleInstance.IsRunning(name));
    }

    [Fact]
    public async Task Waiting_for_the_end_does_not_end_while_such_an_instance_runs()
    {
        var name = NewName();
        using var theirs = ClosedToEveryone(name);

        Assert.False(await SingleInstance.WaitUntilGoneAsync(name, TimeSpan.FromMilliseconds(300)));
    }
}
