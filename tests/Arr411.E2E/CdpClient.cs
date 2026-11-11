using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Arr411.E2E;

/// <summary>An event pushed by the browser: console message, uncaught exception, log entry...</summary>
public sealed record CdpEvent(string Method, JsonElement Params);

/// <summary>A minimal Chrome DevTools Protocol client: commands with their answers, plus the events the browser pushes.</summary>
public sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<CdpEvent> _events = [];
    private Task _reader = Task.CompletedTask;
    private int _nextId;
    private int _disposed;

    private CdpClient()
    {
    }

    public static async Task<CdpClient> ConnectAsync(string url)
    {
        var client = new CdpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client._socket.ConnectAsync(new Uri(url), timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        client._reader = Task.Run(client.ReadLoopAsync);
        return client;
    }

    public IReadOnlyList<CdpEvent> Events
    {
        get
        {
            lock (_events) return _events.ToArray();
        }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, int timeoutMs = 15_000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _stop.Token);
            return await answer.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The browser did not answer {method} within {timeoutMs} ms.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Runs a JavaScript expression in the page (promises are awaited) and returns its value, or an undefined element.
    /// With <paramref name="userGesture"/> the page sees what a user did (a click it triggers counts as the user's).
    /// </summary>
    public async Task<JsonElement> EvaluateAsync(string expression, int timeoutMs = 15_000, bool userGesture = false)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true, userGesture }, timeoutMs);
        if (result.TryGetProperty("exceptionDetails", out var details))
        {
            throw new InvalidOperationException("The page threw: " + Describe(details) + "\nExpression: " + expression);
        }

        var value = result.GetProperty("result");
        return value.TryGetProperty("value", out var v) ? v : default;
    }

    public async Task<T?> EvaluateAsync<T>(string expression, int timeoutMs = 15_000, bool userGesture = false)
    {
        var value = await EvaluateAsync(expression, timeoutMs, userGesture);
        return value.ValueKind == JsonValueKind.Undefined ? default : value.Deserialize<T>();
    }

    /// <summary>Polls a JavaScript condition until it is truthy.</summary>
    public async Task WaitForAsync(string condition, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var last = "";
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (await EvaluateAsync<bool>("!!(" + condition + ")", 5_000)) return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                last = ex.Message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("This never became true: " + condition + (last.Length > 0 ? " (" + last + ")" : ""));
    }

    /// <summary>What went wrong in the page so far: uncaught exceptions, console errors and browser errors (blocked content, failed loads...).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var e in Events)
        {
            switch (e.Method)
            {
                case "Runtime.exceptionThrown":
                    problems.Add("exception: " + Describe(e.Params.GetProperty("exceptionDetails")));
                    break;
                case "Runtime.consoleAPICalled" when e.Params.GetProperty("type").GetString() is "error" or "assert":
                    problems.Add("console: " + string.Join(' ', e.Params.GetProperty("args").EnumerateArray().Select(DescribeArgument)));
                    break;
                case "Log.entryAdded" when e.Params.GetProperty("entry").GetProperty("level").GetString() == "error":
                    var entry = e.Params.GetProperty("entry");
                    var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
                    problems.Add("log: " + entry.GetProperty("text").GetString() + (url is null ? "" : " [" + url + "]"));
                    break;
            }
        }

        return problems;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _stop.Cancel();
        _socket.Dispose();
        try
        {
            _reader.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop ends with the socket.
        }

        _stop.Dispose();
    }

    private static string Describe(JsonElement details)
    {
        if (details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description))
        {
            return description.GetString() ?? "";
        }

        return details.TryGetProperty("text", out var text) ? text.GetString() ?? "" : details.GetRawText();
    }

    private static string DescribeArgument(JsonElement argument)
    {
        if (argument.TryGetProperty("description", out var description)) return description.GetString() ?? "";
        return argument.TryGetProperty("value", out var value) ? value.ToString() : argument.GetRawText();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The browser went away, or this client was disposed.
        }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(new InvalidOperationException("The debug connection closed."));
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var id))
        {
            if (!_pending.TryRemove(id.GetInt32(), out var answer)) return;
            if (root.TryGetProperty("error", out var error)) answer.TrySetException(new InvalidOperationException(error.GetRawText()));
            else answer.TrySetResult(root.GetProperty("result").Clone());
        }
        else if (root.TryGetProperty("method", out var method))
        {
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            lock (_events) _events.Add(new CdpEvent(method.GetString() ?? "", parameters));
        }
    }
}
