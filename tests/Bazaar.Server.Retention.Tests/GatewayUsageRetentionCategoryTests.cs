using Bazaar.Server.Gateway;
using Bazaar.Server.Settings;
using Npgsql;

namespace Bazaar.Server.Retention.Tests;

public sealed class GatewayUsageRetentionCategoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeSettings _settings = new();

    // Never opened: only the settings paths are exercised here.
    private GatewayUsageRetentionCategory Category() => new(NpgsqlDataSource.Create("Host=localhost"), _settings);

    [Fact]
    public async Task Default_retention_is_indefinite()
    {
        Assert.Null(await Category().GetPeriodAsync(Ct));
    }

    [Fact]
    public async Task Configured_days_become_the_period()
    {
        _settings.Ints[GatewaySettings.UsageRetentionDays.Name] = 90;
        Assert.Equal(TimeSpan.FromDays(90), await Category().GetPeriodAsync(Ct));
    }

    [Fact]
    public async Task Batch_size_defaults_to_the_registered_default()
    {
        Assert.Equal(5_000, await Category().GetBatchSizeAsync(Ct));
    }

    [Fact]
    public async Task Usage_rows_are_not_tied_to_a_person()
    {
        Assert.Empty(await Category().GetCoveredPersonsAsync(Ct));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(GatewaySettings.UsagePurgeBatchSizeCap, true)]
    [InlineData(GatewaySettings.UsagePurgeBatchSizeCap + 1, false)]
    public void Batch_size_setting_enforces_one_to_hard_cap(int value, bool valid)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(valid, GatewaySettings.UsagePurgeBatchSize.CheckValue(doc.RootElement) is null);
    }

    [Fact]
    public void Batch_size_setting_is_registered_as_an_operator_install_key()
    {
        Assert.True(SettingRegistry.Known.Contains(GatewaySettings.UsagePurgeBatchSize));
        Assert.Equal(SettingScope.Install, GatewaySettings.UsagePurgeBatchSize.Scope);
    }
}
