using Bazaar.Engines.Godot.SceneDiff;

namespace Bazaar.Engines.Tests.Godot;

public class SceneDiffTests
{
    [Fact]
    public void Identical_files_give_no_changes()
    {
        string scene = """
        [gd_scene load_steps=2 format=3]

        [node name="Root" type="Node2D"]
        position = Vector2(0, 0)

        [node name="Child" type="Sprite2D" parent="."]
        position = Vector2(10, 20)
        """;

        Assert.Empty(SceneDiff.Compare(scene, scene));
    }

    [Fact]
    public void Property_edit_on_one_node_is_one_PropertyChanged()
    {
        string before = """
        [gd_scene format=3]

        [node name="Root" type="Node2D"]

        [node name="Child" type="Sprite2D" parent="."]
        position = Vector2(10, 20)
        """;
        string after = before.Replace("Vector2(10, 20)", "Vector2(30, 20)", StringComparison.Ordinal);

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange> { new PropertyChanged("Child", "position", "Vector2(10, 20)", "Vector2(30, 20)") },
            changes);
    }

    [Fact]
    public void Added_and_removed_nodes_are_reported_by_path_in_stable_order()
    {
        string before = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Texture2D" path="res://icon.png" id="1_abc"]

        [node name="Root" type="Node2D"]
        visible = false

        [node name="Child" type="Sprite2D" parent="."]
        position = Vector2(10, 20)
        texture = ExtResource("1_abc")

        [node name="Old" type="Label" parent="."]
        text = "old"
        """;
        string after = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Texture2D" path="res://icon.png" id="1_abc"]

        [node name="Root" type="Node2D"]

        [node name="Child" type="Sprite2D" parent="."]
        position = Vector2(30, 20)
        texture = ExtResource("1_abc")

        [node name="New" type="Label" parent="Child"]
        text = "new"
        """;

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange>
            {
                new PropertyChanged(".", "visible", "false", null),
                new PropertyChanged("Child", "position", "Vector2(10, 20)", "Vector2(30, 20)"),
                new NodeAdded("Child/New", "Label"),
                new NodeRemoved("Old"),
            },
            changes);
    }

    [Fact]
    public void Instanced_node_without_type_is_added_with_null_type()
    {
        string before = """
        [gd_scene format=3]
        [ext_resource type="PackedScene" path="res://x.tscn" id="2_x"]
        [node name="Root" type="Node"]
        """;
        string after = before + "\n[node name=\"Inst\" parent=\".\" instance=ExtResource(\"2_x\")]";

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(new List<SceneChange> { new NodeAdded("Inst", null) }, changes);
    }

    [Fact]
    public void Multi_line_value_is_one_value_and_reported_once()
    {
        string before = """
        [gd_scene format=3]

        [node name="Root" type="Node"]
        data = {
        "items": [
          1,
          2
        ],
        "name": "a=b"
        }
        """;
        string after = before.Replace("  2\n", "  3\n", StringComparison.Ordinal);

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange>
            {
                new PropertyChanged(
                    ".",
                    "data",
                    "{\n\"items\": [\n  1,\n  2\n],\n\"name\": \"a=b\"\n}",
                    "{\n\"items\": [\n  1,\n  3\n],\n\"name\": \"a=b\"\n}"),
            },
            changes);
    }

    [Fact]
    public void Duplicate_node_path_is_a_parse_error_with_the_line_of_the_second_header()
    {
        string valid = """
        [gd_scene format=3]

        [node name="Root" type="Node"]
        """;
        string duplicate = """
        [gd_scene format=3]

        [node name="Root" type="Node"]

        [node name="Dup" type="Node" parent="."]

        [node name="Dup" type="Node" parent="."]
        """;

        var error = Assert.Throws<SceneParseException>(() => SceneDiff.Compare(valid, duplicate));

        Assert.Equal(7, error.Line);
        Assert.Contains("duplicate node path 'Dup'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Property_added_and_removed_are_both_reported()
    {
        string before = """
        [gd_scene format=3]

        [node name="Root" type="Node2D"]
        visible = false
        """;
        string after = """
        [gd_scene format=3]

        [node name="Root" type="Node2D"]
        modulate = Color(1, 1, 1, 1)
        """;

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange>
            {
                new PropertyChanged(".", "modulate", null, "Color(1, 1, 1, 1)"),
                new PropertyChanged(".", "visible", "false", null),
            },
            changes);
    }

    [Fact]
    public void Resource_path_and_sub_resource_property_changes_are_reported()
    {
        string before = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://a.gd" id="1_a"]

        [sub_resource type="RectangleShape2D" id="RectangleShape2D_1"]
        size = Vector2(10, 10)

        [node name="Root" type="Node2D"]
        """;
        string after = before
            .Replace("res://a.gd", "res://b.gd", StringComparison.Ordinal)
            .Replace("Vector2(10, 10)", "Vector2(20, 10)", StringComparison.Ordinal);

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange>
            {
                new ResourceChanged("ext_resource", "1_a", "@path", "res://a.gd", "res://b.gd"),
                new ResourceChanged("sub_resource", "RectangleShape2D_1", "size", "Vector2(10, 10)", "Vector2(20, 10)"),
            },
            changes);
    }

    [Fact]
    public void Main_resource_section_of_a_tres_file_is_diffed()
    {
        string before = """
        [gd_resource type="Resource" format=3]

        [resource]
        name = "x"
        value = 1
        """;
        string after = before.Replace("value = 1", "value = 2", StringComparison.Ordinal);

        var changes = SceneDiff.Compare(before, after);

        Assert.Equal(
            new List<SceneChange> { new ResourceChanged("resource", string.Empty, "value", "1", "2") },
            changes);
    }

    [Fact]
    public void Property_before_any_section_is_a_parse_error_on_its_line()
    {
        string bad = """
        x = 1
        [node name="Root" type="Node"]
        """;
        string valid = """
        [node name="Root" type="Node"]
        """;

        var error = Assert.Throws<SceneParseException>(() => SceneDiff.Compare(bad, valid));

        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void Unclosed_multi_line_value_is_a_parse_error_on_its_first_line()
    {
        string bad = """
        [node name="Root" type="Node"]
        data = [1,
        2
        """;
        string valid = """
        [node name="Root" type="Node"]
        """;

        var error = Assert.Throws<SceneParseException>(() => SceneDiff.Compare(valid, bad));

        Assert.Equal(2, error.Line);
    }
}
