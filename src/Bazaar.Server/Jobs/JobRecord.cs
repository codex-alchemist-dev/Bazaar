namespace Bazaar.Server.Jobs;

/// <summary>A job this poller has claimed. <see cref="Attempts"/> counts this claim.</summary>
public sealed record JobRecord(long Id, string Kind, string? DedupeKey, int Attempts, string PayloadJson);
