using System.Text;

namespace Arr411.Core.Hosting;

/// <summary>What the local sites (the web app, the engine) answer the same way: JSON bodies, the methods they serve and the security headers.</summary>
internal static class LocalResponses
{
    public static HostResponse NotFound() => HostResponse.Empty(404, "Not Found");

    /// <summary>For the favicon the web view asks for on its own: there is none to show, and a 404 would end up in the page's console.</summary>
    public static HostResponse NoContent() => new() { Status = 204, Reason = "No Content" };

    public static HostResponse Json(string json) => new()
    {
        Status = 200,
        Reason = "OK",
        ContentType = "application/json; charset=UTF-8",
        Body = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false),
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
    };

    /// <summary>Whether the site serves the method (GET and HEAD, the latter without a body).</summary>
    public static bool IsReadOnly(string method, out bool head)
    {
        head = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        return head || method.Equals("GET", StringComparison.OrdinalIgnoreCase);
    }

    public static HostResponse MethodNotAllowed() =>
        Complete(HostResponse.Empty(405, "Method Not Allowed"), head: false, htmlPolicy: null, extra: ("Allow", "GET, HEAD"));

    /// <summary>Adds the headers every answer carries, and the security policy of the HTML pages.</summary>
    public static HostResponse Finish(HostResponse response, bool head, string htmlPolicy) =>
        Complete(response, head, htmlPolicy, extra: null);

    private static HostResponse Complete(HostResponse response, bool head, string? htmlPolicy, (string Name, string Value)? extra)
    {
        var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["X-Content-Type-Options"] = "nosniff",
        };
        if (extra is { } more) headers[more.Name] = more.Value;
        if (htmlPolicy is not null && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            headers["Content-Security-Policy"] = htmlPolicy;
        }

        if (head) response.Body.Dispose();
        return new HostResponse
        {
            Status = response.Status,
            Reason = response.Reason,
            ContentType = response.ContentType,
            Body = head ? Stream.Null : response.Body,
            Headers = headers,
        };
    }
}
