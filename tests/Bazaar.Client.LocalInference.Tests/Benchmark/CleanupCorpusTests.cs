using Bazaar.Client.LocalInference.Benchmark;

namespace Bazaar.Client.LocalInference.Tests.Benchmark;

public class CleanupCorpusTests
{
    private static string ReadShippedCorpus() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "cleanup-corpus.json"));

    [Fact]
    public void Shipped_corpus_has_20_items_and_each_has_a_nonempty_mustKeep()
    {
        var items = CleanupCorpus.Parse(ReadShippedCorpus());

        Assert.Equal(20, items.Count);
        Assert.All(items, item => Assert.NotEmpty(item.MustKeep));
        Assert.Equal(20, items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void Parse_reads_id_input_and_mustKeep_in_camel_case()
    {
        const string json = """[{"id":"x1","input":"call 42 at https://example.com/a","mustKeep":["42","https://example.com/a"]}]""";

        var item = Assert.Single(CleanupCorpus.Parse(json));

        Assert.Equal("x1", item.Id);
        Assert.Equal("call 42 at https://example.com/a", item.Input);
        string[] expectedKeep = ["42", "https://example.com/a"];
        Assert.Equal(expectedKeep, item.MustKeep);
    }

    [Fact]
    public void Parse_rejects_duplicate_ids()
    {
        const string json = """[{"id":"x","input":"a","mustKeep":[]},{"id":"x","input":"b","mustKeep":[]}]""";

        Assert.Throws<InvalidDataException>(() => CleanupCorpus.Parse(json));
    }

    [Fact]
    public void Parse_rejects_an_item_with_empty_input()
    {
        const string json = """[{"id":"x","input":"  ","mustKeep":["1"]}]""";

        Assert.Throws<InvalidDataException>(() => CleanupCorpus.Parse(json));
    }

    [Fact]
    public void Parse_rejects_an_item_without_a_mustKeep_list()
    {
        const string json = """[{"id":"x","input":"hello"}]""";

        Assert.Throws<InvalidDataException>(() => CleanupCorpus.Parse(json));
    }
}
