namespace Bazaar.Tabula.Tests;

public class PolicyAndVocabularyTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_policy_keeps_everything_forever()
    {
        foreach (AuditClass c in Enum.GetValues<AuditClass>())
        {
            Assert.Equal(RetentionMode.Indefinite, AuditRetentionPolicy.KeepForever.RuleFor(c).Mode);
            Assert.Null(AuditRetentionPolicy.KeepForever.Horizon(c, _now));
        }
    }

    [Fact]
    public void Fixed_window_gives_a_horizon_that_is_a_pure_function_of_now()
    {
        AuditRetentionPolicy policy = new([new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Fixed, 400)]);

        Assert.Equal(_now.AddDays(-400), policy.Horizon(AuditClass.DataRead, _now));
        Assert.Null(policy.Horizon(AuditClass.DataWrite, _now));
    }

    [Theory]
    [InlineData(AuditClass.Security)]
    [InlineData(AuditClass.Admin)]
    [InlineData(AuditClass.System)]
    public void Never_pruned_classes_refuse_a_window(AuditClass c) =>
        Assert.Throws<ArgumentException>(() => new AuditRetentionPolicy([new AuditRetentionRule(c, RetentionMode.Fixed, 9999)]));

    [Fact]
    public void Window_below_host_floor_is_refused()
    {
        Dictionary<AuditClass, int> floors = new() { [AuditClass.DataRead] = 365 };

        Assert.Throws<ArgumentException>(() =>
            new AuditRetentionPolicy([new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Fixed, 364)], floors));
        _ = new AuditRetentionPolicy([new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Fixed, 365)], floors);
    }

    [Fact]
    public void Malformed_rules_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new AuditRetentionPolicy([new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Fixed, null)]));
        Assert.Throws<ArgumentException>(() => new AuditRetentionPolicy([new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Indefinite, 5)]));
        Assert.Throws<ArgumentException>(() => new AuditRetentionPolicy(
        [
            new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Indefinite, null),
            new AuditRetentionRule(AuditClass.DataRead, RetentionMode.Indefinite, null),
        ]));
    }

    private sealed class Source(string moduleId, params AuditOperation[] ops) : IAuditOperationSource
    {
        public string ModuleId => moduleId;

        public IEnumerable<AuditOperation> Operations => ops;
    }

    private static AuditOperation Op(string module, string kind, OperationStatus status = OperationStatus.Active, string? until = null) =>
        new(kind, module, AuditClass.Admin, ["who"], true, false, status, until, "doc x");

    [Fact]
    public void Registry_resolves_kinds_and_hash_ignores_registration_order()
    {
        Source a = new("alpha", Op("alpha", "alpha.thing.made"));
        Source b = new("beta", Op("beta", "beta.thing.made"));

        AuditOperationRegistry r1 = new([a, b]);
        AuditOperationRegistry r2 = new([b, a]);

        Assert.True(r1.TryGet("alpha.thing.made", out AuditOperation? op));
        Assert.Equal("alpha", op.ModuleId);
        Assert.False(r1.TryGet("nope.thing.made", out _));
        Assert.Equal(r1.RegistryHash, r2.RegistryHash);
        Assert.Equal(64, r1.RegistryHash.Length);
        Assert.NotEqual(r1.RegistryHash, new AuditOperationRegistry([a]).RegistryHash);
    }

    [Fact]
    public void Registry_rejects_duplicates_bad_kinds_and_planned_without_version()
    {
        InvalidOperationException dup = Assert.Throws<InvalidOperationException>(() =>
            new AuditOperationRegistry([new Source("alpha", Op("alpha", "x.thing.made")), new Source("beta", Op("beta", "x.thing.made"))]));
        Assert.Contains("alpha", dup.Message);
        Assert.Contains("beta", dup.Message);

        Assert.Throws<ArgumentException>(() => new AuditOperationRegistry([new Source("alpha", Op("alpha", "Bad Kind"))]));
        Assert.Throws<ArgumentException>(() => new AuditOperationRegistry([new Source("alpha", Op("beta", "alpha.thing.made"))]));
        Assert.Throws<ArgumentException>(() =>
            new AuditOperationRegistry([new Source("alpha", Op("alpha", "alpha.thing.made", OperationStatus.Planned))]));
        _ = new AuditOperationRegistry([new Source("alpha", Op("alpha", "alpha.thing.made", OperationStatus.Planned, "0.5.0"))]);
    }

    [Fact]
    public void Access_grant_kinds_all_match_the_vocabulary_pattern()
    {
        Assert.Equal(5, AccessGrantEvent.OperationKinds.Count);
        _ = new AuditOperationRegistry([new Source("access_grant", [.. AccessGrantEvent.OperationKinds.Select(k => Op("access_grant", k))])]);
    }

    private static AccessGrantEvent Grant(
        AccessGrantReason reason = AccessGrantReason.Support,
        string label = "ticket_review",
        DateTimeOffset? expires = null) =>
        new(Guid.NewGuid(), "user:o", "user:s", null, "logs", "all", reason, label, expires ?? _now.AddMinutes(60), null);

    [Fact]
    public void Access_grant_requires_reason_label_and_future_expiry()
    {
        Grant().Validate(_now);

        Assert.Throws<ArgumentException>(() => Grant(reason: (AccessGrantReason)99).Validate(_now));
        Assert.Throws<ArgumentException>(() => Grant(label: "Free text reason!").Validate(_now));
        Assert.Throws<ArgumentException>(() => Grant(label: string.Empty).Validate(_now));
        Assert.Throws<ArgumentException>(() => Grant(expires: _now).Validate(_now));
        Assert.Throws<ArgumentException>(() => (Grant() with { Grantee = " " }).Validate(_now));
    }
}
