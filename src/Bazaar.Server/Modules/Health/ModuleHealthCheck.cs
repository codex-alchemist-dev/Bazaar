namespace Bazaar.Server.Modules.Health;

/// <summary>
/// The result of one probe: its status, a short reason and the time it was taken. The time comes from an injected
/// <see cref="TimeProvider"/>, so results are deterministic under test.
/// </summary>
/// <param name="Status">What the probe found.</param>
/// <param name="Reason">A short human-readable reason. Never contains secrets or exception messages.</param>
/// <param name="CheckedAt">When the probe result was produced, in UTC.</param>
public sealed record ModuleHealthCheck(ModuleHealthStatus Status, string Reason, DateTimeOffset CheckedAt);
