namespace Arr411.Core.Http;

/// <summary>Outcome of a native HTTP request: a response (any status, even 4xx/5xx) or a network failure.</summary>
/// <param name="Status">HTTP status; 0 on a network failure.</param>
/// <param name="Text">Response body decoded as UTF-8; empty on a network failure.</param>
/// <param name="Error">Set only for network failures (French message shown to the user).</param>
public sealed record HttpResult(int Status, string Text, string? Error)
{
    public static HttpResult Ok(int status, string text) => new(status, text, null);

    public static HttpResult Fail(string error) => new(0, "", error);
}
