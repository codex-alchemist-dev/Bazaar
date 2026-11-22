using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

public class PersonalFieldExporterTests
{
    private const string _secretValue = "alice@example.test";

    private sealed class FakeSource : IPersonalFieldSource
    {
        public List<PersonalSubjectState> Subjects { get; } =
        [
            new("alice", false),
            new("bob", true, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)),
        ];

        public List<string> EnvelopeReads { get; } = [];

        public Task<IReadOnlyList<PersonalSubjectState>> ListSubjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonalSubjectState>>(Subjects);

        public Task<IReadOnlyList<PersonalEnvelope>> ReadEnvelopesAsync(string subjectId, CancellationToken ct)
        {
            EnvelopeReads.Add(subjectId);
            IReadOnlyList<PersonalEnvelope> list =
            [
                new("email", "key-1", [1, 2, 3], [9, 9, 9]),
                new("name", "key-1", [4, 5, 6], [8, 8, 8]),
            ];
            return Task.FromResult(list);
        }
    }

    private sealed class FakeDecryptor : IPersonalFieldDecryptor
    {
        public List<string> Calls { get; } = [];
        public bool KeyGone { get; set; }

        public Task<string?> DecryptAsync(string subjectId, PersonalEnvelope envelope, CancellationToken ct)
        {
            Calls.Add(subjectId);
            return Task.FromResult<string?>(KeyGone ? null : _secretValue);
        }
    }

    private sealed class FakeSettings(bool allow) : IPersonalExportSettings
    {
        public Task<bool> AllowPlaintextPersonalAsync(CancellationToken ct) => Task.FromResult(allow);
    }

    private sealed class FakeAudit : IPersonalExportAuditSink
    {
        public List<PersonalExportAuditRecord> Records { get; } = [];
        public bool Fail { get; set; }

        public Task AppendAsync(PersonalExportAuditRecord record, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("audit down");
            }
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static (PersonalFieldExporter Exporter, FakeSource Source, FakeDecryptor Decryptor, FakeAudit Audit) Make(bool allowPlaintext = false)
    {
        var source = new FakeSource();
        var decryptor = new FakeDecryptor();
        var audit = new FakeAudit();
        return (new PersonalFieldExporter(source, decryptor, new FakeSettings(allowPlaintext), audit), source, decryptor, audit);
    }

    private static PersonalExportScope Owner(bool plaintext = false, bool session = true) =>
        new("owner", PersonalExportRole.Owner, IsOwnerSession: session, IncludePlaintext: plaintext);

    private static string Text(PersonalExportResult r, string path) =>
        Encoding.UTF8.GetString(r.Files.Single(f => f.Path == path).Content);

    private static JsonElement Index(PersonalExportResult r) => JsonElement.Parse(Text(r, PersonalFieldExporter.IndexPath));

    // Rule 1: ciphertext plus key id, never a DEK or plaintext.
    [Fact]
    public async Task Default_export_carries_ciphertext_and_key_id_only()
    {
        var (exporter, _, decryptor, _) = Make();
        var result = await exporter.ExportAsync(Owner(), CancellationToken.None);

        var lines = Text(result, "personal/alice.enc.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var first = JsonElement.Parse(lines[0]);
        Assert.Equal("email", first.GetProperty("field").GetString());
        Assert.Equal("key-1", first.GetProperty("keyId").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), first.GetProperty("nonce").GetString());
        Assert.DoesNotContain(result.Files, f => f.Path == "personal/alice.json");
        Assert.DoesNotContain(result.Files, f => Encoding.UTF8.GetString(f.Content).Contains(_secretValue, StringComparison.Ordinal));
        Assert.Empty(decryptor.Calls);
        Assert.Null(result.PlaintextNotice);
        Assert.False(Index(result).GetProperty("plaintextPersonal").GetBoolean());
    }

    [Fact]
    public async Task Envelope_without_key_id_is_refused()
    {
        var (_, _, decryptor, audit) = Make();
        var bad = new PersonalFieldExporter(new BadEnvelopeSource(), decryptor, new FakeSettings(false), audit);
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(() => bad.ExportAsync(Owner(), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.InvalidEnvelope, ex.Reason);
    }

    private sealed class BadEnvelopeSource : IPersonalFieldSource
    {
        public Task<IReadOnlyList<PersonalSubjectState>> ListSubjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonalSubjectState>>([new("alice", false)]);

        public Task<IReadOnlyList<PersonalEnvelope>> ReadEnvelopesAsync(string subjectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonalEnvelope>>([new("email", " ", [1], [2])]);
    }

    // Rule 2: plaintext needs the owner-session flag and the setting, is flagged, and is audited.
    [Fact]
    public async Task Plaintext_is_produced_flagged_and_audited_when_every_gate_passes()
    {
        var (exporter, _, _, audit) = Make(allowPlaintext: true);
        var result = await exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None);

        var json = JsonElement.Parse(Text(result, "personal/alice.json"));
        Assert.Equal(_secretValue, json.GetProperty("fields").GetProperty("email").GetString());
        Assert.True(Index(result).GetProperty("plaintextPersonal").GetBoolean());
        Assert.True(Index(result).GetProperty("subjects")[0].GetProperty("plaintextPersonal").GetBoolean());
        Assert.NotNull(result.PlaintextNotice);
        Assert.Contains(audit.Records, r => r.Kind == PersonalExportAuditRecord.PlaintextKind && r.Plaintext && r.Actor == "owner");
        Assert.DoesNotContain(audit.Records, r => string.Join(',', r.Subjects).Contains(_secretValue, StringComparison.Ordinal));
        // The ciphertext file is still there.
        Assert.Contains(result.Files, f => f.Path == "personal/alice.enc.jsonl");
    }

    [Fact]
    public async Task Plaintext_is_refused_without_the_owner_session_flag()
    {
        var (exporter, _, decryptor, audit) = Make(allowPlaintext: true);
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(
            () => exporter.ExportAsync(Owner(plaintext: true, session: false), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.PlaintextNeedsOwnerSession, ex.Reason);
        Assert.Empty(decryptor.Calls);
        Assert.Contains(audit.Records, r => r.Kind == PersonalExportAuditRecord.RefusedKind);
    }

    [Fact]
    public async Task Plaintext_is_refused_for_a_member_even_with_the_session_flag()
    {
        var (exporter, _, decryptor, _) = Make(allowPlaintext: true);
        var scope = new PersonalExportScope("alice", PersonalExportRole.Member, IsOwnerSession: true, IncludePlaintext: true, RequesterSubjectId: "alice");
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(() => exporter.ExportAsync(scope, CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.PlaintextNeedsOwnerSession, ex.Reason);
        Assert.Empty(decryptor.Calls);
    }

    [Fact]
    public async Task Plaintext_is_refused_when_the_setting_is_false()
    {
        var (exporter, _, decryptor, _) = Make(allowPlaintext: false);
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(
            () => exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.PlaintextSettingOff, ex.Reason);
        Assert.Empty(decryptor.Calls);
    }

    [Fact]
    public async Task Setting_true_alone_does_not_produce_plaintext()
    {
        var (exporter, _, decryptor, _) = Make(allowPlaintext: true);
        var result = await exporter.ExportAsync(Owner(plaintext: false), CancellationToken.None);
        Assert.DoesNotContain(result.Files, f => f.Path.EndsWith(".json", StringComparison.Ordinal) && f.Path.StartsWith("personal/alice", StringComparison.Ordinal) && !f.Path.EndsWith(".tombstone.json", StringComparison.Ordinal));
        Assert.Empty(decryptor.Calls);
    }

    [Fact]
    public async Task Plaintext_with_a_failing_audit_decrypts_nothing()
    {
        var (exporter, _, decryptor, audit) = Make(allowPlaintext: true);
        audit.Fail = true;
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(
            () => exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.AuditUnavailable, ex.Reason);
        Assert.Empty(decryptor.Calls);
    }

    [Fact]
    public async Task Plaintext_with_a_destroyed_key_fails_closed()
    {
        var (exporter, _, decryptor, _) = Make(allowPlaintext: true);
        decryptor.KeyGone = true;
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(
            () => exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.PlaintextUnavailable, ex.Reason);
        Assert.DoesNotContain(_secretValue, ex.Message, StringComparison.Ordinal);
    }

    // Rule 3: the manifest side lists the subjects whose ciphertext the export holds.
    [Fact]
    public async Task Index_lists_subjects_holding_ciphertext_with_key_ids()
    {
        var (exporter, _, _, audit) = Make();
        var result = await exporter.ExportAsync(Owner(), CancellationToken.None);
        var subjects = Index(result).GetProperty("subjects").EnumerateArray().ToList();

        var alice = subjects.Single(s => s.GetProperty("subject").GetString() == "alice");
        Assert.True(alice.GetProperty("holdsCiphertext").GetBoolean());
        Assert.Equal("key-1", alice.GetProperty("keyIds")[0].GetString());
        var bob = subjects.Single(s => s.GetProperty("subject").GetString() == "bob");
        Assert.False(bob.GetProperty("holdsCiphertext").GetBoolean());
        Assert.Contains(audit.Records, r => r.Kind == PersonalExportAuditRecord.ExportKind && r.Subjects.Contains("alice"));
    }

    [Fact]
    public async Task Subject_filter_limits_the_index_and_an_unknown_subject_is_refused()
    {
        var (exporter, _, _, _) = Make();
        var scope = Owner() with { SubjectIds = new HashSet<string> { "alice" } };
        var result = await exporter.ExportAsync(scope, CancellationToken.None);
        Assert.Single(Index(result).GetProperty("subjects").EnumerateArray());

        var unknown = Owner() with { SubjectIds = new HashSet<string> { "nobody" } };
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(() => exporter.ExportAsync(unknown, CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.SubjectOutOfScope, ex.Reason);
    }

    [Fact]
    public async Task Member_exports_only_their_own_subject()
    {
        var (exporter, _, _, _) = Make();
        var own = new PersonalExportScope("alice", PersonalExportRole.Member, RequesterSubjectId: "alice");
        var result = await exporter.ExportAsync(own, CancellationToken.None);
        Assert.Contains(result.Files, f => f.Path == "personal/alice.enc.jsonl");
        Assert.DoesNotContain(result.Files, f => f.Path.StartsWith("personal/bob", StringComparison.Ordinal));

        var other = own with { SubjectIds = new HashSet<string> { "bob" } };
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(() => exporter.ExportAsync(other, CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.SubjectOutOfScope, ex.Reason);
    }

    [Fact]
    public async Task Agents_export_nothing()
    {
        var (exporter, source, _, _) = Make();
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(
            () => exporter.ExportAsync(new PersonalExportScope("agent-1", PersonalExportRole.Agent), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.AgentNotAllowed, ex.Reason);
        Assert.Empty(source.EnvelopeReads);
    }

    // Rule 4: tombstones are exported as tombstones, never as content.
    [Fact]
    public async Task Erased_subject_is_exported_as_a_tombstone_only()
    {
        var (exporter, source, decryptor, _) = Make(allowPlaintext: true);
        var result = await exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None);

        var tomb = JsonElement.Parse(Text(result, "personal/bob.tombstone.json"));
        Assert.True(tomb.GetProperty("erased").GetBoolean());
        Assert.Equal("2026-11-01T00:00:00Z", tomb.GetProperty("erasedAt").GetString());
        Assert.DoesNotContain(result.Files, f => f.Path is "personal/bob.enc.jsonl" or "personal/bob.json");
        Assert.DoesNotContain("bob", source.EnvelopeReads);
        Assert.DoesNotContain("bob", decryptor.Calls);
    }

    [Fact]
    public async Task Erased_subject_never_gets_content_even_if_the_store_still_holds_it()
    {
        // The source would happily return envelopes for bob; the exporter must not ask.
        var (exporter, source, _, _) = Make();
        var result = await exporter.ExportAsync(Owner(), CancellationToken.None);
        Assert.DoesNotContain("bob", source.EnvelopeReads);
        Assert.DoesNotContain(result.Files, f => f.Path.Contains("bob", StringComparison.Ordinal) && !f.Path.EndsWith(".tombstone.json", StringComparison.Ordinal));
    }

    // Rule 5: an erased subject's key is never requested (the decryptor is the only key path and is never called for it).
    [Fact]
    public async Task Erased_subject_key_is_never_requested_on_any_path()
    {
        var (exporter, _, decryptor, _) = Make(allowPlaintext: true);
        await exporter.ExportAsync(Owner(plaintext: true), CancellationToken.None);
        Assert.All(decryptor.Calls, id => Assert.Equal("alice", id));
        Assert.NotEmpty(decryptor.Calls);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Unsafe_subject_ids_are_refused(string id)
    {
        var source = new FakeSource();
        source.Subjects.Add(new PersonalSubjectState(id, false));
        var exporter = new PersonalFieldExporter(source, new FakeDecryptor(), new FakeSettings(false), new FakeAudit());
        var ex = await Assert.ThrowsAsync<PersonalExportRefusedException>(() => exporter.ExportAsync(Owner(), CancellationToken.None));
        Assert.Equal(PersonalExportRefusal.InvalidSubjectId, ex.Reason);
    }

    [Fact]
    public async Task Output_builds_into_a_verified_bundle()
    {
        var (exporter, _, _, _) = Make();
        var result = await exporter.ExportAsync(Owner(), CancellationToken.None);
        var bundle = ExportBundleWriter.Build("inst-1", new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), [.. result.Files]);
        Assert.Empty(ExportBundleWriter.Verify(bundle));
    }
}
