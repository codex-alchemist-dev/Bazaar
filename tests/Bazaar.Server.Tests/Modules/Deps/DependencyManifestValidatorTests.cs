using Bazaar.Server.Modules.Deps;

namespace Bazaar.Server.Tests.Modules.Deps;

public class DependencyManifestValidatorTests
{
    private static readonly IReadOnlyList<string> _tags = ["Deps", "3rd party"];

    private static DependencyLine Newest(string line = "19", string version = "19.0.0") =>
        new(line, version, DependencyLineRole.Newest);

    private static DependencyLine Stable(string line = "18", string version = "18.6.0") =>
        new(line, version, DependencyLineRole.Stable);

    private static DependencyLine Retired(string line, string version = "17.9.0") =>
        new(line, version, DependencyLineRole.Retired);

    private static DependencyManifest Manifest(
        string id = "postgresql",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<DependencyLine>? lines = null,
        IReadOnlyList<string>? extensions = null,
        string licence = "PostgreSQL",
        DependencyLicenceVerdict verdict = DependencyLicenceVerdict.Compatible) =>
        new(id, "PostgreSQL", tags ?? _tags, lines ?? [Newest(), Stable()], extensions ?? ["pgvector"], licence, verdict);

    private static IEnumerable<DependencyManifestIssueCode> Codes(IEnumerable<DependencyManifestIssue> issues) =>
        issues.Select(i => i.Code);

    [Fact]
    public void A_well_formed_manifest_has_no_issues()
    {
        var issues = DependencyManifestValidator.Validate(Manifest());

        Assert.Empty(issues);
        Assert.True(DependencyManifestValidator.CanInstall(issues));
    }

    [Fact]
    public void Incompatible_licence_is_an_error_with_no_override()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(verdict: DependencyLicenceVerdict.Incompatible, licence: "SSPL-1.0"));

        var issue = Assert.Single(issues);
        Assert.Equal(DependencyManifestIssueCode.IncompatibleLicence, issue.Code);
        Assert.Equal(DependencyManifestIssueSeverity.Error, issue.Severity);
        Assert.Contains("SSPL-1.0", issue.Message, StringComparison.Ordinal);
        Assert.False(DependencyManifestValidator.CanInstall(issues));
    }

    [Fact]
    public void Unverified_licence_is_a_warning_that_still_installs()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(verdict: DependencyLicenceVerdict.Unverified));

        var issue = Assert.Single(issues);
        Assert.Equal(DependencyManifestIssueCode.UnverifiedLicence, issue.Code);
        Assert.Equal(DependencyManifestIssueSeverity.Warning, issue.Severity);
        Assert.True(DependencyManifestValidator.CanInstall(issues));
    }

    [Fact]
    public void Unknown_licence_verdict_is_refused_rather_than_guessed()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(verdict: (DependencyLicenceVerdict)99));

        Assert.Contains(issues, i => i.Code == DependencyManifestIssueCode.IncompatibleLicence
            && i.Severity == DependencyManifestIssueSeverity.Error);
    }

    [Fact]
    public void Empty_licence_identifier_is_an_error()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(licence: "  "));

        Assert.Equal([DependencyManifestIssueCode.MissingLicence], Codes(issues));
    }

    [Fact]
    public void Missing_deps_tag_or_third_party_tag_is_an_error()
    {
        var noDeps = DependencyManifestValidator.Validate(Manifest(tags: ["3rd party"]));
        var noThirdParty = DependencyManifestValidator.Validate(Manifest(tags: ["Deps"]));

        Assert.Equal([DependencyManifestIssueCode.MissingRequiredTag], Codes(noDeps));
        Assert.Contains("'Deps'", noDeps.Single().Message, StringComparison.Ordinal);
        Assert.Equal([DependencyManifestIssueCode.MissingRequiredTag], Codes(noThirdParty));
        Assert.Contains("'3rd party'", noThirdParty.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_tags_are_case_sensitive()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(tags: ["deps", "3rd Party"]));

        Assert.Equal(2, issues.Count(i => i.Code == DependencyManifestIssueCode.MissingRequiredTag));
    }

    [Fact]
    public void Extra_tags_are_allowed()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(tags: ["Deps", "3rd party", "database"]));

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PostgreSQL")]
    [InlineData("post_gres")]
    [InlineData("-postgres")]
    [InlineData("postgres-")]
    [InlineData("post--gres")]
    [InlineData("postgres space")]
    [InlineData("../escape")]
    public void Module_id_outside_the_slug_pattern_is_an_error(string id)
    {
        var issues = DependencyManifestValidator.Validate(Manifest(id: id));

        Assert.Contains(issues, i => i.Code == DependencyManifestIssueCode.InvalidModuleId);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("pgvector")]
    [InlineData("valkey-7")]
    [InlineData("a")]
    public void Module_id_inside_the_slug_pattern_is_accepted(string id)
    {
        Assert.True(DependencyManifestValidator.IsSafeSlug(id));
        Assert.DoesNotContain(DependencyManifestValidator.Validate(Manifest(id: id)), i => i.Code == DependencyManifestIssueCode.InvalidModuleId);
    }

    [Fact]
    public void Slug_longer_than_64_characters_is_rejected()
    {
        var tooLong = new string('a', 65);

        Assert.False(DependencyManifestValidator.IsSafeSlug(tooLong));
        Assert.True(DependencyManifestValidator.IsSafeSlug(new string('a', 64)));
        Assert.False(DependencyManifestValidator.IsSafeSlug(null));
    }

    [Fact]
    public void Empty_display_name_is_an_error()
    {
        var manifest = Manifest() with { DisplayName = " " };

        Assert.Equal([DependencyManifestIssueCode.MissingDisplayName], Codes(DependencyManifestValidator.Validate(manifest)));
    }

    [Fact]
    public void A_manifest_with_no_lines_is_an_error_and_skips_role_checks()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: []));

        Assert.Equal([DependencyManifestIssueCode.NoLines], Codes(issues));
    }

    [Fact]
    public void Exactly_one_newest_line_is_required()
    {
        var none = DependencyManifestValidator.Validate(Manifest(lines: [Stable()]));
        var two = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19"), Newest("20", "20.0.0")]));

        Assert.Contains(DependencyManifestIssueCode.NewestLineCount, Codes(none));
        Assert.Contains(DependencyManifestIssueCode.NewestLineCount, Codes(two));
        Assert.Contains("found 0", none.Single(i => i.Code == DependencyManifestIssueCode.NewestLineCount).Message, StringComparison.Ordinal);
        Assert.Contains("found 2", two.Single(i => i.Code == DependencyManifestIssueCode.NewestLineCount).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void At_most_one_stable_line_is_allowed()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest(), Stable("18"), Stable("17", "17.5.0")]));

        Assert.Equal([DependencyManifestIssueCode.MultipleStableLines], Codes(issues));
    }

    [Fact]
    public void Zero_stable_lines_is_fine()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest()]));

        Assert.Empty(issues);
    }

    [Fact]
    public void A_retired_line_below_the_newest_is_fine()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19"), Stable("18"), Retired("17")]));

        Assert.Empty(issues);
    }

    [Fact]
    public void A_retired_line_at_or_above_the_newest_is_an_error()
    {
        var sameNumber = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19"), Retired("19")]));
        var above = DependencyManifestValidator.Validate(Manifest(lines: [Newest("18"), Retired("19")]));

        // Same number also repeats the line id, so the duplicate rule fires alongside the retired-line rule.
        Assert.Equal([DependencyManifestIssueCode.DuplicateLineId, DependencyManifestIssueCode.RetiredLineIsNewest], Codes(sameNumber));
        Assert.Equal([DependencyManifestIssueCode.RetiredLineIsNewest], Codes(above));
    }

    [Fact]
    public void Line_ids_must_be_digits_only()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19-beta"), Stable("18")]));

        Assert.Contains(DependencyManifestIssueCode.InvalidLineId, Codes(issues));
    }

    [Fact]
    public void Duplicate_line_id_inside_one_module_is_an_error()
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19"), Stable("19", "19.0.0")]));

        Assert.Contains(DependencyManifestIssueCode.DuplicateLineId, Codes(issues));
    }

    [Theory]
    [InlineData("18.6")]
    [InlineData("18.6.0-beta1")]
    [InlineData("v18.6.0")]
    [InlineData("")]
    public void Line_version_must_be_three_part_semver(string version)
    {
        var issues = DependencyManifestValidator.Validate(Manifest(lines: [Newest("19", version), Stable()]));

        Assert.Equal([DependencyManifestIssueCode.InvalidVersion], Codes(issues));
    }

    [Fact]
    public void Two_lines_of_one_module_are_one_manifest_and_valid()
    {
        var manifest = Manifest(lines: [Newest("19"), Stable("18")]);

        Assert.Empty(DependencyManifestValidator.Validate(manifest));
        Assert.Empty(DependencyManifestValidator.ValidateCatalogue([manifest]));
    }

    [Fact]
    public void Extension_names_must_be_slugs_and_unique()
    {
        var invalid = DependencyManifestValidator.Validate(Manifest(extensions: ["pgvector", "Bad Name"]));
        var duplicate = DependencyManifestValidator.Validate(Manifest(extensions: ["pgvector", "pgvector"]));

        Assert.Equal([DependencyManifestIssueCode.InvalidExtensionName], Codes(invalid));
        Assert.Equal([DependencyManifestIssueCode.DuplicateExtension], Codes(duplicate));
    }

    [Fact]
    public void Catalogue_flags_a_duplicate_module_id_once_per_id()
    {
        var issues = DependencyManifestValidator.ValidateCatalogue(
        [
            Manifest(id: "postgresql"),
            Manifest(id: "postgresql"),
            Manifest(id: "postgresql"),
            Manifest(id: "valkey"),
        ]);

        var duplicate = Assert.Single(issues, i => i.Code == DependencyManifestIssueCode.DuplicateModuleId);
        Assert.Contains("'postgresql'", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_keeps_per_manifest_issues_alongside_duplicates()
    {
        var issues = DependencyManifestValidator.ValidateCatalogue(
        [
            Manifest(id: "postgresql"),
            Manifest(id: "postgresql", verdict: DependencyLicenceVerdict.Incompatible),
        ]);

        Assert.Contains(DependencyManifestIssueCode.DuplicateModuleId, Codes(issues));
        Assert.Contains(DependencyManifestIssueCode.IncompatibleLicence, Codes(issues));
        Assert.False(DependencyManifestValidator.CanInstall(issues));
    }

    [Fact]
    public void Empty_catalogue_has_no_issues()
    {
        Assert.Empty(DependencyManifestValidator.ValidateCatalogue([]));
    }

    [Fact]
    public void Null_arguments_throw_argument_exceptions_not_validation_issues()
    {
        Assert.Throws<ArgumentNullException>(() => DependencyManifestValidator.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => DependencyManifestValidator.ValidateCatalogue(null!));
        Assert.Throws<ArgumentNullException>(() => DependencyManifestValidator.CanInstall(null!));
    }

    [Fact]
    public void Warnings_alone_do_not_block_and_a_single_error_does()
    {
        var warningOnly = DependencyManifestValidator.Validate(Manifest(verdict: DependencyLicenceVerdict.Unverified));
        var warningAndError = warningOnly.Append(new DependencyManifestIssue(
            DependencyManifestIssueCode.MissingDisplayName, DependencyManifestIssueSeverity.Error, "x")).ToList();

        Assert.True(DependencyManifestValidator.CanInstall(warningOnly));
        Assert.False(DependencyManifestValidator.CanInstall(warningAndError));
    }

    [Fact]
    public void Required_tags_are_exposed_as_the_fixed_pair()
    {
        Assert.Equal(["Deps", "3rd party"], DependencyManifest.RequiredTags);
    }
}
