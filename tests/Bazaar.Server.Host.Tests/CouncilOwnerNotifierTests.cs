using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.CouncilAdapters;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The Council owner notifier adapter over the owner approvals queue (doc 10 Escalation).</summary>
public sealed class CouncilOwnerNotifierTests
{
    private static readonly Guid _voteId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset _escalated = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeEnqueuer : IOwnerApprovalEnqueuer
    {
        public Dictionary<string, (Guid Id, OwnerApprovalEnqueueRequest Request)> Rows { get; } = [];

        public Task<Guid> EnqueueAsync(OwnerApprovalEnqueueRequest request, CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(request.ActionKey, out var row))
            {
                row = (Guid.NewGuid(), request);
                Rows[request.ActionKey] = row;
            }

            return Task.FromResult(row.Id);
        }
    }

    private sealed class FixedHours(int hours) : ICouncilEscalationRenotifySource
    {
        public Task<int> GetRenotifyHoursAsync(CancellationToken ct) => Task.FromResult(hours);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static EscalatedVote Vote() => new(
        _voteId, Guid.NewGuid(), "secret-subject", default, default, [], 3, 2, true, [], "escalated: tie", _escalated, _escalated.AddDays(1));

    [Fact]
    public async Task First_alert_files_one_card_and_a_repeat_files_no_second()
    {
        var fake = new FakeEnqueuer();
        var notifier = new OwnerQueueCouncilOwnerNotifier(fake, new FixedClock(_escalated.AddMinutes(10)), new CouncilEscalationOptions());

        await notifier.NotifyEscalationAsync(Vote(), CancellationToken.None);
        await notifier.NotifyEscalationAsync(Vote(), CancellationToken.None);

        var row = Assert.Single(fake.Rows).Value.Request;
        Assert.Equal(OwnerQueueCouncilOwnerNotifier.ToolName, row.Tool);
        Assert.Equal(OwnerQueueCouncilOwnerNotifier.AgentId, row.AgentId);
        Assert.Equal($"council-escalation:{_voteId:N}:r0", row.ActionKey);
        Assert.DoesNotContain("secret-subject", row.Summary, StringComparison.Ordinal);
        Assert.True(row.Summary.Length <= OwnerApprovalEnqueueRequest.MaxSummaryLength);
    }

    [Fact]
    public async Task Each_renotify_round_files_a_fresh_card_using_the_live_interval()
    {
        var fake = new FakeEnqueuer();
        var clock = new FixedClock(_escalated.AddHours(9));
        var notifier = new OwnerQueueCouncilOwnerNotifier(fake, clock, new CouncilEscalationOptions { RenotifyHours = 4 }, new FixedHours(2));

        await notifier.NotifyEscalationAsync(Vote(), CancellationToken.None);

        Assert.Contains($"council-escalation:{_voteId:N}:r4", fake.Rows.Keys);
        Assert.Equal(0, OwnerQueueCouncilOwnerNotifier.Round(_escalated, _escalated.AddHours(3), 4));
        Assert.Equal(1, OwnerQueueCouncilOwnerNotifier.Round(_escalated, _escalated.AddHours(4), 4));
        Assert.Equal(0, OwnerQueueCouncilOwnerNotifier.Round(_escalated, _escalated.AddHours(-1), 4));
    }

    [Fact]
    public void Host_adapters_register_the_notifier_and_a_host_registration_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOwnerApprovalEnqueuer>(new FakeEnqueuer());
        _ = services.AddBazaarCouncilHostAdapters();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<OwnerQueueCouncilOwnerNotifier>(sp.GetRequiredService<ICouncilOwnerNotifier>());
    }
}
