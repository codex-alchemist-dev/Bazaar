using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class ChatLogImporterTests
{
    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static readonly IReadOnlySet<string> _noKeys = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void Bracketed_shape_parses_author_timestamp_and_text()
    {
        var importer = new ChatLogImporter();
        var source = Src("dump.txt", "[2026-11-19 10:31:05] Alice: hello there\n[2026-11-19 10:32:00] Bob: hi\n");

        var records = importer.Parse(source);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(ImportCommitter.GcEntryKind, r.Kind));
        var first = records[0];
        Assert.Equal("Alice", first.Attributes["author"]);
        Assert.Equal("2026-11-19T10:31:05Z", first.Attributes["timestamp"]);
        Assert.Equal("chat-log", first.Attributes["format"]);
        Assert.Equal("dump.txt", first.Attributes["source"]);
        Assert.Equal("1", first.Attributes["line"]);
        Assert.Equal("hello there", first.Body);
        Assert.Equal("2", records[1].Attributes["line"]);
    }

    [Fact]
    public void Dash_shape_parses_author_and_timestamp()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "2026-11-19 10:31 - Alice: first\n2026-11-19T10:32:00Z - Bob: second\n"));

        Assert.Equal(2, records.Count);
        Assert.Equal("Alice", records[0].Attributes["author"]);
        Assert.Equal("2026-11-19T10:31:00Z", records[0].Attributes["timestamp"]);
        Assert.Equal("Bob", records[1].Attributes["author"]);
        Assert.Equal("2026-11-19T10:32:00Z", records[1].Attributes["timestamp"]);
        Assert.Equal("second", records[1].Body);
    }

    [Fact]
    public void Bare_shape_parses_timestamp_without_brackets_or_dash()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "2026-11-19 10:31:05 Alice: plain\n2026-11-19 10:32:05 Bob: other\n"));

        Assert.Equal(2, records.Count);
        Assert.Equal("Alice", records[0].Attributes["author"]);
        Assert.Equal("2026-11-19T10:31:05Z", records[0].Attributes["timestamp"]);
        Assert.Equal("plain", records[0].Body);
    }

    [Fact]
    public void Offset_is_converted_to_utc()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "[2026-11-19T12:00:00+02:00] Alice: offset\n[2026-11-19T12:00:00Z] Bob: zulu\n"));

        Assert.Equal("2026-11-19T10:00:00Z", records[0].Attributes["timestamp"]);
        Assert.Equal("2026-11-19T12:00:00Z", records[1].Attributes["timestamp"]);
    }

    [Fact]
    public void Continuation_lines_append_to_the_previous_message()
    {
        var importer = new ChatLogImporter();
        var source = Src(
            "dump.txt",
            "[2026-11-19 10:31:05] Alice: first line\nsecond line of Alice\n\nthird after blank\n" +
            "[2026-11-19 10:32:00] Bob: short\n");

        var records = importer.Parse(source);

        Assert.Equal(2, records.Count);
        Assert.Equal("first line\nsecond line of Alice\n\nthird after blank", records[0].Body);
        Assert.Equal("short", records[1].Body);
        Assert.Equal("1", records[0].Attributes["line"]);
    }

    [Fact]
    public void Crlf_line_endings_parse_like_lf()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "[2026-11-19 10:31:05] Alice: one\r\ncontinued\r\n[2026-11-19 10:32:00] Bob: two\r\n"));

        Assert.Equal(2, records.Count);
        Assert.Equal("one\ncontinued", records[0].Body);
    }

    [Fact]
    public void Unparseable_timestamps_are_kept_raw()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "[later today] Alice: a\n[10:31] Bob: b\n[2026-11-19] Carol: c\n"));

        Assert.Equal(3, records.Count);
        Assert.Equal("later today", records[0].Attributes["timestamp"]);
        // A bare time would take today's date, so it is kept raw to keep re-imports deterministic.
        Assert.Equal("10:31", records[1].Attributes["timestamp"]);
        // A date with no time is not an instant, so it is kept raw too.
        Assert.Equal("2026-11-19", records[2].Attributes["timestamp"]);
        Assert.Equal("Alice", records[0].Attributes["author"]);
    }

    [Fact]
    public void Malformed_lines_never_throw_and_become_continuation_text()
    {
        var importer = new ChatLogImporter();
        var source = Src(
            "dump.txt",
            "[2026-11-19 10:31:05] Alice: ok\n[unclosed bracket Bob: x\n\u0001\u0002 junk ::: \n[] : empty name\n");

        var records = importer.Parse(source);

        var record = Assert.Single(records);
        Assert.Contains("[unclosed bracket Bob: x", record.Body);
        Assert.Contains("junk :::", record.Body);
    }

    [Fact]
    public void Leading_lines_without_a_message_are_kept_as_unattributed_record()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "preamble text\n[2026-11-19 10:31:05] Alice: hi\n"));

        Assert.Equal(2, records.Count);
        Assert.Equal("preamble text", records[0].Body);
        Assert.Equal("", records[0].Attributes["author"]);
        Assert.Equal("", records[0].Attributes["timestamp"]);
        Assert.Equal("Alice", records[1].Attributes["author"]);
    }

    [Fact]
    public void Detection_accepts_chat_structure_with_high_confidence()
    {
        var importer = new ChatLogImporter();
        var result = importer.Detect(Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\n"));

        Assert.True(result.Confidence > 0.3, $"expected above plain text, got {result.Confidence}");
        Assert.Equal(0.9, result.Confidence);
    }

    [Fact]
    public void Detection_rejects_prose()
    {
        var importer = new ChatLogImporter();
        const string prose = "The quarterly review went well. We shipped three features and fixed twelve bugs.\n" +
                             "Next quarter we will focus on import tooling, and on the vault layout.\n";

        Assert.Equal(0, importer.Detect(Src("notes.txt", prose)).Confidence);
    }

    [Fact]
    public void Detection_rejects_prose_with_colons_and_no_timestamps()
    {
        var importer = new ChatLogImporter();
        const string prose = "Note: this is a plain sentence.\nWarning: nothing here is a chat line.\n";

        Assert.Equal(0, importer.Detect(Src("notes.txt", prose)).Confidence);
    }

    [Fact]
    public void Detection_needs_at_least_two_matching_lines()
    {
        var importer = new ChatLogImporter();

        Assert.Equal(0, importer.Detect(Src("one.txt", "[2026-11-19 10:31:05] Alice: only one\n")).Confidence);
    }

    [Fact]
    public void Confidence_stays_below_plain_text_when_fewer_than_60_percent_match()
    {
        var importer = new ChatLogImporter();
        // Two of five non-empty lines match: 40%.
        const string mixed = "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\nprose one\nprose two\nprose three\n";

        var result = importer.Detect(Src("mixed.txt", mixed));

        Assert.True(result.Confidence > 0, "should still be a candidate");
        Assert.True(result.Confidence < 0.3, $"expected below plain text, got {result.Confidence}");
    }

    [Fact]
    public void Confidence_rises_with_the_fraction_of_matching_lines()
    {
        var importer = new ChatLogImporter();
        var mostly = importer.Detect(Src("a.txt", "[2026-11-19 10:31:05] A: 1\n[2026-11-19 10:32:00] B: 2\n[2026-11-19 10:33:00] C: 3\nextra\n"));
        var fewer = importer.Detect(Src("b.txt", "[2026-11-19 10:31:05] A: 1\n[2026-11-19 10:32:00] B: 2\nextra\nextra\nextra\n"));

        Assert.True(mostly.Confidence > fewer.Confidence);
    }

    [Fact]
    public void Detection_rejects_empty_and_binary_content()
    {
        var importer = new ChatLogImporter();

        Assert.Equal(0, importer.Detect(new ImportSource("empty.txt", [])).Confidence);
        Assert.Equal(0, importer.Detect(new ImportSource("blob.bin", [0x50, 0x4B, 0x00, 0x03])).Confidence);
    }

    [Fact]
    public void Parsing_is_deterministic()
    {
        var importer = new ChatLogImporter();
        var source = Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\ncont\n2026-11-19 10:32 - Bob: b\n[x] Carol: c\n");

        var first = importer.Parse(source);
        var second = importer.Parse(source);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].SourceKey, second[i].SourceKey);
            Assert.Equal(first[i].Title, second[i].Title);
            Assert.Equal(first[i].Body, second[i].Body);
            Assert.Equal(first[i].Attributes, second[i].Attributes);
        }
    }

    [Fact]
    public void Source_keys_are_stable_across_reimports()
    {
        var importer = new ChatLogImporter();
        var source = Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\n");

        var first = importer.Parse(source);
        var again = importer.Parse(Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\n"));

        Assert.Equal(first.Select(r => r.SourceKey), again.Select(r => r.SourceKey));
        var planned = ImportPlanner.Plan(first, first.Select(r => r.SourceKey).ToHashSet(StringComparer.Ordinal));
        Assert.Empty(planned.New);
        Assert.Equal(2, planned.Duplicates.Count);
    }

    [Fact]
    public void Editing_one_message_changes_only_its_key()
    {
        var importer = new ChatLogImporter();
        var before = importer.Parse(Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\n"));
        var after = importer.Parse(Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b edited\n"));

        Assert.Equal(before[0].SourceKey, after[0].SourceKey);
        Assert.NotEqual(before[1].SourceKey, after[1].SourceKey);
    }

    [Fact]
    public void Identical_messages_at_different_positions_get_different_keys()
    {
        var importer = new ChatLogImporter();
        var records = importer.Parse(Src("dump.txt", "[2026-11-19 10:31:05] Alice: ok\n[2026-11-19 10:31:05] Alice: ok\n"));

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].SourceKey, records[1].SourceKey);
    }

    [Fact]
    public void Source_name_is_part_of_the_key()
    {
        var importer = new ChatLogImporter();
        var a = importer.Parse(Src("a.txt", "[2026-11-19 10:31:05] Alice: ok\n"));
        var b = importer.Parse(Src("b.txt", "[2026-11-19 10:31:05] Alice: ok\n"));

        Assert.NotEqual(a[0].SourceKey, b[0].SourceKey);
    }

    [Fact]
    public void Pipeline_prefers_chat_log_over_plain_text_for_a_chat_dump()
    {
        var pipeline = new ImportPipeline([new PlainTextImporter(), new ChatLogImporter()]);
        var source = Src("dump.txt", "[2026-11-19 10:31:05] Alice: a\n[2026-11-19 10:32:00] Bob: b\n");

        var result = pipeline.Run(source, _noKeys);

        Assert.Equal(ImportStatus.Ready, result.Status);
        Assert.Equal("bazaar.import.chatlog", result.ImporterId);
    }
}
