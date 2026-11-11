namespace Arr411.Core.Hosting;

/// <summary>What the app answers to a request on https://arr411.local.</summary>
public sealed class HostResponse
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public required int Status { get; init; }

    public required string Reason { get; init; }

    public string? ContentType { get; init; }

    public Stream Body { get; init; } = Stream.Null;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    /// <summary>Empty plain-text response carrying only a status (404, 403, ...).</summary>
    public static HostResponse Empty(int status, string reason) =>
        new() { Status = status, Reason = reason, ContentType = "text/plain; charset=UTF-8" };
}
