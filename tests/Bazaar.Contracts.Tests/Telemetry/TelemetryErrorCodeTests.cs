using Bazaar.Contracts.Telemetry;

namespace Bazaar.Contracts.Tests.Telemetry;

public class TelemetryErrorCodeTests
{
    [Fact]
    public void Registry_has_no_duplicate_malformed_or_undescribed_codes() =>
        Assert.Empty(TelemetryErrorCodes.Validate(TelemetryErrorCodes.Registry));

    [Theory]
    [InlineData("BZ-GC-0042")]
    [InlineData("BZ-CLIENT-0001")]
    [InlineData("BZ-AB-9999")]
    public void Well_formed_codes_are_accepted(string code) => Assert.True(TelemetryErrorCodes.IsWellFormed(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BZ-GC-042")]
    [InlineData("BZ-GC-00042")]
    [InlineData("BZ-G-0042")]
    [InlineData("BZ-GCXXXXXXXXX-0042")]
    [InlineData("bz-gc-0042")]
    [InlineData("BZ-gc-0042")]
    [InlineData("XX-GC-0042")]
    [InlineData("BZ-GC-0042\n")]
    [InlineData("BZ-GC-004a")]
    public void Malformed_codes_are_rejected(string? code) => Assert.False(TelemetryErrorCodes.IsWellFormed(code));

    [Fact]
    public void Validate_reports_a_malformed_code()
    {
        var problems = TelemetryErrorCodes.Validate([new TelemetryErrorCodeEntry("GC-0042", "Bad shape")]);

        var problem = Assert.Single(problems);
        Assert.Contains("Malformed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_reports_a_duplicate_code()
    {
        var problems = TelemetryErrorCodes.Validate(
        [
            new TelemetryErrorCodeEntry("BZ-GC-0042", "First"),
            new TelemetryErrorCodeEntry("BZ-GC-0042", "Second"),
        ]);

        var problem = Assert.Single(problems);
        Assert.Contains("Duplicate", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_reports_a_missing_description(string description)
    {
        var problems = TelemetryErrorCodes.Validate([new TelemetryErrorCodeEntry("BZ-GC-0042", description)]);

        var problem = Assert.Single(problems);
        Assert.Contains("no description", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_a_clean_set() =>
        Assert.Empty(TelemetryErrorCodes.Validate(
        [
            new TelemetryErrorCodeEntry("BZ-GC-0042", "A GC post could not be saved."),
            new TelemetryErrorCodeEntry("BZ-CLIENT-0001", "The client could not start its renderer."),
        ]));

    [Fact]
    public void Unregistered_code_has_no_description() => Assert.Null(TelemetryErrorCodes.DescriptionOf("BZ-GC-9999"));

    [Fact]
    public void Null_code_has_no_description() => Assert.Null(TelemetryErrorCodes.DescriptionOf(null));
}
