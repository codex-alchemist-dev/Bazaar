namespace Bazaar.Server.Agents.Inbox;

/// <summary>The digest items of one GC that go into one batch.</summary>
/// <param name="GcId">The GC.</param>
/// <param name="FirstSeq">The smallest GC sequence number in the batch (0 when no item carries one).</param>
/// <param name="LastSeq">The largest GC sequence number in the batch (0 when no item carries one).</param>
/// <param name="Count">The number of digest items for this GC, with or without a sequence number.</param>
public sealed record DigestRange(Guid GcId, long FirstSeq, long LastSeq, int Count);
