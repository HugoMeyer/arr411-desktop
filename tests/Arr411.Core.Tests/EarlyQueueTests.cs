using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class EarlyQueueTests
{
    [Fact]
    public void What_waits_comes_out_in_the_order_it_came()
    {
        var queue = new EarlyQueue<int>();

        Assert.True(queue.TryHold(3));
        Assert.True(queue.TryHold(1));
        Assert.True(queue.TryHold(2));

        Assert.Equal([3, 1, 2], queue.Release());
    }

    [Fact]
    public void Nothing_waiting_is_an_empty_release()
    {
        Assert.Empty(new EarlyQueue<string>().Release());
    }

    [Fact]
    public void Once_released_nothing_waits_any_more()
    {
        var queue = new EarlyQueue<string>();
        queue.TryHold("before");
        queue.Release();

        Assert.False(queue.TryHold("after"));
        Assert.Empty(queue.Release());
    }

    [Fact]
    public void The_second_release_does_not_hand_the_items_over_again()
    {
        var queue = new EarlyQueue<string>();
        queue.TryHold("only");

        Assert.Equal(["only"], queue.Release());
        Assert.Empty(queue.Release());
    }

    [Fact]
    public async Task An_item_is_either_kept_for_the_release_or_refused_never_lost_nor_doubled()
    {
        for (var round = 0; round < 50; round++)
        {
            var queue = new EarlyQueue<int>();
            var refused = new List<int>();
            var start = new ManualResetEventSlim();
            var posters = Enumerable.Range(0, 4).Select(poster => Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 200; i++)
                {
                    var item = poster * 1000 + i;
                    if (!queue.TryHold(item))
                    {
                        lock (refused) refused.Add(item);
                    }
                }
            })).ToArray();

            start.Set();
            Thread.Yield();
            var kept = queue.Release();
            await Task.WhenAll(posters);

            var all = kept.Concat(refused).OrderBy(i => i).ToArray();
            Assert.Equal(Enumerable.Range(0, 4).SelectMany(poster => Enumerable.Range(0, 200).Select(i => poster * 1000 + i)).OrderBy(i => i), all);
        }
    }
}
