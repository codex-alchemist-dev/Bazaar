namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// Splits the registered panes into pager pages for a layout variant (doc 29, layout table). Folded shows one
/// pane per page; unfolded shows two side by side. Order is the registry order. Pure, so it has no UI.
/// </summary>
public static class PanePages
{
    /// <summary>Panes shown on one page: 1 folded, 2 unfolded.</summary>
    public static int PanesPerPage(LayoutVariant variant) => variant == LayoutVariant.Unfolded ? 2 : 1;

    /// <summary>Groups the pane kinds into pages, keeping their order. The last page may hold fewer panes.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Paginate(LayoutVariant variant, IReadOnlyList<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        return kinds.Chunk(PanesPerPage(variant)).Select(page => (IReadOnlyList<string>)page).ToList();
    }
}
