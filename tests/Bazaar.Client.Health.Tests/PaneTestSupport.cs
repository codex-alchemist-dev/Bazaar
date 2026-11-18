using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Health;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Tests;

/// <summary>Builds descriptors from JSON text and a registry with the Health modules composed, as a head does.</summary>
internal static class PaneTestSupport
{
    public static PaneDescriptor Descriptor(string kind, string model)
    {
        using var document = JsonDocument.Parse(model);
        return new PaneDescriptor(kind, "test", string.Empty, document.RootElement.Clone());
    }

    public static PaneRegistry CreateRegistry() => PaneRegistry.Compose(HealthPanes.Modules);
}
