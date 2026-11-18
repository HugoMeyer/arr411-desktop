using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arr411.Core;

/// <summary>JSON written for the web views and the disk: UTF-8 kept as is (messages never end up inside HTML).</summary>
internal static class JsonText
{
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Stringify(JsonNode? node) => node is null ? "null" : node.ToJsonString(Relaxed);

    /// <summary>
    /// <paramref name="head"/> with one more property, <paramref name="name"/>, that holds <paramref name="text"/>: the message that carries a response,
    /// whose text can be tens of MB (the whole library of qBittorrent). The text is escaped straight into a string of the right size: the JSON writers
    /// keep several copies of such a text in the buffers they rent from the shared pool, and these stay in the memory of the process.
    /// </summary>
    /// <param name="name">A plain ASCII name, nothing to escape.</param>
    public static string WithText(JsonObject head, string name, string? text)
    {
        var start = Stringify(head);
        var open = start.Length > 2 ? "," : "";
        var key = "\"" + name + "\":";
        if (text is null) return string.Concat(start.AsSpan(0, start.Length - 1), open, key, "null}");

        // The start without its closing brace, the separator, the name, the opening quote, the text, the closing quote and the closing brace.
        var length = start.Length - 1 + open.Length + key.Length + 1 + EscapedLength(text) + 2;
        return string.Create(length, (start, open, key, text), static (span, state) =>
        {
            var (head, open, key, text) = state;
            head.AsSpan(0, head.Length - 1).CopyTo(span);
            var at = head.Length - 1;
            open.CopyTo(span[at..]);
            at += open.Length;
            key.CopyTo(span[at..]);
            at += key.Length;
            span[at++] = '"';
            at += Escape(text, span[at..]);
            span[at++] = '"';
            span[at] = '}';
        });
    }

    // Quotes, backslashes and control characters get an escape. A surrogate that has no partner (which no JSON writer accepts, and the browser could
    // not decode) is replaced by U+FFFD.
    private static int EscapedLength(string text)
    {
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t') length += 2;
            else if (c < ' ') length += 6;
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                length += 2;
                i++;
            }
            else if (char.IsSurrogate(c)) length += 6;
            else length++;
        }

        return length;
    }

    private static int Escape(string text, Span<char> to)
    {
        var at = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    to[at++] = '\\';
                    to[at++] = '"';
                    break;
                case '\\':
                    to[at++] = '\\';
                    to[at++] = '\\';
                    break;
                case '\b':
                    to[at++] = '\\';
                    to[at++] = 'b';
                    break;
                case '\f':
                    to[at++] = '\\';
                    to[at++] = 'f';
                    break;
                case '\n':
                    to[at++] = '\\';
                    to[at++] = 'n';
                    break;
                case '\r':
                    to[at++] = '\\';
                    to[at++] = 'r';
                    break;
                case '\t':
                    to[at++] = '\\';
                    to[at++] = 't';
                    break;
                case < ' ':
                    at += WriteUnicodeEscape(c, to[at..]);
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        to[at++] = c;
                        to[at++] = text[++i];
                    }
                    else if (char.IsSurrogate(c))
                    {
                        at += WriteUnicodeEscape('\uFFFD', to[at..]);
                    }
                    else
                    {
                        to[at++] = c;
                    }

                    break;
            }
        }

        return at;
    }

    private static int WriteUnicodeEscape(char c, Span<char> to)
    {
        to[0] = '\\';
        to[1] = 'u';
        to[2] = HexDigit(c >> 12);
        to[3] = HexDigit(c >> 8);
        to[4] = HexDigit(c >> 4);
        to[5] = HexDigit(c);
        return 6;
    }

    private static char HexDigit(int value) => "0123456789abcdef"[value & 0xF];

    /// <summary>The string value of a property, or null when it is missing or not a string.</summary>
    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
