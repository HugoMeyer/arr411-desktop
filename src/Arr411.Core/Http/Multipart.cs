using System.Text;
using System.Text.Json.Nodes;

namespace Arr411.Core.Http;

/// <summary>multipart/form-data bodies, built by hand like the Android app does.</summary>
internal static class Multipart
{
    /// <param name="parts">[{name, value}] for text fields, [{name, base64, filename?, contentType?}] for files.</param>
    public static (byte[] Body, string Boundary) Build(JsonArray parts)
    {
        var boundary = "----c411qbt" + Guid.NewGuid().ToString("N");
        using var buffer = new MemoryStream();

        void Write(string text) => buffer.Write(Encoding.UTF8.GetBytes(text));

        foreach (var node in parts)
        {
            if (node is not JsonObject part) throw new FormatException("Invalid part");

            var name = Clean(JsonText.Str(part["name"]) ?? throw new FormatException("No value for name"));
            var head = new StringBuilder("--").Append(boundary).Append("\r\n")
                .Append("Content-Disposition: form-data; name=\"").Append(name).Append('"');

            if (part.ContainsKey("base64"))
            {
                var file = Clean(JsonText.Str(part["filename"]) ?? "file");
                var type = Clean(JsonText.Str(part["contentType"]) ?? "application/octet-stream");
                head.Append("; filename=\"").Append(file).Append("\"\r\n")
                    .Append("Content-Type: ").Append(type).Append("\r\n\r\n");
                Write(head.ToString());
                buffer.Write(Convert.FromBase64String(JsonText.Str(part["base64"]) ?? ""));
            }
            else
            {
                head.Append("\r\n\r\n").Append(JsonText.Str(part["value"]) ?? "");
                Write(head.ToString());
            }

            Write("\r\n");
        }

        Write("--" + boundary + "--\r\n");
        return (buffer.ToArray(), boundary);
    }

    // Quotes and line breaks would let a name or a file name break out of its header.
    private static string Clean(string text) => text.Replace("\"", "").Replace("\r", "").Replace("\n", "");
}
