using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Bazaar.Client.Views.Tests.Fixtures;

/// <summary>Walks a built control tree, so a test can assert on what the mapper produced anywhere in it.</summary>
internal static class ViewTree
{
    public static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (ILogical child in root.GetLogicalChildren())
        {
            if (child is Control control)
            {
                foreach (Control inner in Descendants(control))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>Every failure card in the tree. A view that renders correctly has none.</summary>
    public static List<Control> Placeholders(Control root) =>
        Descendants(root).Where(c => ViewFixtures.AsPlaceholder(c) is not null).ToList();

    /// <summary>The text of every text block in the tree, in order.</summary>
    public static List<string> Texts(Control root) =>
        Descendants(root).OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
}
