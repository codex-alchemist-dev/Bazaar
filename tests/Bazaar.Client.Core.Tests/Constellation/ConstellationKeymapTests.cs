using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Tests.Constellation;

public class ConstellationKeymapTests
{
    [Fact]
    public void Default_table_maps_the_documented_mnemonics()
    {
        Assert.Equal(ConstellationCommandKind.Focus, ConstellationKeymap.Defaults["f"]);
        Assert.Equal(ConstellationCommandKind.Back, ConstellationKeymap.Defaults["b"]);
        Assert.Equal(ConstellationCommandKind.NextNeighbour, ConstellationKeymap.Defaults["n"]);
        Assert.Equal(ConstellationCommandKind.PreviousNeighbour, ConstellationKeymap.Defaults["p"]);
        Assert.Equal(ConstellationCommandKind.ZoomIn, ConstellationKeymap.Defaults["+"]);
        Assert.Equal(ConstellationCommandKind.ZoomOut, ConstellationKeymap.Defaults["-"]);
        Assert.Equal(6, ConstellationKeymap.Defaults.Count);
    }

    [Fact]
    public void Default_table_has_no_ctrl_prefixed_or_multi_character_keys()
    {
        foreach (string key in ConstellationKeymap.Defaults.Keys)
        {
            Assert.Equal(1, key.Length);
            Assert.False(char.IsControl(key[0]), $"Control character bound: {key}");
            Assert.DoesNotContain("ctrl", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("control", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Merge_applies_overrides_and_keeps_the_defaults_intact()
    {
        var overrides = new Dictionary<string, ConstellationCommandKind> { ["x"] = ConstellationCommandKind.Forward };

        var merged = ConstellationKeymap.Merge(overrides);

        Assert.Equal(ConstellationCommandKind.Forward, merged["x"]);
        Assert.Equal(ConstellationCommandKind.Back, merged["b"]);
        Assert.False(ConstellationKeymap.Defaults.ContainsKey("x"));
    }

    [Fact]
    public void Merge_can_rebind_a_default_key()
    {
        var overrides = new Dictionary<string, ConstellationCommandKind> { ["b"] = ConstellationCommandKind.Forward };

        var merged = ConstellationKeymap.Merge(overrides);

        Assert.Equal(ConstellationCommandKind.Forward, merged["b"]);
    }

    [Theory]
    [InlineData("ctrl+f")]
    [InlineData("Ctrl+b")]
    [InlineData("")]
    [InlineData("\u0006")]
    [InlineData(" ")]
    public void Merge_rejects_chords_empty_and_control_keys(string key)
    {
        var overrides = new Dictionary<string, ConstellationCommandKind> { [key] = ConstellationCommandKind.Back };

        Assert.ThrowsAny<ArgumentException>(() => ConstellationKeymap.Merge(overrides));
    }

    [Fact]
    public void TryResolve_finds_bound_keys_and_rejects_others()
    {
        Assert.True(ConstellationKeymap.TryResolve("n", out ConstellationCommandKind next));
        Assert.Equal(ConstellationCommandKind.NextNeighbour, next);

        Assert.False(ConstellationKeymap.TryResolve("q", out _));
        Assert.False(ConstellationKeymap.TryResolve(null!, out _));
    }

    [Fact]
    public void TryResolve_uses_a_supplied_map()
    {
        var map = ConstellationKeymap.Merge(new Dictionary<string, ConstellationCommandKind>
        {
            ["x"] = ConstellationCommandKind.Forward,
        });

        Assert.True(ConstellationKeymap.TryResolve(map, "x", out ConstellationCommandKind forward));
        Assert.Equal(ConstellationCommandKind.Forward, forward);
        Assert.False(ConstellationKeymap.TryResolve(ConstellationKeymap.Defaults, "x", out _));
    }
}
