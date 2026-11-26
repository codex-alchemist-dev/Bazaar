using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

// Doc 44 section 8.5. Fake secrets are built at run time so no key-shaped literal sits in the repository.
public class ImportSecretGateTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static string AwsKey() => "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    private static string GithubToken() => "gh" + "p_" + new string('a', 20) + "Z9y8X7w6V5u4T3s2R1q0";
    private static string PemBlock() => "-----BEGIN " + "PRIVATE KEY-----\nMIIEvQIBADANBgkq\nabcdef\n-----END " + "PRIVATE KEY-----";
    private static string PostgresUrl() => "postgres" + "://admin:hunter2pass@db.internal:5432/app";
    private static string Jwt() => "ey" + "J" + new string('a', 12) + ".ey" + "J" + new string('b', 12) + "." + new string('c', 12);

    private static SourceRecord Record(string key, string title, string body) =>
        new(key, "note", title, body, new Dictionary<string, string> { ["format"] = "test" });

    private static ImportPreview Preview(params SourceRecord[] records) => new(records, []);

    private static SecretScanOptions Quarantine => SecretScanOptions.ForExternal(SecretScanPolicy.Quarantine);

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Each_secret_shape_is_found_with_its_line(string expectedRule, string secret)
    {
        var record = Record("k", "Title", "line one\nline two " + secret + "\nline three");

        var finding = Assert.Single(ImportSecretScanner.Scan(record), f => f.RuleId == expectedRule);

        Assert.Equal(SecretField.Body, finding.Field);
        Assert.Equal(2, finding.Line);
    }

    public static TheoryData<string, string> Secrets() => new()
    {
        { "aws-access-key-id", AwsKey() },
        { "github-token", GithubToken() },
        { "private-key-block", PemBlock() },
        { "connection-string-url", PostgresUrl() },
        { "json-web-token", Jwt() },
        { "anthropic-api-key", "sk" + "-ant-" + "abcdefghij1234567890" },
        { "slack-token", "xo" + "xb-" + "1234567890-abcdefghij" },
        { "google-api-key", "AI" + "za" + new string('A', 35) },
        { "stripe-key", "sk" + "_live_" + new string('a', 20) },
    };

    [Fact]
    public void A_credential_assignment_needs_enough_entropy()
    {
        var weak = Record("a", "t", "password: aaaaaaaaaa");
        var strong = Record("b", "t", "password: Zx9!qLm2#Rt7");

        Assert.Empty(ImportSecretScanner.Scan(weak));
        Assert.Contains(ImportSecretScanner.Scan(strong), f => f.RuleId == "credential-assignment");
    }

    [Fact]
    public void A_secret_in_the_title_is_reported_as_a_title_finding()
    {
        var finding = Assert.Single(ImportSecretScanner.Scan(Record("k", "key " + AwsKey(), "clean body")));

        Assert.Equal(SecretField.Title, finding.Field);
    }

    [Fact]
    public void Clean_text_has_no_findings()
    {
        Assert.Empty(ImportSecretScanner.Scan(Record("k", "Meeting notes", "Ship the beta on Friday. See https://example.com/spec.")));
    }

    [Fact]
    public void A_finding_never_holds_the_secret()
    {
        var record = Record("k", "t", "x " + GithubToken());

        var json = JsonSerializer.Serialize(ImportSecretScanner.Scan(record));

        Assert.DoesNotContain(GithubToken(), json, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_removes_every_secret_and_keeps_the_rest()
    {
        var text = "before " + AwsKey() + " middle " + PostgresUrl() + " after";

        var redacted = ImportSecretScanner.Redact(text);

        Assert.DoesNotContain(AwsKey(), redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2pass", redacted, StringComparison.Ordinal);
        Assert.StartsWith("before [REDACTED:aws-access-key-id] middle [REDACTED:connection-string-url]", redacted, StringComparison.Ordinal);
        Assert.EndsWith(" after", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_key_block_is_removed_whole()
    {
        var redacted = ImportSecretScanner.Redact("keep\n" + PemBlock() + "\nalso keep");

        Assert.DoesNotContain("MIIEvQIBADANBgkq", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", redacted, StringComparison.Ordinal);
        Assert.Contains("keep", redacted, StringComparison.Ordinal);
        Assert.Contains("also keep", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_private_key_block_is_removed_to_the_end()
    {
        var redacted = ImportSecretScanner.Redact("head\n-----BEGIN " + "PRIVATE KEY-----\nMIIEvQIBADANBgkq\n");

        Assert.DoesNotContain("MIIEvQIBADANBgkq", redacted, StringComparison.Ordinal);
        Assert.StartsWith("head", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacting_clean_text_returns_it_unchanged()
    {
        Assert.Equal("nothing to hide", ImportSecretScanner.Redact("nothing to hide"));
    }

    [Fact]
    public void The_scan_cannot_be_turned_off_for_external_text()
    {
        Assert.Throws<ArgumentException>(() => SecretScanOptions.ForExternal(SecretScanPolicy.Off));
        Assert.Equal(SecretScanPolicy.Off, SecretScanOptions.ForTrusted(SecretScanPolicy.Off).Policy);
    }

    [Fact]
    public void Quarantine_holds_flagged_records_until_decided()
    {
        var clean = Record("clean", "A", "fine");
        var dirty = Record("dirty", "B", "token " + AwsKey());
        var preview = Preview(clean, dirty);

        var report = ImportSecretGate.Scan(preview, Quarantine);
        var resolved = ImportSecretGate.Resolve(preview, Quarantine, new Dictionary<string, QuarantineChoice>());

        Assert.True(report.HoldsRecords);
        Assert.Equal("dirty", Assert.Single(report.Flagged).Record.SourceKey);
        Assert.Equal(["clean"], resolved.New.Select(r => r.SourceKey).ToArray());
    }

    [Fact]
    public void Import_as_is_keeps_the_record_and_marks_it()
    {
        var dirty = Record("dirty", "B", "token " + AwsKey());

        var resolved = ImportSecretGate.Resolve(Preview(dirty), Quarantine,
            new Dictionary<string, QuarantineChoice> { ["dirty"] = QuarantineChoice.ImportAsIs });

        var kept = Assert.Single(resolved.New);
        Assert.Equal(dirty.Body, kept.Body);
        Assert.Equal("as_is", kept.Attributes["secret_scan"]);
        Assert.Equal("test", kept.Attributes["format"]);
    }

    [Fact]
    public void Redact_and_import_keeps_a_redacted_copy_with_the_same_key()
    {
        var dirty = Record("dirty", "Key " + AwsKey(), "token " + AwsKey());

        var resolved = ImportSecretGate.Resolve(Preview(dirty), Quarantine,
            new Dictionary<string, QuarantineChoice> { ["dirty"] = QuarantineChoice.RedactAndImport });

        var kept = Assert.Single(resolved.New);
        Assert.Equal("dirty", kept.SourceKey);
        Assert.DoesNotContain(AwsKey(), kept.Body + kept.Title, StringComparison.Ordinal);
        Assert.Equal("redacted", kept.Attributes["secret_scan"]);
    }

    [Fact]
    public void Skip_drops_the_record()
    {
        var resolved = ImportSecretGate.Resolve(Preview(Record("dirty", "B", AwsKey())), Quarantine,
            new Dictionary<string, QuarantineChoice> { ["dirty"] = QuarantineChoice.Skip });

        Assert.Empty(resolved.New);
    }

    [Fact]
    public void Warn_reports_but_holds_nothing()
    {
        var options = SecretScanOptions.ForExternal(SecretScanPolicy.Warn);
        var preview = Preview(Record("dirty", "B", AwsKey()));

        var report = ImportSecretGate.Scan(preview, options);
        var resolved = ImportSecretGate.Resolve(preview, options, new Dictionary<string, QuarantineChoice>());

        Assert.False(report.HoldsRecords);
        Assert.Single(report.Flagged);
        Assert.Same(preview, resolved);
    }

    [Fact]
    public void Off_scans_nothing()
    {
        var options = SecretScanOptions.ForTrusted(SecretScanPolicy.Off);

        var report = ImportSecretGate.Scan(Preview(Record("dirty", "B", AwsKey())), options);

        Assert.Empty(report.Flagged);
    }

    [Fact]
    public void Replace_choices_and_collisions_follow_only_the_records_that_remain()
    {
        var kept = Record("kept", "K", "fine");
        var held = Record("held", "H", AwsKey());
        var existing = new ExistingObject("note", "K", "obj-1", "v1");
        var preview = new ImportPreview([kept, held], [])
        {
            ReplaceSourceKeys = ["kept", "held"],
            ExistingCollisions = [new ExistingCollision(kept, existing), new ExistingCollision(held, existing)],
        };

        var resolved = ImportSecretGate.Resolve(preview, Quarantine, new Dictionary<string, QuarantineChoice>());

        Assert.Equal(["kept"], resolved.ReplaceSourceKeys);
        Assert.Equal("kept", Assert.Single(resolved.ExistingCollisions).Incoming.SourceKey);
    }

    [Fact]
    public void Resolve_is_deterministic()
    {
        var preview = Preview(Record("a", "A", AwsKey()), Record("b", "B", "fine"));
        var decisions = new Dictionary<string, QuarantineChoice> { ["a"] = QuarantineChoice.RedactAndImport };

        var first = ImportSecretGate.Resolve(preview, Quarantine, decisions);
        var second = ImportSecretGate.Resolve(preview, Quarantine, decisions);

        Assert.Equal(first.New.Select(r => (r.SourceKey, r.Body)), second.New.Select(r => (r.SourceKey, r.Body)));
    }

    [Fact]
    public async Task Each_decision_is_audited_with_the_choice_and_rules_but_never_the_secret()
    {
        var dirty = Record("dirty", "B", "token " + AwsKey());
        var preview = Preview(dirty);
        var report = ImportSecretGate.Scan(preview, Quarantine);
        var sink = new ListSink();

        await ImportSecretAudit.RecordDecisionsAsync(sink, "job-1", report,
            new Dictionary<string, QuarantineChoice> { ["dirty"] = QuarantineChoice.RedactAndImport }, _at, TestContext.Current.CancellationToken);

        var entry = Assert.Single(sink.Records);
        Assert.Equal(ImportAuditRecord.SecretDecisionKind, entry.Kind);
        Assert.Equal("dirty", entry.SourceKey);
        Assert.Equal("choice=redact_and_import;rules=aws-access-key-id", entry.Detail);
        Assert.DoesNotContain(AwsKey(), JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flagged_record_without_a_decision_writes_no_audit_entry()
    {
        var report = ImportSecretGate.Scan(Preview(Record("dirty", "B", AwsKey())), Quarantine);
        var sink = new ListSink();

        await ImportSecretAudit.RecordDecisionsAsync(sink, "job-1", report, new Dictionary<string, QuarantineChoice>(), _at, TestContext.Current.CancellationToken);

        Assert.Empty(sink.Records);
    }

    private sealed class ListSink : IAuditSink
    {
        public List<ImportAuditRecord> Records { get; } = [];

        public Task AppendAsync(ImportAuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
