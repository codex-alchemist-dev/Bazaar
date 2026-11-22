using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.LoopGuard.Abstractions;
using Bazaar.Sanitization;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.PsychWard.Tests;

public class GcWardJournalTests
{
    private static readonly GcPrincipal _owner = new(Guid.Parse("00000000-0000-7000-8000-0000000000f0"), PrincipalKind.Owner, "Boss");
    private static readonly GcPrincipal _agent = new(Guid.Parse("00000000-0000-7000-8000-00000000000a"), PrincipalKind.Agent, "Charles");

    private sealed class FakeProvisioner : IGcProvisioner
    {
        public Dictionary<Guid, (string Type, string Title, IReadOnlyDictionary<string, string>? Meta, List<Guid> Members)> Gcs { get; } = [];

        public Task<bool> CreateProjectGcAsync(Guid gcId, string title, GcPrincipal owner, CancellationToken ct) =>
            CreateAsync(gcId, "project", title, owner, null, ct);

        public Task<bool> CreateAsync(Guid gcId, string typeKey, string title, GcPrincipal owner, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
        {
            if (Gcs.ContainsKey(gcId))
            {
                return Task.FromResult(false);
            }

            Gcs[gcId] = (typeKey, title, metadata, [owner.Id]);
            return Task.FromResult(true);
        }

        public Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);

        public Task<bool> AddMemberAsync(Guid gcId, GcPrincipal member, CancellationToken ct)
        {
            var members = Gcs[gcId].Members;
            if (members.Contains(member.Id))
            {
                return Task.FromResult(false);
            }

            members.Add(member.Id);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeWriter : IGcLogWriter
    {
        public List<AppendRequest> Appended { get; } = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            var duplicate = Appended.Any(a => a.GcId == request.GcId && a.ClientMsgId == request.ClientMsgId);
            if (!duplicate)
            {
                Appended.Add(request);
            }

            return Task.FromResult(new AppendResult(request.GcId, Appended.Count, Guid.NewGuid(), duplicate, DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDirectory : IWardPrincipalDirectory
    {
        public Task<GcPrincipal?> GetOwnerAsync(CancellationToken ct) => Task.FromResult<GcPrincipal?>(_owner);

        public Task<GcPrincipal?> GetAgentAsync(Guid agentId, CancellationToken ct) =>
            Task.FromResult(agentId == _agent.Id ? _agent : null);
    }

    private sealed class PassThroughSanitizer : ITextSanitizer
    {
        public SanitizedText Sanitize(string text, SensitiveTerms terms) => new(text, []);
    }

    private static (ServiceProvider Sp, FakeProvisioner Gcs, FakeWriter Log) Host(FakeTimeProvider time)
    {
        var gcs = new FakeProvisioner();
        var log = new FakeWriter();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ITextSanitizer, PassThroughSanitizer>();
        services.AddSingleton<IGcProvisioner>(gcs);
        services.AddSingleton<IGcLogWriter>(log);
        services.AddSingleton<IWardPrincipalDirectory, FakeDirectory>();
        services.AddBazaarPsychWard();
        return (services.BuildServiceProvider(), gcs, log);
    }

    private static string Action(AppendRequest r) => JsonNode.Parse(r.PayloadJson!)!["action"]!.GetValue<string>();

    [Fact]
    public async Task A_loop_guard_trip_opens_a_ward_gc_with_the_owner_and_agent_and_notes_the_admission()
    {
        var (sp, gcs, log) = Host(new FakeTimeProvider());
        using var _ = sp;

        await sp.GetRequiredService<IPsychWardGate>()
            .AdmitAsync(new LoopTrip(Guid.NewGuid(), "ping_pong", "bounces", [_agent.Id]), CancellationToken.None);

        var (gcId, gc) = Assert.Single(gcs.Gcs);
        Assert.Equal("ward", gc.Type);
        Assert.Equal("Psych Ward: Charles", gc.Title);
        Assert.Equal([_owner.Id, _agent.Id], gc.Members);
        var entry = Assert.Single(log.Appended);
        Assert.Equal(gcId, entry.GcId);
        Assert.Equal(GcEventKinds.System, entry.Kind);
        Assert.Equal("ward_admitted", Action(entry));
        var payload = JsonNode.Parse(entry.PayloadJson!)!;
        Assert.Equal("loop_guard", payload["trigger"]!.GetValue<string>());
        Assert.Equal("ping_pong: bounces", payload["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Check_in_and_discharge_land_in_the_same_ward_gc_and_the_check_in_is_marked_agent_trust()
    {
        var time = new FakeTimeProvider();
        var (sp, gcs, log) = Host(time);
        using var _ = sp;
        var ward = sp.GetRequiredService<IPsychWardService>();

        await ward.AdmitAsync(_agent.Id, WardTrigger.SelfReferral, null, CancellationToken.None);
        await ward.CheckInAsync(_agent.Id, new WardCheckIn("tried x", "y fails", "help", "the build"), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(61));
        await ward.DischargeAsync(_agent.Id, new DischargeRequest(true), CancellationToken.None);

        Assert.Single(gcs.Gcs);
        Assert.Equal(["ward_admitted", "checkin", "ward_discharged"], log.Appended.Select(Action));
        Assert.Single(log.Appended.Select(a => a.GcId).Distinct());
        var checkIn = JsonNode.Parse(log.Appended[1].PayloadJson!)!;
        Assert.Equal("agent", checkIn["source_trust"]!.GetValue<string>());
        Assert.Equal("tried x", checkIn["attempted"]!.GetValue<string>());
    }

    [Fact]
    public async Task Vent_entries_go_to_one_private_vent_gc_per_agent_and_carry_no_text()
    {
        var (sp, gcs, log) = Host(new FakeTimeProvider());
        using var _ = sp;
        var vents = sp.GetRequiredService<IVentService>();

        await vents.WriteAsync("f1", _agent.Id, new VentInput("secret-ish words", "change", "unsure"), new VentTaint(true, false), SensitiveTerms.Empty, CancellationToken.None);
        await vents.WriteAsync("f2", _agent.Id, new VentInput("more", "change", "unsure"), new VentTaint(false, false), SensitiveTerms.Empty, CancellationToken.None);

        var (_, gc) = Assert.Single(gcs.Gcs);
        Assert.Equal("vent", gc.Type);
        Assert.Equal(2, log.Appended.Count);
        Assert.All(log.Appended, e =>
        {
            Assert.Equal("vent_written", Action(e));
            Assert.DoesNotContain("secret-ish", e.PayloadJson);
        });
        Assert.Equal("held", JsonNode.Parse(log.Appended[0].PayloadJson!)!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_journal_is_idempotent_so_repeating_a_write_adds_nothing()
    {
        var (sp, gcs, log) = Host(new FakeTimeProvider());
        using var _ = sp;
        var journal = sp.GetRequiredService<IWardJournal>();
        var stay = new WardStay(Guid.NewGuid(), _agent.Id, WardTrigger.LoopGuard, null, DateTimeOffset.UnixEpoch);

        await journal.AdmittedAsync(stay, CancellationToken.None);
        await journal.AdmittedAsync(stay, CancellationToken.None);

        Assert.Single(gcs.Gcs);
        Assert.Single(log.Appended);
    }

    [Fact]
    public async Task A_vent_published_entry_names_the_target()
    {
        var (sp, _, log) = Host(new FakeTimeProvider());
        using var _ = sp;
        var journal = sp.GetRequiredService<IWardJournal>();

        await journal.VentPublishedAsync(
            new VentDraft("f1", _agent.Id, "a", "b", "c", VentStatus.Published, DateTimeOffset.UnixEpoch, "internal_general_gc"),
            CancellationToken.None);

        Assert.Equal("vent_published", Action(Assert.Single(log.Appended)));
        Assert.Equal("internal_general_gc", JsonNode.Parse(log.Appended[0].PayloadJson!)!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_gc_log_the_module_still_runs_with_a_no_op_journal()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITextSanitizer, PassThroughSanitizer>();
        services.AddBazaarPsychWard();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<NoOpWardJournal>(sp.GetRequiredService<IWardJournal>());
        Assert.True((await sp.GetRequiredService<IPsychWardService>().AdmitAsync(_agent.Id, WardTrigger.LoopGuard, null, CancellationToken.None)).Accepted);
    }

    private sealed class NoOwnerDirectory : IWardPrincipalDirectory
    {
        public Task<GcPrincipal?> GetOwnerAsync(CancellationToken ct) => Task.FromResult<GcPrincipal?>(null);

        public Task<GcPrincipal?> GetAgentAsync(Guid agentId, CancellationToken ct) => Task.FromResult<GcPrincipal?>(_agent);
    }

    [Fact]
    public async Task With_no_owner_yet_no_gc_is_made_and_the_stay_still_holds()
    {
        var gcs = new FakeProvisioner();
        var log = new FakeWriter();
        var services = new ServiceCollection();
        services.AddSingleton<IGcProvisioner>(gcs);
        services.AddSingleton<IGcLogWriter>(log);
        services.AddSingleton<IWardPrincipalDirectory, NoOwnerDirectory>();
        services.AddBazaarPsychWard();
        using var sp = services.BuildServiceProvider();

        var ward = sp.GetRequiredService<IPsychWardService>();
        await ward.AdmitAsync(_agent.Id, WardTrigger.LoopGuard, null, CancellationToken.None);

        Assert.Empty(gcs.Gcs);
        Assert.Empty(log.Appended);
        Assert.Equal(WardRestrictions.Admitted, await ward.GetRestrictionsAsync(_agent.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_agent_surfaces_to_the_caller_after_the_stay_is_saved()
    {
        var (sp, _, _) = Host(new FakeTimeProvider());
        using var _ = sp;
        var ward = sp.GetRequiredService<IPsychWardService>();
        var stranger = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ward.AdmitAsync(stranger, WardTrigger.LoopGuard, null, CancellationToken.None));

        Assert.Equal(WardRestrictions.Admitted, await ward.GetRestrictionsAsync(stranger, CancellationToken.None));
    }
}
