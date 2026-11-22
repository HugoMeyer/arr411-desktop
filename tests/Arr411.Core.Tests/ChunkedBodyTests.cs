using System.Text;
using System.Text.Json.Nodes;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public sealed class ChunkedBodyTests
{
    private const int Block = ChunkedBody.BlockSize;

    private static string Decode(byte[] bytes, int readSize = int.MaxValue)
    {
        using var body = new ChunkedBody();
        var at = 0;
        while (at < bytes.Length)
        {
            var room = body.Free();
            var count = Math.Min(Math.Min(room.Length, bytes.Length - at), readSize);
            bytes.AsSpan(at, count).CopyTo(room.Span);
            body.Advance(count);
            at += count;
        }

        // The read that finds the end of the response asks for room too, which adds an empty block after a full one.
        body.Free();
        Assert.Equal(bytes.Length, body.Length);
        return body.Decode();
    }

    private static string Pattern(int size) => new([.. Enumerable.Range(0, size).Select(i => (char)('a' + (i * 7 % 26)))]);

    [Fact]
    public void Nothing_received_is_an_empty_text()
    {
        using var body = new ChunkedBody();
        body.Free();

        Assert.Equal("", body.Decode());
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Block - 1)]
    [InlineData(Block)]
    [InlineData(Block + 1)]
    [InlineData(2 * Block)]
    [InlineData(3 * Block + 17)]
    public void Every_byte_comes_back_in_order_around_the_edges_of_the_blocks(int size)
    {
        var text = Pattern(size);

        Assert.Equal(text, Decode(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData(0, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    [InlineData(Block - 1, int.MaxValue)]
    [InlineData(Block, int.MaxValue)]
    [InlineData(Block + 1, int.MaxValue)]
    [InlineData(2 * Block, int.MaxValue)]
    [InlineData(3 * Block, int.MaxValue)]
    [InlineData(0, 4093)]
    [InlineData(1, 4093)]
    [InlineData(Block - 1, 4093)]
    [InlineData(Block, 4093)]
    [InlineData(Block + 1, 4093)]
    [InlineData(2 * Block, 4093)]
    [InlineData(3 * Block, 4093)]
    public async Task The_read_loop_of_the_http_client_keeps_the_last_block_of_a_body_whose_size_is_a_multiple_of_the_block(int size, int readSize)
    {
        var text = Pattern(size);
        using var timeout = new CancellationTokenSource();

        var read = await NativeHttp.ReadTextAsync(new ShortReads(Encoding.ASCII.GetBytes(text), readSize), timeout, TimeSpan.FromSeconds(30));

        Assert.Equal(text.Length, read.Length);
        Assert.Equal(text, read);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Block - 1)]
    [InlineData(Block)]
    [InlineData(Block + 1)]
    [InlineData(2 * Block)]
    [InlineData(3 * Block)]
    public async Task A_response_of_any_size_arrives_whole_through_the_http_client(int size)
    {
        var text = Pattern(size);
        using var server = new TestHttpServer(_ => TestResponse.Text(text));
        using var http = new NativeHttp("Arr411-Test/1.0");

        var result = await http.SendAsync(new JsonObject { ["url"] = server.Url("/big") }.ToJsonString());

        Assert.Equal(200, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(text.Length, result.Text.Length);
        Assert.Equal(text, result.Text);
    }

    /// <summary>A response that arrives in pieces, as a socket delivers it: a read never gets more than <c>maxRead</c> bytes.</summary>
    private sealed class ShortReads(byte[] bytes, int maxRead) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var count = Math.Min(Math.Min(buffer.Length, maxRead), bytes.Length - _at);
            bytes.AsSpan(_at, count).CopyTo(buffer.Span);
            _at += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    [InlineData(7)]
    public void A_character_cut_between_two_blocks_is_put_back_together(int readSize)
    {
        for (var padding = ChunkedBody.BlockSize - 5; padding <= ChunkedBody.BlockSize + 1; padding++)
        {
            var text = new string('a', padding) + "é€😀é€😀 end";

            Assert.Equal(text, Decode(Encoding.UTF8.GetBytes(text), readSize));
        }
    }

    [Fact]
    public void Bytes_that_are_not_utf8_become_the_replacement_character()
    {
        Assert.Equal("a\uFFFDb\uFFFD", Decode([0x61, 0xFF, 0x62, 0xC3]));
    }

    [Fact]
    public void What_an_earlier_body_left_in_the_pool_does_not_leak_into_the_next()
    {
        Assert.Equal(new string('z', 70_000), Decode(Encoding.ASCII.GetBytes(new string('z', 70_000))));

        Assert.Equal("short", Decode("short"u8.ToArray()));
    }

    [Fact]
    public void The_text_is_made_with_one_copy()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("héllo ✓ 😀 \"quoted\" ", 250_000)));
        using var body = new ChunkedBody();
        var at = 0;
        while (at < bytes.Length)
        {
            var room = body.Free();
            var count = Math.Min(room.Length, bytes.Length - at);
            bytes.AsSpan(at, count).CopyTo(room.Span);
            body.Advance(count);
            at += count;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = body.Decode();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < text.Length * 2L + 64 * 1024, $"{allocated} bytes were allocated for a text of {text.Length} characters");
    }
}
