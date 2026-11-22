namespace Bazaar.Contracts.Modules;

/// <summary>
/// Probes the host's facts for <see cref="HostCompatibility"/>. Must be synchronous and change nothing. Each OS-bound provider
/// module supplies one for its own feature, and tests replace it with fixed facts.
/// </summary>
public interface IHostFactsSource
{
    HostFacts Current();
}
