namespace Bazaar.Server.Providers;

/// <summary>Result of a provider's <see cref="IProvider.Probe"/>. Logged at startup and shown on the status page (doc 23).</summary>
/// <param name="Kind">Provider kind as the settings name it, for example <c>systemd</c> or <c>postgres_fts</c>.</param>
/// <param name="Usable">True when the provider can serve its role on this machine right now.</param>
/// <param name="Features">Optional capabilities the provider reports, for example <c>pgvector</c>.</param>
/// <param name="Reason">Why the provider is not usable, or null when it is.</param>
public sealed record ProviderStatus(string Kind, bool Usable, IReadOnlyList<string> Features, string? Reason);
