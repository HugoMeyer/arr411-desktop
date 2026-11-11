using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Config;

namespace Arr411.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "arr411-tests-" + Guid.NewGuid().ToString("N"));

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in %TEMP%.
        }
    }
}

/// <summary>Reversible and visibly not the identity, so that tests prove the data is not stored in clear.</summary>
internal sealed class ReversibleProtector : IDataProtector
{
    private static readonly byte[] Magic = "PROT1"u8.ToArray();

    public byte[] Protect(byte[] data) => [.. Magic, .. data.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("not protected by this protector");
        }

        return [.. data.Skip(Magic.Length).Select(b => (byte)(b ^ 0x5A))];
    }
}

internal sealed class FakeConfig : Dictionary<string, string>, IConfigReader
{
    public string? GetString(string key) => TryGetValue(key, out var value) ? value : null;
}

internal sealed record RecordedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>An <see cref="HttpMessageHandler"/> that never touches the network.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedRequest> _requests = [];

    public StubHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public StubHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public static HttpResponseMessage Reply(HttpStatusCode status, byte[]? body = null, string? contentType = null, string? location = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        if (contentType is not null) response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (location is not null) response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers) headers[name] = string.Join(", ", values);
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers) headers[name] = string.Join(", ", values);
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body);
        lock (_requests) _requests.Add(recorded);
        return await _respond(recorded, cancellationToken);
    }
}

internal sealed record TestResponse(int Status, byte[] Body, IReadOnlyList<(string Name, string Value)> Headers, bool Hang = false)
{
    public static TestResponse Text(string text, int status = 200, params (string, string)[] headers) =>
        new(status, Encoding.UTF8.GetBytes(text), [("Content-Type", "text/plain; charset=utf-8"), .. headers]);

    public static TestResponse Redirect(string location) => new(302, [], [("Location", location)]);

    public static TestResponse Silent() => new(0, [], [], Hang: true);

    public static TestResponse Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest))
        {
            zip.Write(Encoding.UTF8.GetBytes(text));
        }

        return new TestResponse(200, buffer.ToArray(), [("Content-Type", "text/plain; charset=utf-8"), ("Content-Encoding", "gzip")]);
    }
}

/// <summary>A minimal HTTP/1.1 server on the loopback interface: one answer per connection, requests recorded verbatim.</summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RecordedRequest> _requests = [];

    public TestHttpServer(Func<RecordedRequest, TestResponse>? handler = null)
    {
        Handler = handler ?? (_ => TestResponse.Text("ok"));
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public Func<RecordedRequest, TestResponse> Handler { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _stop.Token);
            if (request is null) return;

            lock (_requests) _requests.Add(request);
            var response = Handler(request);
            if (response.Hang)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            var head = new StringBuilder($"HTTP/1.1 {response.Status} Test\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\n");
            foreach (var (name, value) in response.Headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token);
            await stream.WriteAsync(response.Body, _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The client gave up (timeout tests) or the server stopped.
        }
    }

    private static async Task<RecordedRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var received = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(received))
        {
            if (await stream.ReadAsync(one, cancellation) == 0) return null;
            received.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString([.. received]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
        var read = 0;
        while (read < body.Length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), cancellation);
            if (n == 0) return null;
            read += n;
        }

        return new RecordedRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static bool EndsWithBlankLine(List<byte> bytes) =>
        bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';
}
