using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bazaar.Client.GitSolarSystem.Abstractions;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

/// <summary>A feed the test drives: updates are pushed in order, and the feed can end or be cancelled.</summary>
internal sealed class FakeGitSolarSource : IGitSolarSource
{
    private readonly Channel<GitSolarUpdate> _updates = Channel.CreateUnbounded<GitSolarUpdate>();

    /// <summary>The repository the pane asked for.</summary>
    public volatile string? Repository;

    /// <summary>True once the pane started consuming.</summary>
    public volatile bool Subscribed;

    /// <summary>True once the consumer ended because its token was cancelled.</summary>
    public volatile bool Cancelled;

    public void Push(GitSolarUpdate update) => _updates.Writer.TryWrite(update);

    /// <summary>Ends the feed without cancelling it, as a dropped connection does.</summary>
    public void Complete() => _updates.Writer.TryComplete();

    public async IAsyncEnumerable<GitSolarUpdate> UpdatesAsync(
        string repository,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Repository = repository;
        Subscribed = true;
        try
        {
            while (await _updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_updates.Reader.TryRead(out var update))
                {
                    yield return update;
                }
            }
        }
        finally
        {
            Cancelled = cancellationToken.IsCancellationRequested;
        }
    }
}
