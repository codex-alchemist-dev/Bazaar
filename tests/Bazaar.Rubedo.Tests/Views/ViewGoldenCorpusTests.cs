using System.Text.Json;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Tests.Views;

/// <summary>
/// Runs the shared view golden corpus (doc 41c, section 8). Every head reads the same JSON file, so a disagreement in
/// reading or validation fails here. Each case expects either an exact ordered list of diagnostic codes (empty when valid)
/// or a read error from <see cref="ViewDocumentReader"/>.
/// </summary>
public class ViewGoldenCorpusTests
{
    [Fact]
    public void Every_view_golden_case_matches_the_reader_and_validator()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "rubedo-views-v1.json");
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = corpus.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("rubedo-views", root.GetProperty("language").GetString());

        var failures = new List<string>();
        int count = 0;
        foreach (JsonElement caseElement in root.GetProperty("cases").EnumerateArray())
        {
            count++;
            string name = caseElement.GetProperty("name").GetString()!;
            string? problem = Check(caseElement);
            if (problem is not null)
            {
                failures.Add($"{name}: {problem}");
            }
        }

        Assert.True(count >= 25, $"The view corpus has {count} cases; expected at least 25.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string? Check(JsonElement caseElement)
    {
        JsonElement expect = caseElement.GetProperty("expect");
        string documentJson = caseElement.GetProperty("document").GetRawText();

        if (expect.TryGetProperty("readError", out JsonElement readError) && readError.GetBoolean())
        {
            try
            {
                ViewDocumentReader.Read(documentJson);
                return "expected a read error, but the document was read";
            }
            catch (FormatException)
            {
                return null;
            }
        }

        ViewDocument document;
        try
        {
            document = ViewDocumentReader.Read(documentJson);
        }
        catch (FormatException ex)
        {
            return $"unexpected read error: {ex.Message}";
        }

        string[] actual = ViewValidator.Validate(document).Select(d => d.Code).ToArray();
        string[] wanted = expect.GetProperty("codes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return actual.SequenceEqual(wanted, StringComparer.Ordinal)
            ? null
            : $"expected [{string.Join(", ", wanted)}] but got [{string.Join(", ", actual)}]";
    }
}
