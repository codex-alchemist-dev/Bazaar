using Avalonia.Controls;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>Collection widgets: list (keyed, one item template) and tabs.</summary>
public sealed partial class ViewMapper
{
    /// <summary>
    /// A keyed list (doc 41c, section 9.4). <c>over</c> names a declared list source, <c>key</c> one of its fields. The
    /// records come from the head under that name; each item's fields are in scope in the one item template, typed as the
    /// source declares them.
    /// </summary>
    private KeyedListPanel MapList(ViewNode node, Scope scope)
    {
        string overName = LiteralString(node, "over");
        if (!scope.Document.Data.TryGetValue(overName, out ViewDataSource? source) || source.Fields is null)
        {
            throw new ViewDeclarationException($"'over' must name a declared list source, not '{overName}'.");
        }

        string keyField = LiteralString(node, "key");
        if (!source.Fields.ContainsKey(keyField))
        {
            throw new ViewDeclarationException($"'key' must name a field of '{overName}'.");
        }

        if (node.Children.Count != 1)
        {
            throw new ViewDeclarationException("needs exactly one item template.");
        }

        ViewNode template = node.Children[0];
        if (!scope.Values.TryGetValue(overName, out object? data) || data is not IEnumerable<IReadOnlyDictionary<string, object?>> items)
        {
            throw new ViewDeclarationException($"'{overName}' must be a list of records.");
        }

        var identifiers = new Dictionary<string, RubedoType>(scope.Schema.Identifiers, StringComparer.Ordinal);
        foreach ((string field, RubedoType type) in source.Fields)
        {
            // The validator reports a field that shadows a name in scope; here the outer name wins.
            identifiers.TryAdd(field, type);
        }

        var itemSchema = new RubedoSchema(identifiers);
        var list = new KeyedListPanel(keyField, item => Node(template, new Scope(scope.Document, item, itemSchema)));
        list.Update(items);
        return list;
    }

    /// <summary>
    /// Tabs (doc 41c, section 3): at least one <c>tab</c> child, nothing else. A tab has a non-empty title and exactly one page
    /// child. Any violation fails the whole tabs node, as the validator does.
    /// </summary>
    private TabControl MapTabs(ViewNode node, Scope scope)
    {
        if (node.Children.Count == 0)
        {
            throw new ViewDeclarationException("needs at least one tab.");
        }

        var tabs = new TabControl();
        foreach (ViewNode tab in node.Children)
        {
            if (tab.Widget != "tab")
            {
                throw new ViewDeclarationException($"a child of tabs must be a tab, not '{tab.Widget}'.");
            }

            string title = Text(tab, "title", scope);
            if (title.Length == 0)
            {
                throw new ViewDeclarationException("a tab needs a non-empty title.");
            }

            if (tab.Children.Count != 1)
            {
                throw new ViewDeclarationException("each tab needs exactly one child.");
            }

            tabs.Items.Add(new TabItem { Header = title, Content = Node(tab.Children[0], scope) });
        }

        return tabs;
    }
}
