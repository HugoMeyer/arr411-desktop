using System.Reflection;
using System.Text;

namespace Arr411.Desktop;

/// <summary>Files embedded at build time: app/ (web app), engine/, page/ (C411 scripts), scripts/ (adapters), app.ico.</summary>
internal static class EmbeddedAssets
{
    private static readonly Assembly Asm = typeof(EmbeddedAssets).Assembly;

    public static Stream? Open(string name) => Asm.GetManifestResourceStream(name);

    /// <summary>A script or other text file of the app (UTF-8).</summary>
    public static string ReadText(string name)
    {
        using var stream = Open(name) ?? throw new InvalidOperationException(name + " is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static int Count(string prefix) =>
        Asm.GetManifestResourceNames().Count(n => n.StartsWith(prefix, StringComparison.Ordinal));
}
