namespace Arr411.E2E;

/// <summary>Starting and stopping the app.</summary>
[Trait("Category", "E2E")]
public sealed class LifecycleTests
{
    [Fact]
    public async Task Closing_the_window_ends_the_app_cleanly()
    {
        await using var app = await AppProcess.StartAsync();

        var exitCode = await app.CloseAsync();

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(" ERROR ", app.ReadLog());
    }
}
