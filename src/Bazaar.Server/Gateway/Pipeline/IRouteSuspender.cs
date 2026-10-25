namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>Suspends a delegated route after its upstream served a model outside the expected set (doc 05).</summary>
public interface IRouteSuspender
{
    ValueTask SuspendAsync(string routeId, IReadOnlyList<string> violations, CancellationToken ct);
}

/// <summary>Does nothing. The default until route suspension exists.</summary>
public sealed class NullRouteSuspender : IRouteSuspender
{
    public static NullRouteSuspender Instance { get; } = new();

    public ValueTask SuspendAsync(string routeId, IReadOnlyList<string> violations, CancellationToken ct) => ValueTask.CompletedTask;
}
