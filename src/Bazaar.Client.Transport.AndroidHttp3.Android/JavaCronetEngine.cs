namespace Bazaar.Client.Transport.AndroidHttp3.Android;

/// <summary>An <see cref="ICronetEngine"/> over an <see cref="IJavaCronetBridge"/>.</summary>
public sealed class JavaCronetEngine : ICronetEngine
{
    private readonly IJavaCronetBridge _bridge;

    /// <summary>Creates the engine over the head's bridge.</summary>
    public JavaCronetEngine(IJavaCronetBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <inheritdoc />
    public bool IsAvailable => _bridge.IsAvailable;

    /// <inheritdoc />
    public async ValueTask<ICronetBidirectionalStream> OpenStreamAsync(CronetStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new JavaCronetStream(_bridge);
        try
        {
            await stream.StartAsync(request, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
