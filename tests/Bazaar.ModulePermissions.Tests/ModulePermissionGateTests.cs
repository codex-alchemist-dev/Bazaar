using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.ModulePermissions.Tests;

public class ModulePermissionGateTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 6, 0, 0, TimeSpan.Zero);

    private sealed class ListSink : IModulePermissionDenialSink
    {
        private readonly Lock _gate = new();

        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial)
        {
            lock (_gate)
            {
                Denials.Add(denial);
            }
        }
    }

    private sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ThrowingSink : IModulePermissionDenialSink
    {
        public void Record(ModulePermissionDenial denial) => throw new IOException("audit store is down");
    }

    private static (ModulePermissionGate Gate, ListSink Sink, FakeTimeProvider Time) Build()
    {
        var sink = new ListSink();
        var time = new FakeTimeProvider(_t0);
        return (new ModulePermissionGate(sink, time), sink, time);
    }

    [Fact]
    public void A_running_module_gets_exactly_its_approved_set()
    {
        var (gate, sink, _) = Build();
        var summary = gate.Activate("bazaar.weather", ["network:api.example.com", "secret:weather/api-key", "fs:data/weather/*", "gc.post"]);

        Assert.Equal(4, summary.Granted.Count);
        Assert.Empty(summary.Ignored);
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("network", "api.example.com")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("secret", "weather/api-key")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("fs", "data/weather/cache/today.json")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("gc.post")));
        Assert.Empty(sink.Denials);

        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("secret", "owner/recovery-key")));
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("network", "evil.test")));
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("process", "sh")));
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("fs", "data/weather")));
        Assert.Equal(4, sink.Denials.Count);
    }

    [Fact]
    public void A_refusal_is_audited_with_the_module_the_class_and_the_clean_target()
    {
        var (gate, sink, _) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);

        gate.Check("bazaar.weather", new("secret", "owner/recovery-key"));

        var denial = Assert.Single(sink.Denials);
        Assert.Equal(_t0, denial.At);
        Assert.Equal("bazaar.weather", denial.ModuleId);
        Assert.Equal("secret", denial.Class);
        Assert.Equal("owner/recovery-key", denial.Target);
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, denial.Decision);
        Assert.Equal(0, denial.SuppressedBefore);
    }

    [Theory]
    [InlineData("fs", "data/weather/../../etc/passwd")]
    [InlineData("fs", "data\\weather\\x")]
    [InlineData("fs", "data/weather/*")]
    [InlineData("secret", "weather/api-key\n{\"kind\":\"module.permissions.approved\"}")]
    [InlineData("network", "api.example.com:22")]
    [InlineData("network", "2130706433")]
    public void A_malformed_target_is_refused_and_its_text_never_reaches_the_audit_log(string @class, string target)
    {
        var (gate, sink, _) = Build();
        gate.Activate("bazaar.weather", ["fs:data/weather/*", "secret:weather/*", "network:api.example.com"]);

        Assert.Equal(ModuleAccessDecision.DeniedInvalidTarget, gate.Check("bazaar.weather", new(@class, target)));

        var denial = Assert.Single(sink.Denials);
        Assert.Null(denial.Target);
        Assert.Equal(target.Length, denial.TargetLength);
    }

    [Fact]
    public void An_unknown_class_is_refused_and_its_name_never_reaches_the_audit_log()
    {
        var (gate, sink, _) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);

        Assert.Equal(ModuleAccessDecision.DeniedUnknownClass, gate.Check("bazaar.weather", new("GC.POST")));
        Assert.Equal(ModuleAccessDecision.DeniedUnknownClass, gate.Check("bazaar.weather", new("admin\n", "x")));

        Assert.All(sink.Denials, d => Assert.Equal("(unknown)", d.Class));
        Assert.All(sink.Denials, d => Assert.Null(d.Target));
    }

    [Fact]
    public void Approved_strings_that_are_not_permissions_grant_nothing_and_are_reported()
    {
        var (gate, _, _) = Build();

        var summary = gate.Activate("bazaar.weather", ["fs:*", "*", "fs", "network:*.example.com", "fs:data/weather/*", "fs:data/weather/*"]);

        Assert.Equal(["fs:data/weather/*"], summary.Granted);
        Assert.Equal(["fs:*", "*", "fs", "network:*.example.com"], summary.Ignored);
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("fs", "etc/passwd")));
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("network", "a.example.com")));
    }

    [Fact]
    public void A_module_that_is_not_running_is_refused_everything()
    {
        var (gate, sink, _) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);

        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("bazaar.other", new("gc.post")));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("BAZAAR.WEATHER", new("gc.post")));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("bazaar.weather ", new("gc.post")));

        Assert.True(gate.Deactivate("bazaar.weather"));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("bazaar.weather", new("gc.post")));
        Assert.False(gate.Deactivate("bazaar.weather"));
        Assert.All(sink.Denials, d => Assert.Equal("(not running)", d.ModuleId));
    }

    [Fact]
    public void A_set_in_force_cannot_be_replaced_or_widened_in_place()
    {
        var (gate, _, _) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);

        Assert.Throws<InvalidOperationException>(() => gate.Activate("bazaar.weather", ["gc.post", "secret:owner/*"]));

        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("secret", "owner/key")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("gc.post")));
    }

    [Fact]
    public void One_modules_grants_never_serve_another_module()
    {
        var (gate, _, _) = Build();
        gate.Activate("bazaar.weather", ["secret:weather/*"]);
        gate.Activate("bazaar.notes", ["secret:notes/*"]);

        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.notes", new("secret", "weather/api-key")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.notes", new("secret", "notes/token")));
    }

    [Fact]
    public void A_restart_with_a_narrower_set_drops_the_old_grants()
    {
        var (gate, _, _) = Build();
        gate.Activate("bazaar.weather", ["secret:weather/*", "gc.post"]);
        gate.Deactivate("bazaar.weather");
        gate.Activate("bazaar.weather", ["gc.post"]);

        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("secret", "weather/api-key")));
    }

    [Fact]
    public void A_denial_flood_reaches_the_audit_log_a_bounded_number_of_times_and_the_dropped_count_is_kept()
    {
        var (gate, sink, time) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);
        gate.Activate("bazaar.notes", ["gc.post"]);

        for (var i = 0; i < 10_000; i++)
        {
            Assert.Equal(ModuleAccessDecision.DeniedNotGranted, gate.Check("bazaar.weather", new("secret", $"guess/{i}")));
        }

        Assert.Equal(ModulePermissionGate.MaxDenialsPerWindow, sink.Denials.Count);

        // Another module's refusals are not crowded out by the flood.
        gate.Check("bazaar.notes", new("secret", "x"));
        Assert.Equal("bazaar.notes", sink.Denials[^1].ModuleId);

        time.Advance(ModulePermissionGate.DenialWindow);
        gate.Check("bazaar.weather", new("secret", "after"));
        Assert.Equal(10_000 - ModulePermissionGate.MaxDenialsPerWindow, sink.Denials[^1].SuppressedBefore);

        gate.Check("bazaar.weather", new("secret", "again"));
        Assert.Equal(0, sink.Denials[^1].SuppressedBefore);
    }

    [Fact]
    public void Made_up_module_ids_share_one_refusal_budget()
    {
        var (gate, sink, _) = Build();

        for (var i = 0; i < 5_000; i++)
        {
            gate.Check($"ghost.{i}", new("gc.post"));
        }

        Assert.Equal(ModulePermissionGate.MaxDenialsPerWindow, sink.Denials.Count);
    }

    [Fact]
    public void A_clock_that_steps_back_does_not_silence_the_audit_for_good()
    {
        var sink = new ListSink();
        var time = new SettableTime { Now = _t0 };
        var gate = new ModulePermissionGate(sink, time);
        gate.Activate("bazaar.weather", ["gc.post"]);
        for (var i = 0; i < ModulePermissionGate.MaxDenialsPerWindow + 5; i++)
        {
            gate.Check("bazaar.weather", new("secret", "x"));
        }

        time.Now = _t0.AddDays(-1);
        gate.Check("bazaar.weather", new("secret", "x"));

        Assert.Equal(ModulePermissionGate.MaxDenialsPerWindow + 1, sink.Denials.Count);
    }

    [Fact]
    public void Demand_throws_on_a_refusal_and_returns_on_a_grant()
    {
        var (gate, _, _) = Build();
        gate.Activate("bazaar.weather", ["gc.post"]);

        gate.Demand("bazaar.weather", new("gc.post"));
        var refused = Assert.Throws<ModulePermissionDeniedException>(() => gate.Demand("bazaar.weather", new("process", "sh")));

        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, refused.Decision);
        Assert.DoesNotContain("sh", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_audit_sink_fails_the_access_is_still_refused()
    {
        var gate = new ModulePermissionGate(new ThrowingSink(), new FakeTimeProvider(_t0));
        gate.Activate("bazaar.weather", ["gc.post"]);

        Assert.Throws<IOException>(() => gate.Check("bazaar.weather", new("secret", "x")));
        Assert.Throws<IOException>(() => gate.Demand("bazaar.weather", new("secret", "x")));
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("gc.post")));
    }

    [Fact]
    public void Too_many_permissions_or_a_bad_module_id_do_not_start()
    {
        var (gate, _, _) = Build();

        Assert.Throws<ArgumentException>(() => gate.Activate("bazaar.big", Enumerable.Range(0, ModulePermissionGate.MaxPermissions + 1).Select(i => $"secret:s{i}")));
        Assert.ThrowsAny<ArgumentException>(() => gate.Activate(" ", ["gc.post"]));
        Assert.ThrowsAny<ArgumentException>(() => gate.Activate(new string('a', ModulePermissionGate.MaxModuleIdLength + 1), ["gc.post"]));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("bazaar.big", new("secret", "s1")));
    }

    [Fact]
    public async Task Concurrent_checks_starts_and_stops_never_allow_an_ungranted_access()
    {
        var (gate, _, _) = Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var allowedWrongly = 0;

        var churn = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    gate.Activate("bazaar.weather", ["secret:weather/*"]);
                    gate.Deactivate("bazaar.weather");
                }
            },
            TestContext.Current.CancellationToken);
        var probes = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (gate.Check("bazaar.weather", new("secret", "owner/key")) == ModuleAccessDecision.Allowed)
                    {
                        Interlocked.Increment(ref allowedWrongly);
                    }
                }
            },
            TestContext.Current.CancellationToken));

        await Task.WhenAll([churn, .. probes]);

        Assert.Equal(0, allowedWrongly);
    }

    [Fact]
    public void The_container_serves_one_gate_and_needs_an_audit_sink()
    {
        var without = new ServiceCollection().AddBazaarModulePermissions().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => without.GetRequiredService<IModulePermissionGate>());

        var services = new ServiceCollection().AddSingleton<IModulePermissionDenialSink, ListSink>().AddBazaarModulePermissions().BuildServiceProvider();
        var gate = services.GetRequiredService<IModulePermissionGate>();
        services.GetRequiredService<IModulePermissionRegistry>().Activate("bazaar.weather", ["gc.post"]);

        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("bazaar.weather", new("gc.post")));
    }
}
