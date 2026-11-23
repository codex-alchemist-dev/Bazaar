namespace Bazaar.Client.Studio.Abstractions;

/// <summary>Runs a granted Studio tool call on the human's desktop and returns the result. Keeps no grants and no audit.</summary>
public interface IStudioRelay
{
    /// <summary>
    /// Relays one call. Refusals and Studio failures come back as a <see cref="StudioRelayResult"/> with <c>Ok = false</c>
    /// and a <see cref="StudioRelayError"/> payload; only caller cancellation throws.
    /// </summary>
    Task<StudioRelayResult> RelayAsync(StudioRelayRequest request, CancellationToken cancellationToken = default);
}
