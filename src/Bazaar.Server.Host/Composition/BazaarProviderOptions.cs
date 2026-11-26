using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Composition;

/// <summary>Provider choice per role: <c>auto</c>, <c>none</c> or a provider kind (doc 03). Moves to the settings registry with ST1.</summary>
public sealed class BazaarProviderOptions
{
    public const string SectionName = "Bazaar:Providers";

    public string ServiceHost { get; set; } = ProviderSelector.Auto;

    public string Vm { get; set; } = ProviderSelector.None;

    public string Cache { get; set; } = ProviderSelector.None;

    public string Search { get; set; } = ProviderSelector.None;
}
