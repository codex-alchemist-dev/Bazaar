namespace Bazaar.Server.Providers;

/// <summary>Base contract for every OS-bound provider. Probes are synchronous and must not change state.</summary>
public interface IProvider
{
    /// <summary>The kind name the provider reports, for example <c>hyperv</c>.</summary>
    string Kind { get; }

    /// <summary>Reports whether this provider can serve its role on the current machine.</summary>
    ProviderStatus Probe();
}
