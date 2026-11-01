using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Markdown;

/// <summary>VLT1: front matter splitting (offsets, BOM, line endings) and typed validation of the YAML (doc 18).</summary>
public sealed class FrontMatterTests
{
    private const string _validId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private const string _validYaml = "id: 01ARZ3NDEKTSV4RRFFQ69G5FAV\ntype: plan\ntitle: Vault format\n";

    [Fact]
    public void Split_NoOpeningFence_ReturnsNoFrontMatter()
    {
        var split = FrontMatterSplitter.Split("# Title\n---\nid: x\n---\n");

        Assert.Null(split.Yaml);
        Assert.Equal(0, split.YamlStart);
        Assert.Equal(0, split.BodyStart);
    }

    [Fact]
    public void Split_OpeningFenceWithoutNewline_ReturnsNoFrontMatter()
    {
        var split = FrontMatterSplitter.Split("---");

        Assert.Null(split.Yaml);
        Assert.Equal(0, split.BodyStart);
    }

    [Fact]
    public void Split_LeadingBlankLine_ReturnsNoFrontMatter()
    {
        var split = FrontMatterSplitter.Split("\n---\nid: x\n---\nbody");

        Assert.Null(split.Yaml);
    }

    [Fact]
    public void Split_UnterminatedBlock_IsNotAnError()
    {
        var split = FrontMatterSplitter.Split("---\nid: x\ntitle: y\n");

        Assert.Null(split.Yaml);
        Assert.Equal(0, split.YamlStart);
        Assert.Equal(0, split.BodyStart);
    }

    [Fact]
    public void Split_FenceWithOtherTextIsNotAClosingFence()
    {
        const string source = "---\na: 1\n----\nstill yaml\n---\nbody";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal("a: 1\n----\nstill yaml\n", split.Yaml);
        Assert.Equal(source.IndexOf("body", StringComparison.Ordinal), split.BodyStart);
    }

    [Fact]
    public void Split_ByteOrderMark_KeepsOffsetsAbsolute()
    {
        const string source = "﻿---\nid: a\n---\nbody";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal("id: a\n", split.Yaml);
        Assert.Equal(5, split.YamlStart);
        Assert.Equal(source.IndexOf("body", StringComparison.Ordinal), split.BodyStart);
        Assert.Equal("id: a\n---\n", source[split.YamlStart..split.BodyStart]);
    }

    [Fact]
    public void Split_CrLf_ReturnsYamlWithCrLfAndBodyOffset()
    {
        const string source = "---\r\nid: a\r\ntitle: b\r\n---\r\nbody";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal("id: a\r\ntitle: b\r\n", split.Yaml);
        Assert.Equal(5, split.YamlStart);
        Assert.Equal(source.IndexOf("body", StringComparison.Ordinal), split.BodyStart);
    }

    [Fact]
    public void Split_CrLfOpeningAndClosingFenceWithTrailingSpaces_IsRecognised()
    {
        const string source = "---  \r\na: 1\r\n---\t \r\nbody";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal("a: 1\r\n", split.Yaml);
        Assert.Equal(source.IndexOf("body", StringComparison.Ordinal), split.BodyStart);
    }

    [Fact]
    public void Split_EmptyBlock_GivesEmptyYamlNotNull()
    {
        const string source = "---\n---\nbody";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal(string.Empty, split.Yaml);
        Assert.Equal(4, split.YamlStart);
        Assert.Equal(8, split.BodyStart);
    }

    [Fact]
    public void Split_ClosingFenceAsLastLineWithoutNewline_BodyStartIsLength()
    {
        const string source = "---\na: 1\n---";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal("a: 1\n", split.Yaml);
        Assert.Equal(source.Length, split.BodyStart);
    }

    [Fact]
    public void Split_ValidFullDocument_SplitsFrontMatterFromBody()
    {
        const string source = "---\n" + _validYaml + "owner: alice\n---\n# Heading\n\nBody text.\n";

        var split = FrontMatterSplitter.Split(source);

        Assert.Equal(_validYaml + "owner: alice\n", split.Yaml);
        Assert.StartsWith("# Heading", source[split.BodyStart..], StringComparison.Ordinal);
    }

    [Fact]
    public void Read_FullDocument_ReturnsAllFields()
    {
        const string yaml = """
            id: 01ARZ3NDEKTSV4RRFFQ69G5FAV
            type: decision
            title: Use Npgsql
            project: bazaar
            status: accepted
            aliases:
              - npgsql-choice
              - data layer
            owner: alice
            source_trust: owned
            tier: T2
            unknown_key: ignored
            """;

        var frontMatter = VaultFrontMatterReader.Read(yaml + "\n");

        Assert.Equal(_validId, frontMatter.Id);
        Assert.Equal("decision", frontMatter.Type);
        Assert.Equal("Use Npgsql", frontMatter.Title);
        Assert.Equal("bazaar", frontMatter.Project);
        Assert.Equal("accepted", frontMatter.Status);
        Assert.Equal<string>(["npgsql-choice", "data layer"], frontMatter.Aliases);
        Assert.Equal("alice", frontMatter.Owner);
        Assert.Equal("owned", frontMatter.SourceTrust);
        Assert.Equal("T2", frontMatter.Tier);
    }

    [Fact]
    public void Read_MinimalDocument_OptionalFieldsAreNullAndAliasesEmpty()
    {
        var frontMatter = VaultFrontMatterReader.Read(_validYaml);

        Assert.Null(frontMatter.Project);
        Assert.Null(frontMatter.Status);
        Assert.Empty(frontMatter.Aliases);
        Assert.Null(frontMatter.Owner);
        Assert.Null(frontMatter.SourceTrust);
        Assert.Null(frontMatter.Tier);
    }

    [Fact]
    public void Read_EmptyOptionalValues_BecomeNull()
    {
        const string yaml = _validYaml + "project: \"\"\nowner: '  '\nsource_trust:\nstatus: \"\"\ntier: \"\"\n";

        var frontMatter = VaultFrontMatterReader.Read(yaml);

        Assert.Null(frontMatter.Project);
        Assert.Null(frontMatter.Owner);
        Assert.Null(frontMatter.SourceTrust);
        Assert.Null(frontMatter.Status);
        Assert.Null(frontMatter.Tier);
    }

    [Fact]
    public void Read_LowerCaseUlid_IsAccepted()
    {
        var frontMatter = VaultFrontMatterReader.Read("id: 01arz3ndektsv4rrffq69g5fav\ntype: memory\ntitle: Note\n");

        Assert.Equal("01arz3ndektsv4rrffq69g5fav", frontMatter.Id);
    }

    [Fact]
    public void Read_EmptyBlock_ReportsMissingId()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(string.Empty));

        Assert.Contains("'id' is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MalformedYaml_ThrowsVaultFormatException()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read("id: [unclosed\n"));

        Assert.Contains("Malformed front matter YAML", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TopLevelSequence_ThrowsVaultFormatException()
    {
        Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read("- a\n- b\n"));
    }

    [Fact]
    public void Read_DuplicateKey_ThrowsVaultFormatException()
    {
        Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(_validYaml + "type: memory\n"));
    }

    [Fact]
    public void Read_MissingId_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read("type: plan\ntitle: T\n"));

        Assert.Contains("'id' is missing", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAVX")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAI")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAL")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAO")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAU")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA-")]
    [InlineData("not-a-ulid")]
    public void Read_InvalidUlid_Throws(string id)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {id}\ntype: plan\ntitle: T\n"));

        Assert.Contains("ULID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MissingType_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntitle: T\n"));

        Assert.Contains("'type' is missing", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("Plan")]
    [InlineData("")]
    public void Read_UnknownType_Throws(string type)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: \"{type}\"\ntitle: T\n"));

        Assert.Contains("'type'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deep-dive")]
    [InlineData("charter")]
    [InlineData("draft")]
    public void Read_KnownType_IsAccepted(string type)
    {
        var frontMatter = VaultFrontMatterReader.Read($"id: {_validId}\ntype: {type}\ntitle: T\n");

        Assert.Equal(type, frontMatter.Type);
    }

    [Fact]
    public void Read_MissingTitle_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: plan\n"));

        Assert.Contains("'title' is missing", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Read_EmptyOrWhitespaceTitle_Throws(string title)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: plan\ntitle: {title}\n"));

        Assert.Contains("'title' must not be empty", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Plan # two")]
    [InlineData("A | B")]
    [InlineData("A ^ B")]
    [InlineData("Time: now")]
    [InlineData("Odd %% name")]
    [InlineData("See [[link]]")]
    [InlineData("End ]] here")]
    public void Read_TitleWithForbiddenFragment_Throws(string title)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: plan\ntitle: \"{title}\"\n"));

        Assert.Contains("'title' must not contain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TitleWithSingleBracketsAndHyphens_IsAccepted()
    {
        var frontMatter = VaultFrontMatterReader.Read($"id: {_validId}\ntype: plan\ntitle: \"Plan [draft] - v2 (final)\"\n");

        Assert.Equal("Plan [draft] - v2 (final)", frontMatter.Title);
    }

    [Fact]
    public void Read_DecisionWithoutStatus_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: decision\ntitle: T\n"));

        Assert.Contains("'status' is required", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("Accepted")]
    [InlineData("rejected")]
    public void Read_DecisionWithInvalidStatus_Throws(string status)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read($"id: {_validId}\ntype: decision\ntitle: T\nstatus: {status}\n"));

        Assert.Contains("'status'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("proposed")]
    [InlineData("accepted")]
    [InlineData("superseded")]
    public void Read_DecisionWithValidStatus_IsAccepted(string status)
    {
        var frontMatter = VaultFrontMatterReader.Read($"id: {_validId}\ntype: decision\ntitle: T\nstatus: {status}\n");

        Assert.Equal(status, frontMatter.Status);
    }

    [Fact]
    public void Read_NonDecisionAcceptsAnyStatus()
    {
        var frontMatter = VaultFrontMatterReader.Read($"id: {_validId}\ntype: plan\ntitle: T\nstatus: in review\n");

        Assert.Equal("in review", frontMatter.Status);
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    [InlineData("T2")]
    [InlineData("T3")]
    public void Read_ValidTier_IsAccepted(string tier)
    {
        var frontMatter = VaultFrontMatterReader.Read(_validYaml + $"tier: {tier}\n");

        Assert.Equal(tier, frontMatter.Tier);
    }

    [Theory]
    [InlineData("T4")]
    [InlineData("t1")]
    [InlineData("1")]
    [InlineData("T1 ")]
    public void Read_InvalidTier_Throws(string tier)
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(_validYaml + $"tier: \"{tier}\"\n"));

        Assert.Contains("'tier'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AliasesAsScalar_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(_validYaml + "aliases: just-one\n"));

        Assert.Contains("'aliases' must be a sequence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AliasesWithNullEntry_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(_validYaml + "aliases:\n  - ok\n  - ~\n"));

        Assert.Contains("only strings", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AliasesAbsentOrNull_GivesEmptyList()
    {
        Assert.Empty(VaultFrontMatterReader.Read(_validYaml + "aliases:\n").Aliases);
    }

    [Fact]
    public void Read_NonStringProject_Throws()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFrontMatterReader.Read(_validYaml + "project:\n  - a\n"));

        Assert.Contains("'project' must be a string", error.Message, StringComparison.Ordinal);
    }
}
