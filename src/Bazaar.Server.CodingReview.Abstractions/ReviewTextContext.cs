namespace Bazaar.Server.CodingReview;

/// <summary>The task card the PR serves. Both fields are untrusted text.</summary>
public sealed record TaskCardSummary(UntrustedText Title, UntrustedText Summary);

/// <summary>One message of the GC thread that asked for the work. The author name is a server-known identity.</summary>
public sealed record GcThreadLine(string Author, UntrustedText Text);

/// <summary>The GC thread excerpt the reviewer may read. Lines are in thread order, oldest first.</summary>
public sealed record GcThreadExcerpt(IReadOnlyList<GcThreadLine> Lines);
