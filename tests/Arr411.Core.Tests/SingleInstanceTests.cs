using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Arr411.Core.Platform;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static string NewName() => "Arr411.test." + Guid.NewGuid().ToString("N");

    private static InstanceListener Listen(string name, Action<LaunchRequest> onRequest)
    {
        var listener = new InstanceListener(name, onRequest);
        listener.Start();
        return listener;
    }

    // Talks to the pipe by hand, to say what the real client never would. Returns the reply byte, or -1 when the instance closed without one.
    private static async Task<int> ExchangeAsync(string name, int declaredLength, byte[] body)
    {
        using var limit = new CancellationTokenSource(Generous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000, limit.Token);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        await client.WriteAsync(header, limit.Token);
        await client.WriteAsync(body, limit.Token);
        await client.FlushAsync(limit.Token);

        var reply = new byte[1];
        var read = await client.ReadAsync(reply, limit.Token);
        return read == 0 ? -1 : reply[0];
    }

    [Fact]
    public void The_name_belongs_to_the_data_folder()
    {
        var name = SingleInstance.NameFor(@"C:\Data\Arr411");

        Assert.Matches("^Arr411\\.[0-9a-f]{16}$", name);
        Assert.Equal(name, SingleInstance.NameFor(@"C:\Data\Arr411"));
        Assert.NotEqual(name, SingleInstance.NameFor(@"C:\Data\Autre"));
        Assert.NotEqual(name, SingleInstance.NameFor(@"D:\Data\Arr411"));
    }

    [Fact]
    public void The_way_the_folder_is_written_does_not_matter()
    {
        var name = SingleInstance.NameFor(@"C:\Data\Arr411");

        Assert.Equal(name, SingleInstance.NameFor(@"c:\data\ARR411"));
        Assert.Equal(name, SingleInstance.NameFor(@"C:\Data\Arr411\"));
        Assert.Equal(name, SingleInstance.NameFor(@"C:\Data\Arr411/"));
        Assert.Equal(name, SingleInstance.NameFor(@"C:\Data\Autre\..\Arr411"));
        Assert.Equal(SingleInstance.NameFor(Path.GetFullPath("un-dossier")), SingleInstance.NameFor("un-dossier"));
    }

    [Fact]
    public void Only_one_start_can_claim_the_instance()
    {
        var name = NewName();

        using var first = SingleInstance.TryAcquire(name);

        Assert.NotNull(first);
        Assert.True(SingleInstance.IsRunning(name));
        Assert.Null(SingleInstance.TryAcquire(name));
        Assert.True(SingleInstance.IsRunning(name));
    }

    [Fact]
    public void The_instance_can_be_claimed_again_once_released()
    {
        var name = NewName();
        SingleInstance.TryAcquire(name)!.Dispose();

        Assert.False(SingleInstance.IsRunning(name));
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void Two_data_folders_are_two_instances()
    {
        var root = @"C:\Arr411Tests\" + Guid.NewGuid().ToString("N");

        using var one = SingleInstance.TryAcquire(SingleInstance.NameFor(root + @"\un"));
        using var other = SingleInstance.TryAcquire(SingleInstance.NameFor(root + @"\deux"));

        Assert.NotNull(one);
        Assert.NotNull(other);
    }

    [Fact]
    public void Nothing_runs_under_a_name_nobody_took()
    {
        Assert.False(SingleInstance.IsRunning(NewName()));
    }

    [Fact]
    public async Task Nothing_is_handed_over_when_no_instance_listens()
    {
        var watch = Stopwatch.StartNew();

        var accepted = await SingleInstance.ForwardAsync(NewName(), ["magnet:?xt=urn:btih:abc"], @"C:\Work", TimeSpan.FromMilliseconds(400));

        Assert.False(accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "the wait must end with the time given");
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused_without_even_trying()
    {
        var watch = Stopwatch.StartNew();

        var accepted = await SingleInstance.ForwardAsync(NewName(), [new string('x', SingleInstance.MaxMessageBytes)], "", Generous);

        Assert.False(accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "no connection is attempted for such a message");
    }

    [Fact]
    public async Task A_request_reaches_the_running_instance()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);

        var accepted = await SingleInstance.ForwardAsync(name, ["--tab=qbit", "magnet:?xt=urn:btih:abc", "film.torrent"], @"C:\Work", Generous);

        Assert.True(accepted);
        var request = Assert.Single(received);
        Assert.Equal("qbit", request.Tab);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Equal([@"C:\Work\film.torrent"], request.TorrentFiles);
    }

    [Fact]
    public async Task Arguments_with_quotes_and_accents_arrive_untouched()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);
        const string magnet = "magnet:?xt=urn:btih:abc&dn=Un \"film\" é à ü 日本 😀";

        Assert.True(await SingleInstance.ForwardAsync(name, [magnet], "", Generous));

        Assert.Equal([magnet], Assert.Single(received).Magnets);
    }

    [Fact]
    public async Task Requests_in_a_row_are_all_taken()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(await SingleInstance.ForwardAsync(name, [$"magnet:?xt=urn:btih:{i:x4}"], "", Generous));
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"magnet:?xt=urn:btih:{i:x4}"), received.SelectMany(r => r.Magnets));
    }

    [Fact]
    public async Task Requests_at_the_same_time_are_all_taken()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SingleInstance.ForwardAsync(name, [$"magnet:?xt=urn:btih:{i:x4}"], "", Generous)));

        Assert.All(results, accepted => Assert.True(accepted));
        Assert.Equal(
            Enumerable.Range(0, 8).Select(i => $"magnet:?xt=urn:btih:{i:x4}"),
            received.SelectMany(r => r.Magnets).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_request_sent_while_the_instance_is_still_starting_waits_for_it()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();

        var sending = SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous);
        await Task.Delay(400);
        using var listener = Listen(name, received.Enqueue);

        Assert.True(await sending);
        Assert.Single(received);
    }

    [Fact]
    public async Task A_request_is_still_taken_when_the_instance_needs_more_than_a_second_to_listen()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();

        var sending = SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous);
        await Task.Delay(1500);
        using var listener = Listen(name, received.Enqueue);

        Assert.True(await sending);
        Assert.Single(received);
    }

    [Fact]
    public async Task A_handler_that_fails_is_reported_and_the_service_goes_on()
    {
        var name = NewName();
        var calls = 0;
        using var listener = Listen(name, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
        });

        Assert.False(await SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous));
        Assert.True(await SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_request_with_nothing_to_do_is_still_taken()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);

        Assert.True(await SingleInstance.ForwardAsync(name, [], "", Generous));

        Assert.False(Assert.Single(received).HasWork);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"v":2,"args":[]}""")]
    [InlineData("""{"v":1,"args":[1]}""")]
    public async Task A_message_that_is_not_understood_gets_a_refusal(string text)
    {
        var name = NewName();
        var calls = 0;
        using var listener = Listen(name, _ => Interlocked.Increment(ref calls));
        var body = Encoding.UTF8.GetBytes(text);

        Assert.Equal(SingleInstance.Refused, await ExchangeAsync(name, body.Length, body));

        Assert.Equal(0, calls);
        Assert.True(await SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(SingleInstance.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task A_length_that_makes_no_sense_gets_a_refusal(int length)
    {
        var name = NewName();
        var calls = 0;
        using var listener = Listen(name, _ => Interlocked.Increment(ref calls));

        Assert.Equal(SingleInstance.Refused, await ExchangeAsync(name, length, []));

        Assert.Equal(0, calls);
        Assert.True(await SingleInstance.ForwardAsync(name, [], "", Generous));
    }

    [Fact]
    public async Task A_client_that_leaves_in_the_middle_of_a_message_does_not_stop_the_service()
    {
        var name = NewName();
        using var listener = Listen(name, _ => { });

        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(5000);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 100);
            await client.WriteAsync(header);
            await client.WriteAsync("{\"v\":1"u8.ToArray());
            await client.FlushAsync();
        }

        Assert.True(await SingleInstance.ForwardAsync(name, [], "", Generous));
    }

    [Fact]
    public async Task A_client_that_says_nothing_does_not_keep_the_others_waiting()
    {
        var name = NewName();
        var received = new ConcurrentQueue<LaunchRequest>();
        using var listener = Listen(name, received.Enqueue);

        await using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await silent.ConnectAsync(5000);
        var watch = Stopwatch.StartNew();

        Assert.True(await SingleInstance.ForwardAsync(name, ["magnet:?xt=urn:btih:abc"], "", Generous));

        Assert.Single(received);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "the silent client must not be waited for");
    }

    [Fact]
    public async Task A_stopped_listener_takes_no_more_requests()
    {
        var name = NewName();
        var listener = Listen(name, _ => { });
        Assert.True(await SingleInstance.ForwardAsync(name, [], "", Generous));

        listener.Dispose();

        Assert.False(await SingleInstance.ForwardAsync(name, [], "", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_listener_can_be_disposed_before_anyone_calls()
    {
        var listener = Listen(NewName(), _ => { });

        await Task.Delay(100);
        listener.Dispose();
    }

    [Fact]
    public async Task Waiting_for_the_end_gives_up_while_the_instance_runs()
    {
        var name = NewName();
        using var held = SingleInstance.TryAcquire(name);

        Assert.False(await SingleInstance.WaitUntilGoneAsync(name, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Waiting_for_the_end_returns_at_once_when_nothing_runs()
    {
        Assert.True(await SingleInstance.WaitUntilGoneAsync(NewName(), Generous));
    }

    [Fact]
    public async Task Waiting_for_the_end_notices_the_instance_leaving()
    {
        var name = NewName();
        var held = SingleInstance.TryAcquire(name);
        Assert.NotNull(held);

        var leaving = Task.Run(async () =>
        {
            await Task.Delay(300);
            held.Dispose();
        });

        Assert.True(await SingleInstance.WaitUntilGoneAsync(name, Generous));
        await leaving;
    }

    [Fact]
    public async Task Waiting_for_the_end_can_be_cancelled()
    {
        var name = NewName();
        using var held = SingleInstance.TryAcquire(name);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.False(await SingleInstance.WaitUntilGoneAsync(name, Generous, cancel.Token));
    }
}

public class InstanceListenerDecodeTests
{
    private static LaunchRequest? Decode(string json) => InstanceListener.Decode(Encoding.UTF8.GetBytes(json));

    private static LaunchRequest? Decode(params byte[][] parts) => InstanceListener.Decode([.. parts.SelectMany(part => part)]);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"v":1,"args":[""")]
    [InlineData("[]")]
    [InlineData("\"v\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"args":[]}""")]
    [InlineData("""{"v":2,"args":[]}""")]
    [InlineData("""{"v":0,"args":[]}""")]
    [InlineData("""{"v":"1","args":[]}""")]
    [InlineData("""{"v":1.5,"args":[]}""")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"v":1,"args":"x"}""")]
    [InlineData("""{"v":1,"args":{}}""")]
    [InlineData("""{"v":1,"args":null}""")]
    [InlineData("""{"v":1,"args":[1]}""")]
    [InlineData("""{"v":1,"args":[null]}""")]
    [InlineData("""{"v":1,"args":["a",true]}""")]
    [InlineData("""{"v":1,"args":[["x"]]}""")]
    public void A_message_that_is_not_ours_is_dropped(string json)
    {
        Assert.Null(Decode(json));
    }

    [Theory]
    [InlineData("[[[[[[]]]]]]")]
    [InlineData("""{"v":1,"args":[[[[[]]]]]}""")]
    [InlineData("""{"v":1,"args":[],"x":{"a":{"b":{"c":{}}}}}""")]
    public void A_message_nested_too_deep_is_dropped(string json)
    {
        Assert.Null(Decode(json));
    }

    [Fact]
    public void Bytes_that_are_not_text_are_dropped()
    {
        Assert.Null(InstanceListener.Decode([0xFF, 0xFE, 0x00, 0x01]));
        Assert.Null(InstanceListener.Decode([]));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_inside_a_string_are_dropped()
    {
        Assert.Null(Decode(Utf8("""{"v":1,"args":[" """), [0xFF, 0xC0], Utf8("\"]}")));
        Assert.Null(Decode(Utf8("""{"v":1,"cwd":" """), [0xFF, 0xC0], Utf8("\",\"args\":[]}")));
    }

    [Fact]
    public void A_lone_surrogate_escape_is_dropped_or_harmless()
    {
        var request = Decode("""{"v":1,"args":["\ud800"]}""");

        Assert.True(request is null || !request.HasWork);
    }

    [Fact]
    public void A_valid_message_becomes_a_request()
    {
        var request = Decode("""{"v":1,"cwd":"C:\\Work","args":["magnet:?xt=urn:btih:abc","film.torrent","--tab=qbit"]}""");

        Assert.NotNull(request);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Equal([@"C:\Work\film.torrent"], request.TorrentFiles);
        Assert.Equal("qbit", request.Tab);
    }

    [Theory]
    [InlineData("""{"v":1,"args":["film.torrent"]}""")]
    [InlineData("""{"v":1,"cwd":null,"args":["film.torrent"]}""")]
    [InlineData("""{"v":1,"cwd":5,"args":["film.torrent"]}""")]
    [InlineData("""{"v":1,"cwd":"","args":["film.torrent"]}""")]
    [InlineData("""{"v":1,"cwd":"relatif","args":["film.torrent"]}""")]
    public void A_relative_file_needs_a_working_folder_to_be_found(string json)
    {
        var request = Decode(json);

        Assert.NotNull(request);
        Assert.Empty(request.TorrentFiles);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void Absolute_files_do_not_need_a_working_folder()
    {
        var request = Decode("""{"v":1,"args":["D:\\a\\film.torrent"]}""");

        Assert.NotNull(request);
        Assert.Equal([@"D:\a\film.torrent"], request.TorrentFiles);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        var request = Decode("""{"v":1,"args":["magnet:?xt=urn:btih:abc"],"extra":{"a":[1,2]},"w":true}""");

        Assert.NotNull(request);
        Assert.Single(request.Magnets);
    }

    [Fact]
    public void The_order_of_the_properties_does_not_matter()
    {
        var request = Decode("""{"args":["magnet:?xt=urn:btih:abc"],"cwd":"C:\\Work","v":1}""");

        Assert.NotNull(request);
        Assert.Single(request.Magnets);
    }

    [Fact]
    public void An_empty_list_of_arguments_is_a_request_with_nothing_to_do()
    {
        var request = Decode("""{"v":1,"args":[]}""");

        Assert.NotNull(request);
        Assert.False(request.HasWork);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void Only_the_first_arguments_are_read_whatever_their_number()
    {
        var arguments = Enumerable.Range(0, 5000).Select(i => $"magnet:?xt=urn:btih:{i:x4}").ToArray();

        var request = Decode(JsonSerializer.Serialize(new { v = 1, cwd = "", args = arguments }));

        Assert.NotNull(request);
        Assert.Equal(arguments.Take(LaunchRequest.MaxItems), request.Magnets);
    }

    [Fact]
    public void Options_sent_by_another_start_are_understood()
    {
        var request = Decode("""{"v":1,"args":["--background","--quit"]}""");

        Assert.NotNull(request);
        Assert.True(request.Background);
        Assert.True(request.Quit);
    }

    [Fact]
    public void What_a_quote_in_a_link_let_into_the_command_line_of_another_start_is_ignored()
    {
        // The registered command of that start gave "--", the link as far as its quote, then what came after the quote.
        var request = Decode("""{"v":1,"cwd":"C:\\Work","args":["--","magnet:?xt=urn:btih:abc","\\\\attacker\\share\\x.torrent","--quit"]}""");

        Assert.NotNull(request);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Empty(request.TorrentFiles);
        Assert.False(request.Quit);
        Assert.Equal(2, request.Ignored);
    }
}
