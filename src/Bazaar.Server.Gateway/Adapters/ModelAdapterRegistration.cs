namespace Bazaar.Server.Gateway.Adapters;

/// <summary>
/// One wire-family adapter that a Bazaar Component contributes to the gateway registry under its wire name
/// (docs/design/BAZAAR-COMPONENTS.md section 4). A component registers exactly one of these per adapter. The host builds the
/// <see cref="Pipeline.IAdapterRegistry"/> from every registration present, so a disabled component contributes no adapter.
/// </summary>
/// <param name="WireName">The adapter name stored on provider rows, for example <c>openai_chat</c>.</param>
/// <param name="Create">Builds the adapter from the service provider. Called once when the registry is built.</param>
public sealed record ModelAdapterRegistration(string WireName, Func<IServiceProvider, IModelAdapter> Create);
