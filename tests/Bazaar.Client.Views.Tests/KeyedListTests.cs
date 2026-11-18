using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Views.Tests.Fixtures;

namespace Bazaar.Client.Views.Tests;

/// <summary>A keyed list builds one template per item, reuses items by key, and rejects bad keys.</summary>
public class KeyedListTests
{
    private static Dictionary<string, object?> Item(string id, string title) =>
        ViewFixtures.Data(("id", id), ("title", title));

    private static KeyedListPanel NewList() =>
        new("id", record => new TextBlock { Text = (string)record["title"]! });

    [AvaloniaFact]
    public void Builds_one_template_per_item()
    {
        var harness = new Harness();

        var list = Assert.IsType<KeyedListPanel>(harness.Build(ViewFixtures.Minimal("list")));

        Assert.Equal(2, list.TemplateBuilds);
        Assert.Equal(2, list.Children.Count);
        Assert.Equal("First", Assert.IsType<TextBlock>(list.Children[0]).Text);
        Assert.Equal("Second", Assert.IsType<TextBlock>(list.Children[1]).Text);
    }

    [AvaloniaFact]
    public void Update_with_the_same_records_reuses_every_control()
    {
        var items = new List<IReadOnlyDictionary<string, object?>> { Item("a", "First"), Item("b", "Second") };
        var list = NewList();
        list.Update(items);
        var before = list.Children.ToList();

        list.Update(items);

        Assert.Equal(2, list.TemplateBuilds);
        Assert.Same(before[0], list.Children[0]);
        Assert.Same(before[1], list.Children[1]);
    }

    [AvaloniaFact]
    public void Update_builds_only_new_keys_and_removes_gone_keys_and_keeps_order()
    {
        var first = Item("a", "First");
        var list = NewList();
        list.Update([first, Item("b", "Second")]);
        var keptControl = list.Children[0];

        list.Update([Item("c", "Third"), first]);

        // Two builds for the first update, one for the new key "c"; the kept record is not rebuilt.
        Assert.Equal(3, list.TemplateBuilds);
        Assert.Equal(2, list.Children.Count);
        Assert.Equal("Third", Assert.IsType<TextBlock>(list.Children[0]).Text);
        Assert.Same(keptControl, list.Children[1]);
    }

    [AvaloniaFact]
    public void A_known_key_with_a_new_record_is_rebuilt_so_the_view_is_not_stale()
    {
        var list = NewList();
        list.Update([Item("a", "First")]);

        list.Update([Item("a", "Renamed")]);

        Assert.Equal(2, list.TemplateBuilds);
        Assert.Equal("Renamed", Assert.IsType<TextBlock>(list.Children[0]).Text);
    }

    [AvaloniaFact]
    public void Duplicate_keys_fail_the_list_before_anything_changes()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("list", [ViewFixtures.Text("row")], ("over", ViewFixtures.Lit("items")), ("key", ViewFixtures.Lit("id")));
        var data = Harness.DefaultData();
        data["items"] = new List<IReadOnlyDictionary<string, object?>> { Item("a", "x"), Item("a", "y") };

        Control control = harness.Build(ViewFixtures.ListDocument(node), data: data);

        Assert.Contains("duplicate list key 'a'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void An_item_without_the_key_field_fails_the_list()
    {
        var harness = new Harness();
        var data = Harness.DefaultData();
        data["items"] = new List<IReadOnlyDictionary<string, object?>> { ViewFixtures.Data(("title", "no id")) };
        var node = ViewFixtures.Minimal("list").Root;

        Control control = harness.Build(ViewFixtures.ListDocument(node), data: data);

        Assert.Contains("no text key field 'id'", ViewFixtures.PlaceholderText(control));
    }
}
