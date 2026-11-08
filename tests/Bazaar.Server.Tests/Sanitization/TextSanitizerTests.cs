using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Tests.Sanitization;

public class TextSanitizerTests
{
    private static readonly TextSanitizer _sanitizer = new();

    private static SensitiveTerms TermsFor(RedactionKind kind, string term) => kind switch
    {
        RedactionKind.Person => SensitiveTerms.Empty with { People = [term] },
        RedactionKind.Agent => SensitiveTerms.Empty with { Agents = [term] },
        RedactionKind.Repository => SensitiveTerms.Empty with { Repositories = [term] },
        RedactionKind.Customer => SensitiveTerms.Empty with { Customers = [term] },
        RedactionKind.ExternalQuote => SensitiveTerms.Empty with { ExternalQuotes = [term] },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No term list for this kind."),
    };

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "[secret]", RedactionKind.Secret)]
    [InlineData("use sk-ant-abcdefghij12 now", "use [secret] now", RedactionKind.Secret)]
    [InlineData("auth Bearer abcdef123456xyz end", "auth [secret] end", RedactionKind.Secret)]
    [InlineData("password=hunter2 ok", "[secret] ok", RedactionKind.Secret)]
    [InlineData("clone https://bob:s3cr3t@git.example.com/x", "clone https://[secret]@git.example.com/x", RedactionKind.Secret)]
    [InlineData("reply:\n> line one\n> line two\nafter", "reply:\n[quoted text removed]\nafter", RedactionKind.QuotedText)]
    [InlineData("see [[Project Notes]] now", "see [vault-link] now", RedactionKind.VaultLink)]
    [InlineData("open notes/people/alice.md today", "open [vault-path] today", RedactionKind.VaultPath)]
    [InlineData("see vault/secret/plan.txt here", "see [vault-path] here", RedactionKind.VaultPath)]
    [InlineData("mail bob@example.org now", "mail [email] now", RedactionKind.Email)]
    public void Each_fixed_rule_replaces_its_span(string text, string expected, RedactionKind kind)
    {
        var result = _sanitizer.Sanitize(text, SensitiveTerms.Empty);

        Assert.Equal(expected, result.Text);
        Assert.Equal(new[] { new Redaction(kind, 1) }, result.Redactions.ToArray());
    }

    [Theory]
    [InlineData(RedactionKind.Person, "Alice wrote it", "[person] wrote it")]
    [InlineData(RedactionKind.Agent, "Charles wrote it", "[agent] wrote it")]
    [InlineData(RedactionKind.Repository, "Bazaar wrote it", "[repo] wrote it")]
    [InlineData(RedactionKind.Customer, "Initech wrote it", "[customer] wrote it")]
    [InlineData(RedactionKind.ExternalQuote, "Quote here", "[external quote removed] here")]
    public void Term_lists_replace_their_kind_with_its_placeholder(RedactionKind kind, string text, string expected)
    {
        var term = text.Split(' ')[0];

        var result = _sanitizer.Sanitize(text, TermsFor(kind, term));

        Assert.Equal(expected, result.Text);
        Assert.Equal(new[] { new Redaction(kind, 1) }, result.Redactions.ToArray());
    }

    [Fact]
    public void Name_match_is_whole_word_and_case_insensitive()
    {
        var terms = SensitiveTerms.Empty with { People = ["Alice"] };

        var result = _sanitizer.Sanitize("Alicebot and Alice and ALICE", terms);

        Assert.Equal("Alicebot and [person] and [person]", result.Text);
        Assert.Equal(new[] { new Redaction(RedactionKind.Person, 2) }, result.Redactions.ToArray());
    }

    [Fact]
    public void Email_inside_block_quote_is_removed_once_as_quoted_text()
    {
        var result = _sanitizer.Sanitize("> contact bob@example.org please", SensitiveTerms.Empty);

        Assert.Equal("[quoted text removed]", result.Text);
        Assert.Equal(new[] { new Redaction(RedactionKind.QuotedText, 1) }, result.Redactions.ToArray());
    }

    [Fact]
    public void Sanitizing_twice_gives_the_same_text()
    {
        var terms = new SensitiveTerms(["Alice"], ["Charles"], ["Bazaar"], ["Initech"], ["secret plan"]);
        const string text = "Alice and Charles met at Bazaar for Initech.\n> Charles said the secret plan\n" +
            "mail bob@example.org, password=hunter2, see [[Notes]] and notes/x.md";

        var once = _sanitizer.Sanitize(text, terms);
        var twice = _sanitizer.Sanitize(once.Text, terms);

        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void Empty_text_and_empty_terms_return_input_unchanged()
    {
        var result = _sanitizer.Sanitize(string.Empty, SensitiveTerms.Empty);

        Assert.Equal(string.Empty, result.Text);
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void Text_without_matches_is_returned_unchanged()
    {
        const string text = "A plain sentence with nothing to remove.";

        var result = _sanitizer.Sanitize(text, SensitiveTerms.Empty);

        Assert.Equal(text, result.Text);
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize(null!, SensitiveTerms.Empty));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", null!));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", new SensitiveTerms(null!, [], [], [], [])));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", new SensitiveTerms([], null!, [], [], [])));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", new SensitiveTerms([], [], null!, [], [])));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", new SensitiveTerms([], [], [], null!, [])));
        Assert.Throws<ArgumentNullException>(() => _sanitizer.Sanitize("text", new SensitiveTerms([], [], [], [], null!)));
    }

    [Fact]
    public void Counts_are_reported_per_kind_in_enum_order()
    {
        var terms = SensitiveTerms.Empty with { People = ["Alice", "Bob"] };

        var result = _sanitizer.Sanitize("Alice met Bob; Alice left. bob@example.org token=abc123", terms);

        Assert.Equal(
            new[]
            {
                new Redaction(RedactionKind.Person, 3),
                new Redaction(RedactionKind.Email, 1),
                new Redaction(RedactionKind.Secret, 1),
            },
            result.Redactions.ToArray());
    }

    [Fact]
    public void Redactions_never_carry_the_removed_text()
    {
        const string secret = "hunter2-very-secret";

        var result = _sanitizer.Sanitize($"password={secret} and Alice", SensitiveTerms.Empty with { People = ["Alice"] });

        Assert.DoesNotContain(secret, result.Text);
        foreach (var redaction in result.Redactions)
        {
            Assert.DoesNotContain(secret, redaction.ToString());
            Assert.DoesNotContain("Alice", redaction.ToString());
        }
    }
}
