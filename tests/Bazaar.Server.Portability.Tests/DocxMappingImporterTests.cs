namespace Bazaar.Server.Portability.Tests;

public class DocxMappingImporterTests
{
    [Fact]
    public void Format_id_and_declaration_read_only_the_staging_input()
    {
        var importer = new DocxMappingImporter();
        Assert.Equal("docx", importer.FormatId);
        Assert.Equal(["staging-input"], importer.Declaration.Reads);
        Assert.Equal([ImportTargetKind.VaultFile], importer.Declaration.Targets);
    }

    [Fact]
    public void Id_and_version_come_from_the_wrapped_importer()
    {
        var importer = new DocxMappingImporter();
        Assert.Equal(new DocxImporter().Id, importer.Id);
        Assert.Equal(new DocxImporter().Version, importer.Version);
    }

    [Fact]
    public void Detect_and_parse_match_the_wrapped_importer()
    {
        var fixture = DocxMappingImporter.BuiltInFixture();
        var wrapped = new DocxImporter();
        var adapter = new DocxMappingImporter();

        Assert.Equal(wrapped.Detect(fixture), adapter.Detect(fixture));
        var expected = wrapped.Parse(fixture);
        var actual = adapter.Parse(fixture);
        Assert.Equal(expected.Select(r => (r.SourceKey, r.Kind, r.Title, r.Body)), actual.Select(r => (r.SourceKey, r.Kind, r.Title, r.Body)));
    }

    [Fact]
    public void Built_in_fixture_is_detected_as_a_docx_package()
    {
        var importer = new DocxMappingImporter();
        Assert.True(importer.Detect(DocxMappingImporter.BuiltInFixture()).Confidence > 0);
    }

    [Fact]
    public void Map_proposes_each_record_as_a_vault_file_and_keeps_the_record()
    {
        var importer = new DocxMappingImporter();
        var records = importer.Parse(DocxMappingImporter.BuiltInFixture());
        Assert.NotEmpty(records);

        foreach (var record in records)
        {
            var result = importer.Map(record, MappingRules.Default);
            Assert.False(result.IsSkipped);
            Assert.Equal(ImportTargetKind.VaultFile, result.Target);
            Assert.Same(record, result.Record);
        }
    }

    [Fact]
    public void Map_is_deterministic_for_the_same_record_and_rules()
    {
        var importer = new DocxMappingImporter();
        var record = importer.Parse(DocxMappingImporter.BuiltInFixture())[0];
        Assert.Equal(importer.Map(record, MappingRules.Default), importer.Map(record, MappingRules.Default));
    }

    [Fact]
    public void Map_rejects_null_rules()
    {
        var importer = new DocxMappingImporter();
        var record = importer.Parse(DocxMappingImporter.BuiltInFixture())[0];
        Assert.Throws<ArgumentNullException>(() => importer.Map(record, null!));
    }

    [Fact]
    public void Self_test_passes()
    {
        var result = new DocxMappingImporter().SelfTest();
        Assert.True(result.Ok);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Contract_checks_pass_on_the_built_in_fixture() =>
        Assert.Empty(ImporterContractChecks.Check(new DocxMappingImporter(), DocxMappingImporter.BuiltInFixture()));
}
