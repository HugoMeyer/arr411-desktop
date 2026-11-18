using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class HangWatchTests
{
    private readonly List<string> _info = [];
    private readonly List<string> _warn = [];
    private readonly Queue<Action> _window = new();
    private DateTimeOffset _now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private bool _windowGone;

    private HangWatch Watch() => new(
        job =>
        {
            if (_windowGone) throw new InvalidOperationException("Invoke or BeginInvoke cannot be called on a control until the window handle has been created.");
            _window.Enqueue(job);
        },
        _info.Add,
        _warn.Add,
        () => _now,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        start: false);

    private void Seconds(int seconds) => _now += TimeSpan.FromSeconds(seconds);

    private void WindowAnswers()
    {
        while (_window.Count > 0) _window.Dequeue()();
    }

    [Fact]
    public void A_window_that_answers_is_never_reported()
    {
        using var watch = Watch();

        for (var i = 0; i < 30; i++)
        {
            watch.Tick();
            WindowAnswers();
            Seconds(2);
        }

        Assert.Empty(_warn);
        Assert.Empty(_info);
    }

    [Fact]
    public void A_window_that_does_not_answer_is_reported_once_after_ten_seconds()
    {
        using var watch = Watch();
        watch.Tick();

        for (var i = 0; i < 4; i++)
        {
            Seconds(2);
            watch.Tick();
        }

        Assert.Empty(_warn);

        Seconds(2);
        watch.Tick();
        Seconds(2);
        watch.Tick();
        Seconds(2);
        watch.Tick();

        var warning = Assert.Single(_warn);
        Assert.Equal("The window has not answered for 10 s", warning);
    }

    [Fact]
    public void It_says_when_the_window_answers_again_and_how_long_it_took()
    {
        using var watch = Watch();
        watch.Tick();
        for (var i = 0; i < 7; i++)
        {
            Seconds(2);
            watch.Tick();
        }

        Seconds(1);
        WindowAnswers();

        Assert.Single(_warn);
        Assert.Equal("The window answers again after 15 s", Assert.Single(_info));

        Seconds(2);
        watch.Tick();
        WindowAnswers();
        Assert.Single(_warn);
        Assert.Single(_info);
    }

    [Fact]
    public void A_computer_that_slept_does_not_count_as_a_frozen_window()
    {
        using var watch = Watch();
        watch.Tick();

        Seconds(3 * 3600);
        watch.Tick();
        WindowAnswers();

        Assert.Empty(_warn);
        Assert.Empty(_info);
    }

    [Fact]
    public void The_watch_stops_by_itself_once_the_window_is_gone()
    {
        using var watch = Watch();
        _windowGone = true;

        watch.Tick();
        _windowGone = false;
        Seconds(2);
        watch.Tick();

        Assert.Empty(_window);
        Assert.Empty(_warn);
    }

    [Fact]
    public void A_disposed_watch_does_nothing_more()
    {
        var watch = Watch();
        watch.Dispose();

        watch.Tick();

        Assert.Empty(_window);
    }
}
