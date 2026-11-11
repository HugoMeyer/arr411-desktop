using Arr411.Core.Hosting;

namespace Arr411.Core.Tests;

public class MimeTypesTests
{
    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("core.js", "application/javascript")]
    [InlineData("app.css", "text/css")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("data.json", "application/json")]
    [InlineData("logo.png", "image/png")]
    [InlineData("cover.webp", "image/webp")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("PHOTO.JPG", "image/jpeg")]
    [InlineData("sub/dir/x.JS", "application/javascript")]
    [InlineData("archive.bin", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void For_maps_extensions_like_the_android_app(string name, string expected) =>
        Assert.Equal(expected, MimeTypes.For(name));

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/css", true)]
    [InlineData("application/javascript", true)]
    [InlineData("application/json", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("image/png", false)]
    [InlineData("font/woff2", false)]
    [InlineData("application/octet-stream", false)]
    public void IsText_flags_the_types_served_with_a_utf8_charset(string mime, bool expected) =>
        Assert.Equal(expected, MimeTypes.IsText(mime));
}
