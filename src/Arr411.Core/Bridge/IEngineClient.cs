namespace Arr411.Core.Bridge;

/// <summary>The engine as the bridge of the C411 page sees it: somewhere to send an extension message and wait for the answer.</summary>
public interface IEngineClient
{
    /// <summary>
    /// Hands the message (JSON text) to the engine and returns its answer (JSON text). Waits for the engine to be up;
    /// when it goes away the answer is an error object. Cancelling stops the wait.
    /// </summary>
    Task<string> SendAsync(string messageJson, CancellationToken cancellation = default);
}
