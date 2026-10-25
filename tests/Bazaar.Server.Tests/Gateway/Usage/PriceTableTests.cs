using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class PriceTableTests
{
    private const string _provider = "anthropic-direct";
    private const string _model = "claude-haiku-5-5";

    private static readonly PriceEntry _early = new(_provider, _model, new DateOnly(2025, 1, 1), 1.00m, 5.00m);
    private static readonly PriceEntry _later = new(_provider, _model, new DateOnly(2025, 6, 1), 2.00m, 10.00m);

    private static PriceTable Table() => new("2025-06", [_later, _early]);

    [Fact]
    public void Version_is_kept_for_the_price_version_column()
    {
        Assert.Equal("2025-06", Table().Version);
    }

    [Fact]
    public void A_date_before_the_first_entry_has_no_price()
    {
        var at = new DateTimeOffset(2024, 12, 31, 23, 59, 0, TimeSpan.Zero);

        Assert.Null(Table().Lookup(_provider, _model, at));
    }

    [Fact]
    public void The_first_entry_applies_on_its_own_date()
    {
        var at = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Same(_early, Table().Lookup(_provider, _model, at));
    }

    [Fact]
    public void Between_two_dates_the_earlier_entry_applies()
    {
        var at = new DateTimeOffset(2025, 3, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Same(_early, Table().Lookup(_provider, _model, at));
    }

    [Fact]
    public void The_later_entry_applies_on_its_own_date()
    {
        var at = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Same(_later, Table().Lookup(_provider, _model, at));
    }

    [Fact]
    public void A_date_after_the_last_entry_uses_the_latest_price()
    {
        var at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Same(_later, Table().Lookup(_provider, _model, at));
    }

    [Fact]
    public void The_date_is_judged_in_utc_not_local_time()
    {
        // 23:30 on 31 May at UTC-5 is 04:30 on 1 June in UTC, so the June price applies.
        var lateNightInAmerica = new DateTimeOffset(2025, 5, 31, 23, 30, 0, TimeSpan.FromHours(-5));
        // 00:30 on 1 June at UTC+2 is 22:30 on 31 May in UTC, so the earlier price still applies.
        var earlyMorningInEurope = new DateTimeOffset(2025, 6, 1, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Same(_later, Table().Lookup(_provider, _model, lateNightInAmerica));
        Assert.Same(_early, Table().Lookup(_provider, _model, earlyMorningInEurope));
    }

    [Fact]
    public void An_unknown_provider_or_model_has_no_price()
    {
        var at = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(Table().Lookup("freellmapi", _model, at));
        Assert.Null(Table().Lookup(_provider, "claude-opus-5-5", at));
    }

    [Fact]
    public void A_duplicate_provider_model_and_date_is_rejected()
    {
        var twin = new PriceEntry(_provider, _model, new DateOnly(2025, 1, 1), 9.00m, 9.00m);

        Assert.Throws<ArgumentException>(() => new PriceTable("v", [_early, twin]));
    }

    [Fact]
    public void The_same_date_for_another_model_is_not_a_duplicate()
    {
        var other = new PriceEntry(_provider, "claude-sonnet-5-5", new DateOnly(2025, 1, 1), 3.00m, 15.00m);

        var table = new PriceTable("v", [_early, other]);

        Assert.Same(other, table.Lookup(_provider, "claude-sonnet-5-5", new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
