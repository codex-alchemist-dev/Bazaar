namespace Bazaar.Server.MailInbound;

/// <summary>A pass over every active mailbox, one after another. A failing mailbox never stops the others; failures come back in the result.</summary>
public sealed class InboundMailWorker(IInboundMailboxDirectory directory, IInboundMailPipeline pipeline) : IInboundMailWorker
{
    public async Task<InboundPassResult> RunPassAsync(CancellationToken ct)
    {
        IReadOnlyList<InboundMailboxBinding> mailboxes;
        try
        {
            mailboxes = await directory.ListActiveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InboundPassResult([
                new MailboxPassResult(Guid.Empty, 0, 0, 0, 0, 0, [new InboundFailure("directory_unavailable:" + ex.GetType().Name, null)]),
            ]);
        }

        var results = new List<MailboxPassResult>(mailboxes.Count);
        foreach (var mailbox in mailboxes)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await pipeline.ProcessMailboxAsync(mailbox, ct).ConfigureAwait(false));
        }

        return new InboundPassResult(results);
    }
}
