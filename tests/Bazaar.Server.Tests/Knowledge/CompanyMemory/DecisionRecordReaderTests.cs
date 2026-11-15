using Bazaar.Server.Knowledge.CompanyMemory;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Knowledge.CompanyMemory;

public sealed class DecisionRecordReaderTests
{
    private const string _path = "decisions/0002-use-notify.md";

    [Fact]
    public void Reads_status_supersedes_superseded_by_and_decided_in()
    {
        const string source = """
            ---
            id: 01J5Z3K8M9N0P1Q2R3S4T5V6W7
            type: decision
            title: Use NOTIFY for GC fan-out
            status: accepted
            supersedes:
              - 0001-use-polling
            superseded_by: 0004-use-valkey
            decidedIn: gc-message:01J5Z3K8M9N0P1Q2R3S4T5V6W8
            ---
            Body text.
            """;

        var record = DecisionRecordReader.TryRead(_path, source);

        Assert.NotNull(record);
        Assert.Equal(_path, record.Path);
        Assert.Equal("accepted", record.Status);
        Assert.Equal(["0001-use-polling"], record.Supersedes);
        Assert.Equal("0004-use-valkey", record.SupersededBy);
        Assert.Equal("gc-message:01J5Z3K8M9N0P1Q2R3S4T5V6W8", record.DecidedIn);
    }

    [Fact]
    public void Missing_chain_keys_read_as_empty_and_null()
    {
        const string source = """
            ---
            id: 01J5Z3K8M9N0P1Q2R3S4T5V6W7
            type: decision
            title: Plain decision
            status: proposed
            ---
            """;

        var record = DecisionRecordReader.TryRead(_path, source);

        Assert.NotNull(record);
        Assert.Empty(record.Supersedes);
        Assert.Null(record.SupersededBy);
        Assert.Null(record.DecidedIn);
    }

    [Fact]
    public void Non_decision_document_reads_as_null()
    {
        const string source = """
            ---
            id: 01J5Z3K8M9N0P1Q2R3S4T5V6W7
            type: research
            title: Not a decision
            ---
            """;

        Assert.Null(DecisionRecordReader.TryRead("research/notes.md", source));
    }

    [Fact]
    public void Document_without_front_matter_reads_as_null()
    {
        Assert.Null(DecisionRecordReader.TryRead(_path, "# Just text\n"));
    }

    [Fact]
    public void Supersedes_that_is_not_a_list_is_refused()
    {
        const string source = """
            ---
            id: 01J5Z3K8M9N0P1Q2R3S4T5V6W7
            type: decision
            title: Bad chain
            status: accepted
            supersedes: 0001-use-polling
            ---
            """;

        Assert.Throws<VaultFormatException>(() => DecisionRecordReader.TryRead(_path, source));
    }

    [Fact]
    public void Decision_without_status_is_refused_by_the_vault_reader()
    {
        const string source = """
            ---
            id: 01J5Z3K8M9N0P1Q2R3S4T5V6W7
            type: decision
            title: No status
            ---
            """;

        Assert.Throws<VaultFormatException>(() => DecisionRecordReader.TryRead(_path, source));
    }
}
