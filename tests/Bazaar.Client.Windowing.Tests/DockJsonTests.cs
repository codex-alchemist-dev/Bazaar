namespace Bazaar.Client.Windowing.Tests;

/// <summary>JSON form of dock layouts: exact format, round trip, clamping, and rejection of malformed input.</summary>
public sealed class DockJsonTests
{
    private const string _paneA = """{"t":"pane","kind":"chat","id":"a"}""";
    private const string _paneB = """{"t":"pane","kind":"chat","id":"b"}""";

    public static TheoryData<string> MalformedInputs() => new()
    {
        "{",
        "not json",
        """{"t":"pane","kind":"chat","id":"a"} trailing""",
        """{"t":"widget","kind":"chat","id":"a"}""",
        """{"t":"tabs","active":0,"tabs":[]}""",
        """{"t":"pane","kind":"chat"}""",
        """{"t":"pane","kind":1,"id":"a"}""",
        """{"t":"pane","kind":"","id":"a"}""",
        """{"t":"split","o":"x","r":0.5,"a":{"t":"pane","kind":"chat","id":"a"},"b":{"t":"pane","kind":"chat","id":"b"}}""",
        """{"t":"split","o":"h","a":{"t":"pane","kind":"chat","id":"a"},"b":{"t":"pane","kind":"chat","id":"b"}}""",
        """{"t":"split","o":"h","r":0.5,"a":{"t":"pane","kind":"chat","id":"same"},"b":{"t":"pane","kind":"chat","id":"same"}}""",
        """{"t":"tabs","active":0,"tabs":[{"t":"tabs","active":0,"tabs":[{"t":"pane","kind":"chat","id":"a"}]}]}""",
        """{"t":"tabs","active":"zero","tabs":[{"t":"pane","kind":"chat","id":"a"}]}""",
        """["t","pane"]""",
        "1",
        "\"text\"",
        """{"t":"split","o":"h","r":0.5,"a":null,"b":{"t":"pane","kind":"chat","id":"b"}}""",
    };

    [Fact]
    public void Serialize_null_is_the_null_literal()
    {
        Assert.Equal("null", DockJson.Serialize(null));
    }

    [Fact]
    public void Serialize_pane_uses_the_documented_fields()
    {
        var json = DockJson.Serialize(new DockPane("chat", "gc-1"));

        Assert.Equal("""{"t":"pane","kind":"chat","id":"gc-1"}""", json);
    }

    [Fact]
    public void Serialize_tab_group_uses_the_documented_fields()
    {
        var json = DockJson.Serialize(new DockTabs(new List<DockPane> { new("chat", "a"), new("chat", "b") }, 1));

        Assert.Equal(
            """{"t":"tabs","active":1,"tabs":[{"t":"pane","kind":"chat","id":"a"},{"t":"pane","kind":"chat","id":"b"}]}""",
            json);
    }

    [Fact]
    public void Serialize_split_uses_the_documented_fields()
    {
        var json = DockJson.Serialize(new DockSplit(
            DockOrientation.Vertical,
            0.5,
            new DockPane("chat", "a"),
            new DockPane("chat", "b")));

        Assert.Equal(
            """{"t":"split","o":"v","r":0.5,"a":{"t":"pane","kind":"chat","id":"a"},"b":{"t":"pane","kind":"chat","id":"b"}}""",
            json);
    }

    [Fact]
    public void Serialize_is_deterministic()
    {
        var root = new DockSplit(DockOrientation.Horizontal, 0.37, DockShape.Tabs(1, "a", "b"), DockShape.Pane("c"));

        Assert.Equal(DockJson.Serialize(root), DockJson.Serialize(root));
    }

    [Fact]
    public void Round_trip_of_a_valid_layout_is_exact()
    {
        DockNode root = new DockSplit(
            DockOrientation.Horizontal,
            0.25,
            DockShape.Tabs(2, "a", "b", "c"),
            new DockSplit(DockOrientation.Vertical, 0.8, DockShape.Pane("d"), DockShape.Pane("e")));

        var json = DockJson.Serialize(root);
        var back = DockJson.Deserialize(json);

        Assert.Equal(DockShape.Of(root), DockShape.Of(back));
        Assert.Equal(json, DockJson.Serialize(back));
    }

    [Fact]
    public void Deserialize_null_literal_gives_null()
    {
        Assert.Null(DockJson.Deserialize("null"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_null_or_blank_input_gives_null(string? json)
    {
        Assert.Null(DockJson.Deserialize(json));
    }

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public void Deserialize_malformed_input_gives_null_and_never_throws(string json)
    {
        Assert.Null(DockJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_rejects_a_duplicate_pane_id_across_the_tree()
    {
        var tabs = """{"t":"tabs","active":0,"tabs":[{"t":"pane","kind":"chat","id":"a"}]}""";
        var split = Split("h", "0.5", tabs, """{"t":"pane","kind":"chat","id":"a"}""");

        Assert.Null(DockJson.Deserialize(split));
    }

    [Fact]
    public void Deserialize_clamps_the_ratio_into_range()
    {
        var high = DockJson.Deserialize(Split("h", "2", _paneA, _paneB));
        var low = DockJson.Deserialize(Split("v", "-1", _paneA, _paneB));

        var overflow = DockJson.Deserialize(Split("h", "1e400", _paneA, _paneB));

        Assert.Equal(0.9, Assert.IsType<DockSplit>(high).Ratio);
        Assert.Equal(0.9, Assert.IsType<DockSplit>(overflow).Ratio);
        Assert.Equal(0.1, Assert.IsType<DockSplit>(low).Ratio);
    }

    [Fact]
    public void Deserialize_clamps_the_active_index_into_its_group()
    {
        var past = DockJson.Deserialize("""{"t":"tabs","active":99,"tabs":[{"t":"pane","kind":"chat","id":"a"},{"t":"pane","kind":"chat","id":"b"}]}""");
        var negative = DockJson.Deserialize("""{"t":"tabs","active":-3,"tabs":[{"t":"pane","kind":"chat","id":"a"},{"t":"pane","kind":"chat","id":"b"}]}""");

        Assert.Equal(1, Assert.IsType<DockTabs>(past).ActiveIndex);
        Assert.Equal(0, Assert.IsType<DockTabs>(negative).ActiveIndex);
    }

    [Fact]
    public void Deserialize_accepts_a_layout_of_exactly_the_maximum_depth()
    {
        var json = DockJson.Serialize(DeepChain(DockOps.MaxDepth));

        var back = DockJson.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(DockOps.MaxDepth, DockShape.Depth(back));
    }

    [Fact]
    public void Deserialize_rejects_a_layout_deeper_than_the_maximum()
    {
        var json = DockJson.Serialize(DeepChain(DockOps.MaxDepth + 1));

        Assert.Null(DockJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_accepts_indented_json()
    {
        var json = """
            {
              "t": "split",
              "o": "h",
              "r": 0.5,
              "a": { "t": "pane", "kind": "chat", "id": "a" },
              "b": { "t": "pane", "kind": "chat", "id": "b" }
            }
            """;

        Assert.Equal("h(0.5;a;b)", DockShape.Of(DockJson.Deserialize(json)));
    }

    private static string Split(string orientation, string ratio, string first, string second) =>
        "{\"t\":\"split\",\"o\":\"" + orientation + "\",\"r\":" + ratio + ",\"a\":" + first + ",\"b\":" + second + "}";

    /// <summary>A split chain of the given depth: each level adds one split above a fresh pane.</summary>
    private static DockNode DeepChain(int depth)
    {
        DockNode node = DockShape.Pane("p0");
        for (var i = 1; i < depth; i++)
        {
            node = new DockSplit(DockOrientation.Horizontal, 0.5, DockShape.Pane($"p{i}"), node);
        }

        return node;
    }
}
