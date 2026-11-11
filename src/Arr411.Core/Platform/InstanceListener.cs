using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Platform;

/// <summary>Receives what later starts of the app hand over (see <see cref="SingleInstance"/>). The callback runs on a pool thread.</summary>
public sealed class InstanceListener : IDisposable
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    private readonly string _name;
    private readonly Action<LaunchRequest> _onRequest;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public InstanceListener(string name, Action<LaunchRequest> onRequest)
    {
        _name = name;
        _onRequest = onRequest;
    }

    public void Start() => _loop = Task.Run(() => ServeAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop only ends by cancellation.
        }

        _stop.Dispose();
    }

    internal static LaunchRequest? Decode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SingleInstance.ProtocolVersion) return null;
            if (!root.TryGetProperty("args", out var array) || array.ValueKind != JsonValueKind.Array) return null;

            var args = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;

                args.Add(item.GetString()!);
                if (args.Count > LaunchRequest.MaxArguments) break;
            }

            return LaunchRequest.Parse(args, JsonText.Str(root, "cwd") ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string that cannot be read (invalid UTF-8, lone surrogate): not one of our messages.
            return null;
        }
    }

    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _name,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Other starts of the app cannot hand their request over: " + ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // The client left before the connection was complete.
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // The next client is served while this one is handled.
            _ = HandleAsync(server, stop);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken stop)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(ExchangeTimeout);
                var token = limit.Token;

                var accepted = false;
                var request = await ReadAsync(server, token).ConfigureAwait(false);
                if (request is not null)
                {
                    try
                    {
                        _onRequest(request);
                        accepted = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("A request from another start of the app could not be handled.", ex);
                    }
                }

                await server.WriteAsync(new[] { accepted ? SingleInstance.Accepted : SingleInstance.Refused }, token).ConfigureAwait(false);
                await server.FlushAsync(token).ConfigureAwait(false);

                // The client closes its end once it has read the reply: closing ours before would risk losing it.
                await server.ReadAsync(new byte[1], token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that left, or one that took too long: nothing to do for it.
            }
            catch (Exception ex)
            {
                // Nobody awaits this task: whatever else happens must not go unseen.
                Log.Error("A request from another start of the app failed unexpectedly.", ex);
            }
        }
    }

    private static async Task<LaunchRequest?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > SingleInstance.MaxMessageBytes) return null;

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return Decode(body);
    }
}
