using Bazaar.Constellation.Communities.Abstractions;

namespace Bazaar.Constellation.Sections;

/// <summary>
/// Recomputes the community layout when the graph version changes, off the UI thread, and publishes only the newest
/// result. A slower, older computation never replaces a newer one, and starting a new one cancels the one in flight.
/// </summary>
public sealed class SectionLayoutPublisher : IDisposable
{
    private readonly ICommunityLayout _layout;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private long _latestRequested = long.MinValue;
    private long _published = long.MinValue;

    public SectionLayoutPublisher(ICommunityLayout layout) => _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    /// <summary>The newest published layout and its graph version, or null before the first result.</summary>
    public (long GraphVersion, CommunityLayoutResult Layout)? Latest { get; private set; }

    /// <summary>Raised on the computing thread when a result is published. Marshal to the UI thread in the handler.</summary>
    public event Action<long, CommunityLayoutResult>? Published;

    /// <summary>Starts a computation for <paramref name="graphVersion"/>. Versions not newer than the latest request are ignored.</summary>
    public Task RequestAsync(
        long graphVersion, IReadOnlyList<CommunityNode> nodes, IReadOnlyList<CommunityEdge> edges, CommunityLayoutOptions? options = null)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (graphVersion <= _latestRequested)
            {
                return Task.CompletedTask;
            }

            _latestRequested = graphVersion;
            _current?.Cancel();
            _current = new CancellationTokenSource();
            token = _current.Token;
        }

        return RunAsync(graphVersion, nodes, edges, options, token);
    }

    private async Task RunAsync(
        long version, IReadOnlyList<CommunityNode> nodes, IReadOnlyList<CommunityEdge> edges, CommunityLayoutOptions? options, CancellationToken token)
    {
        CommunityLayoutResult result;
        try
        {
            result = await _layout.ComputeAsync(nodes, edges, options, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (version < _published || token.IsCancellationRequested)
            {
                return;
            }

            _published = version;
            Latest = (version, result);
        }

        Published?.Invoke(version, result);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }
}
