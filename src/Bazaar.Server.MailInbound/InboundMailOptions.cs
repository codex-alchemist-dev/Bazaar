namespace Bazaar.Server.MailInbound;

/// <summary>Settings for the inbound pipeline. Parse and screen limits are not repeated here: they come from the one <c>MailScreeningOptions</c> (<c>MailLimits</c>).</summary>
public sealed record InboundMailOptions
{
    /// <summary>The sender domain stored when a message has no usable From (a reserved, never-resolving name).</summary>
    public const string UnknownSenderDomain = "unknown.invalid";

    /// <summary>
    /// Most messages processed per mailbox per pass (PROPOSED setting <c>mail.inbound.max_batch</c>, default 50). The rest are counted as deferred and
    /// pulled again on the next pass; the pass never exceeds the bound, so one flood cannot starve the other mailboxes.
    /// </summary>
    public int MaxBatchSize { get; init; } = 50;

    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchSize, 1);
}
