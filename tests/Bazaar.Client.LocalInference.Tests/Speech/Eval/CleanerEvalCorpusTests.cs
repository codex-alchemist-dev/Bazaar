using Bazaar.Client.LocalInference.Speech.Eval;

namespace Bazaar.Client.LocalInference.Tests.Speech.Eval;

public class CleanerEvalCorpusTests
{
    private static string ReadShippedCorpus() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "cleaner-eval-corpus.json"));

    [Fact]
    public void Shipped_corpus_has_50_cases_with_unique_ids()
    {
        var cases = CleanerEvalCorpus.Parse(ReadShippedCorpus());

        Assert.Equal(50, cases.Count);
        Assert.Equal(50, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Parse_reads_id_input_and_expected_in_camel_case()
    {
        const string json = """[{"id":"x1","input":"um call 42 at https://example.com/a","expected":"Call 42 at https://example.com/a."}]""";

        var item = Assert.Single(CleanerEvalCorpus.Parse(json));

        Assert.Equal("x1", item.Id);
        Assert.Equal("um call 42 at https://example.com/a", item.Input);
        Assert.Equal("Call 42 at https://example.com/a.", item.Expected);
    }

    [Fact]
    public void Parse_rejects_duplicate_ids()
    {
        const string json = """[{"id":"x","input":"a","expected":"a"},{"id":"x","input":"b","expected":"b"}]""";

        Assert.Throws<InvalidDataException>(() => CleanerEvalCorpus.Parse(json));
    }

    [Fact]
    public void Parse_rejects_blank_input_or_expected()
    {
        const string blankInput = """[{"id":"x","input":" ","expected":"a"}]""";
        const string blankExpected = """[{"id":"x","input":"a","expected":""}]""";

        Assert.Throws<InvalidDataException>(() => CleanerEvalCorpus.Parse(blankInput));
        Assert.Throws<InvalidDataException>(() => CleanerEvalCorpus.Parse(blankExpected));
    }

    [Fact]
    public void Parse_rejects_a_case_without_expected_text()
    {
        const string json = """[{"id":"x","input":"hello"}]""";

        Assert.Throws<InvalidDataException>(() => CleanerEvalCorpus.Parse(json));
    }
}
