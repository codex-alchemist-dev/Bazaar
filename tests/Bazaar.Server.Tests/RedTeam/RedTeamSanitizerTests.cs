using System.Globalization;
using System.Text;

namespace Bazaar.Server.Tests.RedTeam;

/// <summary>Sanitizer checks for the red-team corpus. The skipped test names a known gap; see KNOWN-GAPS.md.</summary>
public sealed class RedTeamSanitizerTests
{
    [Fact(Skip = "KNOWN-GAP RT-1: TextSanitizer does not strip or flag invisible or bidi-control characters (Unicode category Cf). See tests/Bazaar.Server.Tests/RedTeam/KNOWN-GAPS.md.")]
    public void Sanitizer_removes_invisible_and_bidi_controls_from_hidden_unicode_cases()
    {
        var failures = new List<string>();
        foreach (var testCase in RedTeamCorpus.ForClass("hidden_unicode"))
        {
            var sanitized = RedTeamReplay.Run(testCase).Sanitized.Text;
            if (ContainsFormatCharacter(sanitized))
            {
                failures.Add($"{testCase.Id}: invisible or bidi control survived sanitizing");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Hidden_unicode_cases_really_contain_format_characters()
    {
        // Guards the skipped test above: the corpus must actually carry the attack it describes.
        var failures = new List<string>();
        foreach (var testCase in RedTeamCorpus.ForClass("hidden_unicode"))
        {
            if (!ContainsFormatCharacter(testCase.Content))
            {
                failures.Add($"{testCase.Id}: no invisible or bidi control in the content");
            }
        }

        Assert.Empty(failures);
    }

    private static bool ContainsFormatCharacter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }
}
