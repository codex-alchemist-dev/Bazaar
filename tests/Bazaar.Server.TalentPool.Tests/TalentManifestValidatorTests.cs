namespace Bazaar.Server.TalentPool.Tests;

public sealed class TalentManifestValidatorTests
{
    private static TalentManifestValidation Validate(TalentManifest m) => TalentManifestValidator.Validate(m, Samples.Models);

    private static void AssertRejectedWith(TalentManifestValidation result, string code)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Validated);
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    [Fact]
    public void A_plain_manifest_is_valid()
    {
        var result = Validate(Samples.Manifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("memory/notes.md")]
    [InlineData("MEMORY.md")]
    [InlineData("agent/memories.json")]
    [InlineData("mem/store.db")]
    public void A_memory_file_is_rejected(string path)
    {
        var result = Validate(Samples.Manifest() with { Files = [path] });

        AssertRejectedWith(result, TalentManifestErrorCodes.MemoryNotAllowed);
    }

    [Fact]
    public void A_memory_field_is_rejected()
    {
        AssertRejectedWith(Validate(Samples.Manifest() with { ExtraFields = ["memory"] }), TalentManifestErrorCodes.MemoryNotAllowed);
    }

    [Fact]
    public void Any_other_extra_file_or_field_is_rejected_too()
    {
        AssertRejectedWith(Validate(Samples.Manifest() with { Files = ["notes.txt"] }), TalentManifestErrorCodes.UnexpectedContent);
        AssertRejectedWith(Validate(Samples.Manifest() with { ExtraFields = ["secrets"] }), TalentManifestErrorCodes.UnexpectedContent);
    }

    [Fact]
    public void A_model_outside_the_supplied_whitelist_is_rejected()
    {
        var result = Validate(Samples.Manifest(models: ["haiku-5-5", "gpt-mystery"]));

        AssertRejectedWith(result, TalentManifestErrorCodes.ModelNotWhitelisted);
        Assert.Contains(result.Errors, e => e.Message.Contains("gpt-mystery", StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_match_is_exact_not_case_folded()
    {
        AssertRejectedWith(Validate(Samples.Manifest(models: ["Haiku-5-5"])), TalentManifestErrorCodes.ModelNotWhitelisted);
    }

    [Fact]
    public void A_template_that_names_no_model_is_rejected()
    {
        AssertRejectedWith(Validate(Samples.Manifest(models: [])), TalentManifestErrorCodes.ModelsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    public void An_empty_prompt_is_rejected(string prompt)
    {
        AssertRejectedWith(Validate(Samples.Manifest(prompt: prompt)), TalentManifestErrorCodes.PromptEmpty);
    }

    [Fact]
    public void An_oversize_prompt_is_rejected()
    {
        var tooBig = new string('a', TalentManifestLimits.MaxPromptBytes + 1);

        AssertRejectedWith(Validate(Samples.Manifest(prompt: tooBig)), TalentManifestErrorCodes.PromptTooLarge);
        Assert.True(Validate(Samples.Manifest(prompt: new string('a', TalentManifestLimits.MaxPromptBytes))).IsValid);
    }

    [Fact]
    public void Oversize_lists_and_names_are_rejected()
    {
        var tools = Enumerable.Range(0, TalentManifestLimits.MaxTools + 1).Select(i => $"tool{i}").ToArray();
        AssertRejectedWith(Validate(Samples.Manifest(tools: tools)), TalentManifestErrorCodes.TooManyTools);
        AssertRejectedWith(Validate(Samples.Manifest(tools: [new string('t', TalentManifestLimits.MaxNameLength + 1)])), TalentManifestErrorCodes.BadName);
        AssertRejectedWith(Validate(Samples.Manifest(tools: [" padded"])), TalentManifestErrorCodes.BadName);
        AssertRejectedWith(Validate(Samples.Manifest(tools: [""])), TalentManifestErrorCodes.BadName);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var result = Validate(Samples.Manifest(prompt: "", models: ["nope"]) with { Files = ["memory/x"] });

        Assert.Contains(result.Errors, e => e.Code == TalentManifestErrorCodes.PromptEmpty);
        Assert.Contains(result.Errors, e => e.Code == TalentManifestErrorCodes.MemoryNotAllowed);
        Assert.Contains(result.Errors, e => e.Code == TalentManifestErrorCodes.ModelNotWhitelisted);
    }

    [Fact]
    public void The_content_sha_is_deterministic_and_ignores_order_duplicates_line_endings_and_template_id()
    {
        var a = Validate(Samples.Manifest(prompt: "line1\r\nline2", tools: ["b", "a", "a"], models: ["sonnet-5-5", "haiku-5-5"]));
        var b = Validate(Samples.Manifest(prompt: "line1\nline2", tools: ["a", "b"], models: ["haiku-5-5", "sonnet-5-5"], templateId: Guid.NewGuid()));

        Assert.Equal(a.Validated!.ContentSha, b.Validated!.ContentSha);
        Assert.Matches("^[0-9a-f]{64}$", a.Validated.ContentSha);
        Assert.Equal(["a", "b"], a.Validated.Manifest.ToolAllowlist);
    }

    [Fact]
    public void A_pinned_sha_catches_an_accidental_change_to_the_canonical_form()
    {
        var sha = Validate(new TalentManifest("p", ["t"], ["haiku-5-5"])).Validated!.ContentSha;

        // SHA-256 of {"prompt":"p","tools":["t"],"models":["haiku-5-5"]}. Changing the canonical form re-hashes every stored template.
        Assert.Equal("0319d7396df7a12fbf05454ed6550b164f1d83368a548bb716f4f46be47b129a", sha);
    }

    [Fact]
    public void Different_content_gives_a_different_sha()
    {
        var a = Validate(Samples.Manifest()).Validated!.ContentSha;

        Assert.NotEqual(a, Validate(Samples.Manifest(prompt: "Something else.")).Validated!.ContentSha);
        Assert.NotEqual(a, Validate(Samples.Manifest(tools: ["read_file"])).Validated!.ContentSha);
        Assert.NotEqual(a, Validate(Samples.Manifest(models: ["sonnet-5-5"])).Validated!.ContentSha);
    }
}
