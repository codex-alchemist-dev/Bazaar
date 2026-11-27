using Bazaar.LoopGuard.Abstractions;
using Bazaar.Sanitization;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.PsychWard.Tests;

public class PsychWardTests
{
    private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _b = Guid.Parse("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid _reviewer = Guid.Parse("00000000-0000-7000-8000-0000000000cc");

    private sealed class FakeSanitizer : ITextSanitizer
    {
        public SanitizedText Sanitize(string text, SensitiveTerms terms) =>
            new(terms.People.Aggregate(text, (t, p) => t.Replace(p, "[person]", StringComparison.Ordinal)), []);
    }

    private sealed class RecordingPublisher : IVentPublisher
    {
        public List<(VentDraft Draft, string Target)> Posted { get; } = [];

        public Task PublishAsync(VentDraft draft, string target, CancellationToken cancellationToken)
        {
            Posted.Add((draft, target));
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider Host(FakeTimeProvider time, PsychWardSettings? settings = null, IVentPublisher? publisher = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ITextSanitizer, FakeSanitizer>();
        if (settings is not null)
        {
            services.AddSingleton(settings);
        }

        if (publisher is not null)
        {
            services.AddSingleton(publisher);
        }

        services.AddBazaarPsychWard();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Loop_guard_trip_admits_every_agent_involved_and_restricts_them()
    {
        using var sp = Host(new FakeTimeProvider());
        var gate = sp.GetRequiredService<IPsychWardGate>();
        var ward = sp.GetRequiredService<IPsychWardService>();

        await gate.AdmitAsync(new LoopTrip(Guid.NewGuid(), "ping_pong", "too many bounces", [_a, _b]), CancellationToken.None);

        var overview = await ward.GetOverviewAsync(CancellationToken.None);
        Assert.Equal(2, overview.ActiveStays.Count);
        Assert.All(overview.ActiveStays, s => Assert.Equal(WardTrigger.LoopGuard, s.Trigger));
        Assert.Contains(overview.ActiveStays, s => s.Detail == "ping_pong: too many bounces");
        Assert.Equal(WardRestrictions.Admitted, await ward.GetRestrictionsAsync(_a, CancellationToken.None));
        Assert.Equal(WardRestrictions.None, await ward.GetRestrictionsAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task The_default_gate_is_the_real_one_not_the_no_op()
    {
        using var sp = Host(new FakeTimeProvider());

        Assert.IsType<PsychWardGate>(sp.GetRequiredService<IPsychWardGate>());
    }

    [Fact]
    public async Task A_second_trip_does_not_open_a_second_stay()
    {
        using var sp = Host(new FakeTimeProvider());
        var gate = sp.GetRequiredService<IPsychWardGate>();
        var trip = new LoopTrip(Guid.NewGuid(), "mention_depth", "deep", [_a]);

        await Task.WhenAll(gate.AdmitAsync(trip, CancellationToken.None), gate.AdmitAsync(trip, CancellationToken.None));

        var overview = await sp.GetRequiredService<IPsychWardService>().GetOverviewAsync(CancellationToken.None);
        Assert.Single(overview.ActiveStays);
    }

    [Fact]
    public async Task Owner_only_mode_refuses_automatic_admission_but_not_the_owner()
    {
        using var sp = Host(new FakeTimeProvider(), new PsychWardSettings { AdmissionMode = WardAdmissionMode.OwnerOnly });
        var ward = sp.GetRequiredService<IPsychWardService>();

        var auto = await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, CancellationToken.None);
        var owner = await ward.AdmitAsync(_a, WardTrigger.OwnerReferral, null, CancellationToken.None);

        Assert.False(auto.Accepted);
        Assert.True(owner.Accepted);
    }

    [Fact]
    public async Task Discharge_waits_for_the_minimum_stay_and_an_allowed_discharger()
    {
        var time = new FakeTimeProvider();
        using var sp = Host(time);
        var ward = sp.GetRequiredService<IPsychWardService>();
        await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, CancellationToken.None);

        Assert.False((await ward.DischargeAsync(_a, new DischargeRequest(true), CancellationToken.None)).Accepted);

        time.Advance(TimeSpan.FromMinutes(61));

        Assert.False((await ward.DischargeAsync(_a, new DischargeRequest(false, _reviewer, false), CancellationToken.None)).Accepted);
        Assert.False((await ward.DischargeAsync(_a, new DischargeRequest(false, _a, true), CancellationToken.None)).Accepted);
        var ok = await ward.DischargeAsync(_a, new DischargeRequest(false, _reviewer, true), CancellationToken.None);
        Assert.True(ok.Accepted);
        Assert.Equal(_reviewer, ok.Stay!.ReviewerId);
        Assert.Equal(WardRestrictions.None, await ward.GetRestrictionsAsync(_a, CancellationToken.None));
    }

    [Fact]
    public async Task Owner_only_discharge_refuses_a_reviewer()
    {
        var time = new FakeTimeProvider();
        using var sp = Host(time, new PsychWardSettings { DischargeBy = WardDischargeBy.Owner, MinStayMinutes = 0 });
        var ward = sp.GetRequiredService<IPsychWardService>();
        await ward.AdmitAsync(_a, WardTrigger.SelfReferral, null, CancellationToken.None);

        Assert.False((await ward.DischargeAsync(_a, new DischargeRequest(false, _reviewer, true), CancellationToken.None)).Accepted);
        Assert.True((await ward.DischargeAsync(_a, new DischargeRequest(true), CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task Check_in_needs_an_active_stay_and_is_kept_on_the_stay()
    {
        using var sp = Host(new FakeTimeProvider());
        var ward = sp.GetRequiredService<IPsychWardService>();
        var checkIn = new WardCheckIn("tried x", "y fails", "a fresh look", "the build");

        Assert.False((await ward.CheckInAsync(_a, checkIn, CancellationToken.None)).Accepted);
        await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, CancellationToken.None);
        var outcome = await ward.CheckInAsync(_a, checkIn, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal(checkIn, outcome.Stay!.CheckIn);
    }

    [Fact]
    public void Negative_minimum_stay_is_rejected()
    {
        using var sp = Host(new FakeTimeProvider(), new PsychWardSettings { MinStayMinutes = -1 });

        Assert.Throws<ArgumentOutOfRangeException>(() => sp.GetRequiredService<IPsychWardService>());
    }

    private static readonly VentInput _input = new("Dana Ruiz said CI passed (GC seq 4).", "Lint first.", "Is it flaky?");
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);

    [Fact]
    public async Task Vent_is_sanitized_and_idempotent_per_failure()
    {
        using var sp = Host(new FakeTimeProvider());
        var vents = sp.GetRequiredService<IVentService>();

        var first = await vents.WriteAsync("f1", _a, _input, new VentTaint(false, false), _terms, CancellationToken.None);
        var second = await vents.WriteAsync("f1", _a, new VentInput("other", "other", "other"), new VentTaint(false, false), _terms, CancellationToken.None);

        Assert.DoesNotContain("Dana Ruiz", first.WhatHappened);
        Assert.Equal(VentStatus.Draft, first.Status);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Tainted_vents_are_held_and_show_in_the_owner_overview()
    {
        using var sp = Host(new FakeTimeProvider());
        var vents = sp.GetRequiredService<IVentService>();

        await vents.WriteAsync("hard", _a, _input, new VentTaint(true, false), _terms, CancellationToken.None);
        await vents.WriteAsync("soft-clean", _a, _input, new VentTaint(false, false), _terms, CancellationToken.None);
        await vents.WriteAsync("soft-tainted", _b, _input, new VentTaint(false, true), _terms, CancellationToken.None);

        var held = (await sp.GetRequiredService<IPsychWardService>().GetOverviewAsync(CancellationToken.None)).HeldVents;
        Assert.Equal(["hard", "soft-tainted"], held.Select(v => v.FailureRef).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_open_failure_blocks_the_next_card_until_the_vent_is_written()
    {
        using var sp = Host(new FakeTimeProvider());
        var vents = sp.GetRequiredService<IVentService>();

        await vents.RecordFailureAsync(_a, "f1", CancellationToken.None);
        Assert.False(await vents.MayClaimCardAsync(_a, CancellationToken.None));
        Assert.True(await vents.MayClaimCardAsync(_b, CancellationToken.None));

        await vents.WriteAsync("f1", _a, _input, new VentTaint(false, false), _terms, CancellationToken.None);
        Assert.True(await vents.MayClaimCardAsync(_a, CancellationToken.None));
    }

    [Fact]
    public async Task The_card_gate_is_off_when_the_setting_is_off()
    {
        using var sp = Host(new FakeTimeProvider(), new PsychWardSettings { VentRequiredBeforeCard = false });
        var vents = sp.GetRequiredService<IVentService>();

        await vents.RecordFailureAsync(_a, "f1", CancellationToken.None);

        Assert.True(await vents.MayClaimCardAsync(_a, CancellationToken.None));
    }

    [Fact]
    public async Task Publishing_needs_owner_approval_and_a_configured_target()
    {
        var publisher = new RecordingPublisher();
        using var sp = Host(new FakeTimeProvider(), new PsychWardSettings { VentOperatorTargets = new HashSet<string> { "blog" } }, publisher);
        var vents = sp.GetRequiredService<IVentService>();
        await vents.WriteAsync("f1", _a, _input, new VentTaint(true, false), _terms, CancellationToken.None);

        Assert.False((await vents.PublishAsync("f1", PsychWardSettings.InternalGeneralGc, false, CancellationToken.None)).Published);
        Assert.False((await vents.PublishAsync("f1", "elsewhere", true, CancellationToken.None)).Published);
        Assert.False((await vents.PublishAsync("nope", PsychWardSettings.InternalGeneralGc, true, CancellationToken.None)).Published);
        Assert.Empty(publisher.Posted);

        var ok = await vents.PublishAsync("f1", "blog", true, CancellationToken.None);

        Assert.True(ok.Published);
        Assert.Equal(VentStatus.Published, ok.Draft!.Status);
        Assert.Single(publisher.Posted);
        Assert.False((await vents.PublishAsync("f1", "blog", true, CancellationToken.None)).Published);
    }

    [Fact]
    public async Task Publishing_without_a_publisher_is_refused_not_faked()
    {
        using var sp = Host(new FakeTimeProvider());
        var vents = sp.GetRequiredService<IVentService>();
        await vents.WriteAsync("f1", _a, _input, new VentTaint(false, false), _terms, CancellationToken.None);

        var result = await vents.PublishAsync("f1", PsychWardSettings.InternalGeneralGc, true, CancellationToken.None);

        Assert.False(result.Published);
        Assert.Equal(VentStatus.Draft, result.Draft!.Status);
    }
}

public class WardCurfewTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-00000000000a");

    private sealed class FixedProbe(WardCurfewStatus status) : IWardAvailabilityProbe
    {
        public Task<WardCurfewStatus> GetCurfewAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(status);
    }

    private static PsychWardService Service(FakeTimeProvider time, WardCurfewStatus status, WardCurfewBehaviour behaviour) =>
        new(new InMemoryWardStayStore(), new InMemoryVentStore(), new PsychWardSettings { CurfewBehaviour = behaviour, MinStayMinutes = 60 }, time, availability: new FixedProbe(status));

    [Fact]
    public async Task A_curfew_admission_counts_from_the_next_shift_but_restricts_at_once()
    {
        var time = new FakeTimeProvider();
        var next = time.GetUtcNow().AddHours(6);
        using var ward = Service(time, new WardCurfewStatus(true, next), WardCurfewBehaviour.QueueNextShift);

        var outcome = await ward.AdmitAsync(_agent, WardTrigger.LoopGuard, null, CancellationToken.None);

        Assert.Equal(next, outcome.Stay!.AdmittedAt);
        Assert.Equal(WardRestrictions.Admitted, await ward.GetRestrictionsAsync(_agent, CancellationToken.None));

        time.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(30)));
        Assert.False((await ward.DischargeAsync(_agent, new DischargeRequest(true), CancellationToken.None)).Accepted);
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.True((await ward.DischargeAsync(_agent, new DischargeRequest(true), CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task Admit_now_and_owner_referrals_ignore_the_curfew()
    {
        var time = new FakeTimeProvider();
        var status = new WardCurfewStatus(true, time.GetUtcNow().AddHours(6));

        using var now = Service(time, status, WardCurfewBehaviour.AdmitNow);
        Assert.Equal(time.GetUtcNow(), (await now.AdmitAsync(_agent, WardTrigger.LoopGuard, null, CancellationToken.None)).Stay!.AdmittedAt);

        using var queued = Service(time, status, WardCurfewBehaviour.QueueNextShift);
        Assert.Equal(time.GetUtcNow(), (await queued.AdmitAsync(_agent, WardTrigger.OwnerReferral, null, CancellationToken.None)).Stay!.AdmittedAt);
    }

    [Fact]
    public async Task Outside_curfew_or_with_an_unknown_next_shift_the_stay_begins_now()
    {
        var time = new FakeTimeProvider();

        using var outside = Service(time, new WardCurfewStatus(false, time.GetUtcNow().AddHours(6)), WardCurfewBehaviour.QueueNextShift);
        Assert.Equal(time.GetUtcNow(), (await outside.AdmitAsync(_agent, WardTrigger.LoopGuard, null, CancellationToken.None)).Stay!.AdmittedAt);

        using var unknown = Service(time, new WardCurfewStatus(true, null), WardCurfewBehaviour.QueueNextShift);
        Assert.Equal(time.GetUtcNow(), (await unknown.AdmitAsync(_agent, WardTrigger.LoopGuard, null, CancellationToken.None)).Stay!.AdmittedAt);
    }
}
