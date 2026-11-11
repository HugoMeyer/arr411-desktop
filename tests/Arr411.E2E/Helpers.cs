using System.Runtime.InteropServices;
using System.Text.Json;

namespace Arr411.E2E;

internal static class Js
{
    /// <summary>A JavaScript string literal.</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value);
}

internal static class Wait
{
    /// <summary>Polls a condition of the test side until it holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("This never became true: " + what);
    }
}

internal static class PageExtensions
{
    /// <summary>Calls App.http, as the web app does, and returns how it settled: { ok: true, status, text } or { ok: false, code, message }.</summary>
    public static Task<JsonElement> HttpAsync(this CdpClient page, object request, int timeoutMs = 20_000) =>
        page.EvaluateAsync(
            $$"""
            App.http({{JsonSerializer.Serialize(request)}}).then(
              r => ({ ok: true, status: r.status, text: r.text }),
              e => ({ ok: false, code: e.code, message: e.message }))
            """,
            timeoutMs);
}

internal static class KnownFolders
{
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    public static string DownloadsFolder()
    {
        var result = SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (result != 0) throw new InvalidOperationException("There is no Downloads folder.");
            return Marshal.PtrToStringUni(pointer)!;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
