using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GC.Tests;

/// <summary>The pane modules a head ships around the GC module, composed the way the heads compose them.</summary>
internal static class GcTestPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } = [.. FirstPartyPanes.Modules, GcPanes.Module];

    public static PaneRegistry CreateRegistry(ChatViewModel? chat = null) =>
        FirstPartyPanes.CreateRegistry(Modules, configure: context => context.WithChat(chat));

    public static PaneDescriptor Descriptor(string kind) => new(kind, "Title", string.Empty, JsonElement.Parse("{}"));
}
