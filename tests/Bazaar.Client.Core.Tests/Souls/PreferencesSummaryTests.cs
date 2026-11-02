using Bazaar.Client.Core.Souls;

namespace Bazaar.Client.Core.Tests.Souls;

public class PreferencesSummaryTests
{
    [Fact]
    public void Keys_are_sorted_and_each_value_type_is_described()
    {
        var text = PreferencesSummary.Format("{\"z\":true,\"a\":\"dry\",\"m\":3.5,\"n\":null,\"o\":{\"x\":1},\"p\":[1,2,3]}");

        // Six keys: the first five in sorted order are shown, the sixth is counted.
        Assert.Equal("a: dry, m: 3.5, n: null, o: {1 keys}, p: [3 items], +1 more", text);
    }

    [Fact]
    public void Long_string_values_are_cut_with_an_ellipsis()
    {
        var longValue = new string('x', 60);

        var text = PreferencesSummary.Format($"{{\"note\":\"{longValue}\"}}");

        Assert.Equal("note: " + new string('x', 39) + "…", text);
    }

    [Fact]
    public void More_than_five_entries_are_counted_not_listed()
    {
        var text = PreferencesSummary.Format("{\"a\":1,\"b\":2,\"c\":3,\"d\":4,\"e\":5,\"f\":6,\"g\":7}");

        Assert.Equal("a: 1, b: 2, c: 3, d: 4, e: 5, +2 more", text);
    }

    [Fact]
    public void Empty_object_reads_as_none_recorded()
    {
        Assert.Equal(PreferencesSummary.NoneRecorded, PreferencesSummary.Format("{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_preferences_read_as_none_recorded(string? json)
    {
        Assert.Equal(PreferencesSummary.NoneRecorded, PreferencesSummary.Format(json));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void Unparseable_or_non_object_preferences_read_as_unreadable(string json)
    {
        Assert.Equal(PreferencesSummary.Unreadable, PreferencesSummary.Format(json));
    }
}
