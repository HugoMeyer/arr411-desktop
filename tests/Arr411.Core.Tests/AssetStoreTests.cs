using System.Text;
using Arr411.Core.Hosting;

namespace Arr411.Core.Tests;

public class AssetStoreTests
{
    private static AssetStore Store(params (string Name, string Text)[] files)
    {
        var map = files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Text));
        return new AssetStore(name => map.TryGetValue(name, out var bytes) ? new MemoryStream(bytes) : null);
    }

    private static string ReadBody(HostResponse r)
    {
        using var reader = new StreamReader(r.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Empty_path_serves_index_html()
    {
        var r = Store(("index.html", "<h1>Arr411</h1>")).Get("");

        Assert.Equal(200, r.Status);
        Assert.Equal("text/html; charset=UTF-8", r.ContentType);
        Assert.Equal("<h1>Arr411</h1>", ReadBody(r));
    }

    [Fact]
    public void Text_files_get_a_utf8_charset_and_are_never_cached()
    {
        var r = Store(("core.js", "var a = 1;")).Get("core.js");

        Assert.Equal(200, r.Status);
        Assert.Equal("application/javascript; charset=UTF-8", r.ContentType);
        Assert.Equal("no-cache", r.Headers["Cache-Control"]);
    }

    [Fact]
    public void Binary_files_have_no_charset()
    {
        var r = Store(("logo.png", "png")).Get("logo.png");

        Assert.Equal(200, r.Status);
        Assert.Equal("image/png", r.ContentType);
    }

    [Fact]
    public void Missing_file_is_404()
    {
        var r = Store(("index.html", "x")).Get("nope.js");

        Assert.Equal(404, r.Status);
        Assert.Equal("Not Found", r.Reason);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../b.js")]
    [InlineData("..")]
    public void Parent_segments_are_refused_even_when_the_resource_exists(string rel)
    {
        var r = Store((rel, "secret")).Get(rel);

        Assert.Equal(404, r.Status);
    }
}
