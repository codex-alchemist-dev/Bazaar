using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Tests.Redaction;

public class RedactorTests
{
    [Fact]
    public void Never_field_is_replaced_whole_and_counted()
    {
        var result = new Redactor().Redact(RedactionClass.Never, "a gc message body with alice@example.com");

        Assert.Equal(RedactionMarkers.Structural, result.Text);
        Assert.Equal(new RedactionCounts(0, 0, 1, 0), result.Counts);
        Assert.Equal(RedactionStatus.Ok, result.Status);
    }

    [Fact]
    public void Null_input_gives_empty_text()
    {
        var result = new Redactor().Redact(RedactionClass.FreeText, null);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(RedactionCounts.None, result.Counts);
    }

    [Fact]
    public void Exact_values_are_replaced_case_insensitively_and_counted()
    {
        var redactor = new Redactor(["DESKTOP-QX71"]);

        var result = redactor.Redact(RedactionClass.FreeText, "ran on desktop-qx71 at 10:00");

        Assert.Equal("ran on " + RedactionMarkers.Exact + " at 10:00", result.Text);
        Assert.Equal(1, result.Counts.Exact);
    }

    [Fact]
    public void Longer_exact_value_is_replaced_whole()
    {
        var result = new Redactor(["abc", "abcdef"]).Redact(RedactionClass.FreeText, "x abcdef y");

        Assert.Equal("x " + RedactionMarkers.Exact + " y", result.Text);
        Assert.Equal(1, result.Counts.Exact);
    }

    [Fact]
    public void Exact_value_order_does_not_change_output()
    {
        const string input = "host alpha-node and beta-node and alpha-node-2";

        var first = new Redactor(["alpha-node", "beta-node", "alpha-node-2"]).Redact(RedactionClass.FreeText, input);
        var second = new Redactor(["alpha-node-2", "beta-node", "alpha-node"]).Redact(RedactionClass.FreeText, input);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Overlong_string_is_cut_and_marked_with_the_count_dropped()
    {
        var input = new string('a', 5000);

        var result = new Redactor().Redact(RedactionClass.FreeText, input);

        Assert.Equal(new string('a', Redactor.MaxStringLength) + RedactionMarkers.Truncated(5000 - Redactor.MaxStringLength), result.Text);
        Assert.Equal(1, result.Counts.Truncated);
    }

    [Fact]
    public void Throwing_rule_fails_closed_and_keeps_no_original_text()
    {
        var rule = new RedactionRule("test.throws", new Regex("secret"), _ => throw new InvalidOperationException("boom"));
        var redactor = new Redactor([rule], null);

        var result = redactor.Redact(RedactionClass.FreeText, "secret value");

        Assert.Equal(RedactionMarkers.Failed, result.Text);
        Assert.Equal(RedactionStatus.Failed, result.Status);
    }

    [Fact]
    public void Rule_that_still_matches_after_redaction_holds_the_field()
    {
        // The evaluator adds a character, so the rule matches its own output on the residual scan.
        var rule = new RedactionRule("test.residual", new Regex("a"), match => "x" + match.Value);
        var redactor = new Redactor([rule], null);

        var result = redactor.Redact(RedactionClass.FreeText, "a");

        Assert.Equal(RedactionMarkers.Residual, result.Text);
        Assert.Equal(RedactionStatus.Residual, result.Status);
    }

    [Fact]
    public void Repeated_calls_give_identical_results()
    {
        var redactor = new Redactor(["DESKTOP-QX71"]);
        const string input = "C:\\Users\\alice\\x mail alice@example.com ip 10.0.0.9 key sk-ant-AAAAAAAAAAAAAAAAAAAAAAAA desktop-qx71";
        var expected = redactor.Redact(RedactionClass.FreeText, input);

        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(expected, redactor.Redact(RedactionClass.FreeText, input));
        }
    }

    [Fact]
    public void Adversarial_near_matches_finish_quickly()
    {
        var builder = new StringBuilder();
        while (builder.Length < 1 << 20)
        {
            builder.Append("ghp_abcdefghij1234567890 1.2.3 a@b. 00:11:22:33:44:5 :: ");
        }

        var watch = Stopwatch.StartNew();
        var result = new Redactor().Redact(RedactionClass.FreeText, builder.ToString());
        watch.Stop();

        Assert.Equal(RedactionStatus.Ok, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Seeded_generated_secrets_never_survive()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random(20261113);
        var redactor = new Redactor();

        for (var i = 0; i < 200; i++)
        {
            var token = "ghp_" + RandomText(random, alphabet, 36);
            var email = RandomText(random, alphabet, 8) + "@" + RandomText(random, alphabet, 6) + ".org";
            var address = $"{random.Next(1, 127)}.{random.Next(0, 256)}.{random.Next(0, 256)}.{random.Next(1, 255)}";
            var input = $"event {i}: token {token} sent to {email} from {address} done";

            var result = redactor.Redact(RedactionClass.FreeText, input);

            Assert.DoesNotContain(token, result.Text);
            Assert.DoesNotContain(email, result.Text);
            Assert.DoesNotContain(address, result.Text);
        }
    }

    [Fact]
    public void Redaction_assembly_references_no_network_gateway_or_model_assembly()
    {
        var references = typeof(Redactor).Assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty);
        string[] banned = ["Bazaar", "Http", "Sockets", "Gateway", "Onnx", "Npu", "Model"];

        foreach (var name in references)
        {
            Assert.DoesNotContain(banned, banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Rule_ids_are_unique_and_the_version_is_set()
    {
        var ids = RedactionRules.V1.Select(rule => rule.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Matches(@"^\d+\.\d+\.\d+$", RedactionRules.Version);
    }

    private static string RandomText(Random random, string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }
}
