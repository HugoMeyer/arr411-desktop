using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arr411.Core.Tests;

public sealed class JsonTextTests
{
    private static JsonObject Head() => new() { ["t"] = "httpDone", ["id"] = 7, ["status"] = 200, ["error"] = null };

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("a quote \" and a backslash \\ and a slash /")]
    [InlineData("line\nbreak\r\n\ttab\b\f")]
    [InlineData("héllo ✓ — 日本語")]
    [InlineData("emoji 😀🎬 pairs")]
    [InlineData("<script>alert('x')</script> & more")]
    [InlineData("separators \u2028\u2029")]
    public void The_text_comes_back_unchanged_once_the_browser_has_read_the_message(string text)
    {
        var json = JsonText.WithText(Head(), "text", text);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(text, parsed.RootElement.GetProperty("text").GetString());
        Assert.Equal("httpDone", parsed.RootElement.GetProperty("t").GetString());
        Assert.Equal(7, parsed.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(200, parsed.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Every_control_character_is_escaped()
    {
        var text = new string([.. Enumerable.Range(0, 0x20).Select(i => (char)i), '\u007f']);

        var json = JsonText.WithText(new JsonObject(), "text", text);

        Assert.All(json, c => Assert.True(c >= ' ', $"U+{(int)c:X4} was written as it is"));
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(text, parsed.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void A_surrogate_without_its_partner_becomes_the_replacement_character()
    {
        // Built here: an attribute stores its text as UTF-8, where a lone surrogate cannot be.
        const char high = '\uD83D';
        const char low = '\uDE00';
        (string Text, string Expected)[] cases =
        [
            ("a" + high + "b", "a\uFFFDb"),
            ("a" + low + "b", "a\uFFFDb"),
            ("end" + high, "end\uFFFD"),
            ("" + low + high, "\uFFFD\uFFFD"),
            ("" + high + high + low, "\uFFFD" + high + low),
        ];

        foreach (var (text, expected) in cases)
        {
            var json = JsonText.WithText(Head(), "text", text);

            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(expected, parsed.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Random_text_is_read_back_as_it_was_written()
    {
        var random = new Random(411);
        for (var round = 0; round < 300; round++)
        {
            var chars = new char[random.Next(0, 300)];
            for (var i = 0; i < chars.Length; i++) chars[i] = Pick(random);
            var text = new string(chars);

            var json = JsonText.WithText(Head(), "body", text);

            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)), parsed.RootElement.GetProperty("body").GetString());
        }

        static char Pick(Random random) => random.Next(6) switch
        {
            0 => (char)random.Next(0, 0x20),
            1 => random.Next(2) == 0 ? '"' : '\\',
            2 => (char)random.Next(0xD800, 0xDC00),
            3 => (char)random.Next(0xDC00, 0xE000),
            4 => (char)random.Next(0xE000, 0xFFFE),
            _ => (char)random.Next(0x20, 0x7F),
        };
    }

    [Fact]
    public void The_text_goes_last_after_what_the_head_holds()
    {
        Assert.Equal(
            """{"t":"httpDone","id":7,"status":200,"error":null,"text":"x"}""",
            JsonText.WithText(Head(), "text", "x"));
    }

    [Fact]
    public void An_empty_head_gets_no_stray_comma()
    {
        Assert.Equal("""{"text":"x"}""", JsonText.WithText(new JsonObject(), "text", "x"));
    }

    [Fact]
    public void No_text_is_null()
    {
        Assert.Equal("""{"id":3,"body":null}""", JsonText.WithText(new JsonObject { ["id"] = 3 }, "body", null));
        Assert.Equal("""{"body":null}""", JsonText.WithText(new JsonObject(), "body", null));
    }

    [Fact]
    public void A_huge_text_is_copied_once()
    {
        var text = new string('x', 4_000_000) + "\"\n" + new string('é', 1_000_000);
        JsonText.WithText(Head(), "text", "warm up");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var json = JsonText.WithText(Head(), "text", text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < json.Length * 2L + 10_000, $"{allocated} bytes were allocated for a message of {json.Length} characters");
    }
}
