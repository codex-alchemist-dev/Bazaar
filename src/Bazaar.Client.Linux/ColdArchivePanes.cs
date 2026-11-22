using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.ColdArchive;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Linux;

/// <summary>The Linux head's cold-storage pane (doc 25 "Archive cold storage" Client, PROPOSED): shown by the <c>--cold-archive</c> launch flag.</summary>
internal static class LinuxColdArchivePanes
{
    internal const string Flag = "--cold-archive";

    /// <summary>The registry holding the cold-storage module, with the owner's HTTP client in the context.</summary>
    internal static PaneRegistry Compose(IColdArchiveClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return PaneRegistry.Compose(PaneViews.Instance, ColdArchivePanes.Modules, new PaneModuleContext().With(client));
    }

    /// <summary>Shows the pane in <paramref name="window"/> in place of the start content.</summary>
    internal static void Show(MainWindow window, IColdArchiveClient client)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowPanes([new PaneDescriptor(PaneKinds.ColdArchive, "Cold storage", string.Empty, JsonElement.Parse("{}"))], Compose(client));
    }
}
