using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Memory.Notes;

public class NoteDocumentTests
{
    private const string Lf = """
        ---
        id: 01J9ZABCDEFGHJKMNPQRSTVWXY
        type: memory-procedure
        title: "Retry: backoff rules"
        project: example
        memory: {kind: procedural, status: active, version: 3, trust: T2, source_trust: agent, tainted: false, origin: {gc: gc-1, messages: [msg-1, msg-2]}, derived_from: [01J9ZABCDEFGHJKMNPQRSTVWXZ]}
        ---
        # Retry backoff

        Back off exponentially.  Keep   spacing and trailing whitespace.
        """;

    public static TheoryData<string> RoundTripSamples => new()
    {
        Lf,
        Lf.Replace("\n", "\r\n", StringComparison.Ordinal),
        "---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, origin: {gc: g}}\n---",
        "---\n# a comment line\n\nid: a1\ntype: memory-working\ntitle: plain title with: colons\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, origin: {gc: g}}\nx-custom: [one, two]\n---\n\nBody with unicode: caf\u00e9 and no final newline",
    };

    [Theory]
    [MemberData(nameof(RoundTripSamples))]
    public void Unmodified_note_serializes_to_identical_bytes(string text)
    {
        var parsed = NoteDocument.Parse(text);

        Assert.True(parsed.IsSuccess, parsed.Detail);
        Assert.Equal(text, parsed.Document!.Serialize());
    }

    [Fact]
    public void Rewrapping_with_equal_values_keeps_the_original_bytes()
    {
        var doc = NoteDocument.Parse(Lf).Document!;

        var same = doc.WithNote(doc.Note with { }).WithBody(doc.Body);

        Assert.Equal(Lf, same.Serialize());
    }

    [Fact]
    public void Changed_front_matter_is_written_canonically_and_the_body_bytes_survive()
    {
        var doc = NoteDocument.Parse(Lf).Document!;
        var changed = doc.WithNote(doc.Note with { Title = "Retry: \"quoted\" title", Version = 4 });

        var text = changed.Serialize();
        var reparsed = NoteDocument.Parse(text);

        Assert.True(reparsed.IsSuccess, reparsed.Detail);
        Assert.Equal("Retry: \"quoted\" title", reparsed.Document!.Note.Title);
        Assert.Equal(4, reparsed.Document.Note.Version);
        Assert.Equal(doc.Body, reparsed.Document.Body);
        Assert.EndsWith(doc.Body, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_top_level_fields_are_kept_through_a_modified_write()
    {
        var text = "---\nid: a1\ntype: memory-working\ntitle: t\nxtra: [one, two]\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, origin: {gc: g}}\n---\nbody";
        var doc = NoteDocument.Parse(text).Document!;

        var text2 = doc.WithNote(doc.Note with { Version = 2 }).Serialize();

        Assert.Contains("xtra: [one, two]", text2, StringComparison.Ordinal);
        Assert.Equal("body", NoteDocument.Parse(text2).Document!.Body);
    }

    [Fact]
    public void Parsed_note_has_typed_fields()
    {
        var note = NoteDocument.Parse(Lf).Document!.Note;

        Assert.Equal(MemoryKind.Procedural, note.Kind);
        Assert.Equal(MemoryNoteStatus.Active, note.Status);
        Assert.Equal(3, note.Version);
        Assert.Equal(MemoryTier.AgentVerified, note.Trust);
        Assert.Equal(ContentTrust.Agent, note.SourceTrust);
        Assert.False(note.Tainted);
        Assert.Equal("gc-1", note.Origin!.Gc);
        Assert.Equal("msg-1,msg-2", string.Join(',', note.Origin.Messages));
        Assert.Equal("01J9ZABCDEFGHJKMNPQRSTVWXZ", string.Join(',', note.DerivedFrom));
    }

    [Theory]
    [InlineData("", NoteParseError.MissingOpeningDelimiter)]
    [InlineData("id: x\n---\n", NoteParseError.MissingOpeningDelimiter)]
    [InlineData("---\nid: a1\ntype: memory-working\n", NoteParseError.MissingClosingDelimiter)]
    [InlineData("---\n  id: a1\n---\n", NoteParseError.UnsupportedSyntax)]
    [InlineData("---\njust a line\n---\n", NoteParseError.MalformedLine)]
    [InlineData("---\nid:\n---\n", NoteParseError.MalformedValue)]
    [InlineData("---\nid: [unclosed\n---\n", NoteParseError.MalformedValue)]
    [InlineData("---\nid: a1\nid: a2\n---\n", NoteParseError.DuplicateKey)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: \"unterminated\n---\n", NoteParseError.MalformedValue)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, kind: working}\n---\n", NoteParseError.MalformedValue)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\n---\n", NoteParseError.MissingField)]
    [InlineData("---\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false}\n---\n", NoteParseError.MissingField)]
    [InlineData("---\nid: a1\ntype: memory-banana\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false}\n---\n", NoteParseError.InvalidField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: gossip, status: active, version: 1, trust: T2, source_trust: agent, tainted: false}\n---\n", NoteParseError.InvalidField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 0, trust: T2, source_trust: agent, tainted: false}\n---\n", NoteParseError.InvalidField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T9, source_trust: agent, tainted: false}\n---\n", NoteParseError.InvalidField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: maybe}\n---\n", NoteParseError.InvalidField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, surprise: 1}\n---\n", NoteParseError.UnknownMemoryField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, origin: {gc: g, extra: x}}\n---\n", NoteParseError.UnknownMemoryField)]
    [InlineData("---\nid: a1\ntype: memory-working\ntitle: t\nmemory: {kind: working, status: active, version: 1, trust: T2, source_trust: agent, tainted: false, derived_from: [a, [b]]}\n---\n", NoteParseError.InvalidField)]
    public void Malformed_front_matter_is_rejected_with_a_typed_error(string text, NoteParseError expected)
    {
        var result = NoteDocument.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Document);
        Assert.Equal(expected, result.Error);
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    [Fact]
    public void Bad_input_never_throws_even_when_it_is_nonsense()
    {
        var samples = new[] { "---\n[[[[[[[[[[[[[[[[[[[[\n---", "---\nx: {{{{{{{{{{{{{{{{{{{{{{{{{{{{{{{{\n---\n", "---\nx: \"\\q\"\n---\n", "---\n:\n---\n" };

        foreach (var sample in samples)
        {
            var result = NoteDocument.Parse(sample);
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public void Create_serializes_in_canonical_form_that_parses_back()
    {
        var note = NoteFixtures.Note(MemoryKind.Failure) with { Title = "Timeouts: a failure" };
        var text = NoteDocument.Create(note, "Body.").Serialize();

        var parsed = NoteDocument.Parse(text);

        Assert.True(parsed.IsSuccess, parsed.Detail);
        var read = parsed.Document!.Note;
        Assert.Equal("Timeouts: a failure", read.Title);
        Assert.Equal(MemoryKind.Failure, read.Kind);
        Assert.Equal(note.Origin!.Messages, read.Origin!.Messages);
        Assert.Equal("Body.", parsed.Document.Body);
    }

    [Fact]
    public void Deprecated_block_round_trips()
    {
        var note = NoteFixtures.Note() with
        {
            Status = MemoryNoteStatus.Deprecated,
            Deprecated = new MemoryDeprecation("superseded", "owner", "2026-10-30T10:00:00Z"),
            Supersedes = ["01J9ZABCDEFGHJKMNPQRSTVWXZ"],
        };

        var parsed = NoteDocument.Parse(NoteFixtures.Text(note));

        Assert.True(parsed.IsSuccess, parsed.Detail);
        Assert.Equal(MemoryNoteStatus.Deprecated, parsed.Document!.Note.Status);
        Assert.Equal(new MemoryDeprecation("superseded", "owner", "2026-10-30T10:00:00Z"), parsed.Document.Note.Deprecated);
        Assert.Equal("01J9ZABCDEFGHJKMNPQRSTVWXZ", string.Join(',', parsed.Document.Note.Supersedes));
    }

    [Fact]
    public void Trust_tiers_parse_from_their_wire_names()
    {
        var parsed = NoteDocument.Parse("---\nid: a1\ntype: memory-hive\ntitle: t\nmemory: {kind: hive, status: active, version: 1, trust: T3, source_trust: owner, tainted: true, origin: {gc: g}}\n---\n");

        Assert.True(parsed.IsSuccess, parsed.Detail);
        Assert.Equal(MemoryTier.OwnerPromoted, parsed.Document!.Note.Trust);
        Assert.Equal(ContentTrust.Owner, parsed.Document.Note.SourceTrust);
        Assert.True(parsed.Document.Note.Tainted);
    }
}
