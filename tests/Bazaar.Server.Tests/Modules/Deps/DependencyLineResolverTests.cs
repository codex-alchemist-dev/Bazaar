using Bazaar.Server.Modules.Deps;

namespace Bazaar.Server.Tests.Modules.Deps;

public class DependencyLineResolverTests
{
    private static readonly DependencyLine _line19 = new("19", "19.0.0", DependencyLineRole.Newest);
    private static readonly DependencyLine _line18 = new("18", "18.6.0", DependencyLineRole.Stable);

    [Fact]
    public void A_required_line_that_is_installed_resolves_to_that_line()
    {
        var result = DependencyLineResolver.Resolve("postgresql", "18", [_line19, _line18]);

        Assert.True(result.Succeeded);
        Assert.Same(_line18, result.Line);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void A_missing_line_returns_a_missing_line_error_naming_the_module_and_present_lines()
    {
        var result = DependencyLineResolver.Resolve("postgresql", "17", [_line19, _line18]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Line);
        Assert.NotNull(result.Issue);
        Assert.Equal(DependencyManifestIssueCode.MissingLine, result.Issue.Code);
        Assert.Equal(DependencyManifestIssueSeverity.Error, result.Issue.Severity);
        Assert.Contains("'postgresql'", result.Issue.Message, StringComparison.Ordinal);
        Assert.Contains("'17'", result.Issue.Message, StringComparison.Ordinal);
        Assert.Contains("19, 18", result.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_line_with_no_installed_lines_says_none()
    {
        var result = DependencyLineResolver.Resolve("pgvector", "18", []);

        Assert.False(result.Succeeded);
        Assert.Contains("Installed lines: none.", result.Issue!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_is_exact_so_18_does_not_match_180()
    {
        var result = DependencyLineResolver.Resolve("postgresql", "1", [_line19, _line18]);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_retired_line_still_resolves_because_resolution_is_by_id_only()
    {
        var retired = new DependencyLine("17", "17.9.0", DependencyLineRole.Retired);

        var result = DependencyLineResolver.Resolve("postgresql", "17", [_line19, retired]);

        Assert.True(result.Succeeded);
        Assert.Same(retired, result.Line);
    }

    [Fact]
    public void Null_arguments_throw_argument_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => DependencyLineResolver.Resolve(null!, "18", []));
        Assert.Throws<ArgumentNullException>(() => DependencyLineResolver.Resolve("postgresql", null!, []));
        Assert.Throws<ArgumentNullException>(() => DependencyLineResolver.Resolve("postgresql", "18", null!));
    }
}
