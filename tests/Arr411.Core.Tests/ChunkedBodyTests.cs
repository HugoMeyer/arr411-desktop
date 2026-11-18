using System.Text;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public sealed class ChunkedBodyTests
{
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

        Assert.Equal(bytes.Length, body.Length);
        return body.Decode();
    }

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
    [InlineData(ChunkedBody.BlockSize - 1)]
    [InlineData(ChunkedBody.BlockSize)]
    [InlineData(ChunkedBody.BlockSize + 1)]
    [InlineData(2 * ChunkedBody.BlockSize)]
    [InlineData(3 * ChunkedBody.BlockSize + 17)]
    public void Every_byte_comes_back_in_order_around_the_edges_of_the_blocks(int size)
    {
        var text = new string([.. Enumerable.Range(0, size).Select(i => (char)('a' + (i * 7 % 26)))]);

        Assert.Equal(text, Decode(Encoding.ASCII.GetBytes(text)));
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
