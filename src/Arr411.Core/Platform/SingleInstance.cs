using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arr411.Core.Platform;

/// <summary>
/// One instance of the app per data folder. A start that finds one running hands over its command line (a magnet link, a .torrent file,
/// a tab to show) through a named pipe, then ends. Only the current Windows user can reach the pipe.
/// </summary>
public static class SingleInstance
{
    /// <summary>The biggest message accepted: a Windows command line is at most 32 K characters.</summary>
    internal const int MaxMessageBytes = 256 * 1024;

    internal const int ProtocolVersion = 1;

    /// <summary>The reply when the request was handed to the app.</summary>
    internal const byte Accepted = 0x06;

    /// <summary>The reply when the request was not understood.</summary>
    internal const byte Refused = 0x15;

    private const string MutexPrefix = @"Local\";

    /// <summary>The name shared by everything that belongs to the instance using this data folder (mutex and pipe).</summary>
    public static string NameFor(string dataRoot)
    {
        var folder = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder));
        return "Arr411." + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Claims the instance. Null when another one already runs; dispose the result when this one ends.</summary>
    public static Mutex? TryAcquire(string name)
    {
        try
        {
            var mutex = new Mutex(false, MutexPrefix + name, out var created);
            if (created) return mutex;

            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Somebody else owns that name: not an instance that can be talked to, but not one that can be replaced either.
            return null;
        }
    }

    public static bool IsRunning(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexPrefix + name, out var mutex)) return false;

            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Hands the command line of this start to the running instance, trying again while it is still starting.</summary>
    /// <returns>True once the instance took it.</returns>
    public static async Task<bool> ForwardAsync(string name, IReadOnlyList<string> args, string workingDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(new { v = ProtocolVersion, cwd = workingDirectory, args });
        if (message.Length > MaxMessageBytes) return false;

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        var token = limit.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await client.ConnectAsync(1000, token).ConfigureAwait(false);
                    await client.WriteAsync(header, token).ConfigureAwait(false);
                    await client.WriteAsync(message, token).ConfigureAwait(false);
                    await client.FlushAsync(token).ConfigureAwait(false);

                    var reply = new byte[1];
                    await client.ReadExactlyAsync(reply, token).ConfigureAwait(false);
                    return reply[0] == Accepted;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
                {
                    // Not listening yet (it is starting), or already leaving: the pipe may come back, or the instance may be gone.
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Waits until no instance is running any more.</summary>
    public static async Task<bool> WaitUntilGoneAsync(string name, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            while (IsRunning(name)) await Task.Delay(100, limit.Token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
