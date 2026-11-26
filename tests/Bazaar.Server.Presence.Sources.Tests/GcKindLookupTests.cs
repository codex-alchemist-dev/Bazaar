using Bazaar.Server.GcMetadata;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Sources.Tests;

/// <summary>The GC kind lookup maps a GC header to the kind and project presence needs (docs/design/23), and never fails the turn.</summary>
public class GcKindLookupTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000c3");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSource(Func<GcHeader?> answer) : IGcHeaderSource
    {
        public int Calls { get; private set; }

        public Task<GcHeader?> FindAsync(Guid gcId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private sealed class FakeProjector : IActivityProjector
    {
        public List<ActivityEvent> Events { get; } = [];

        public ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct)
        {
            Events.Add(e);
            return ValueTask.CompletedTask;
        }
    }

    private static GcHeader Header(string type, params (string Key, string Value)[] metadata) =>
        new(type, metadata.ToDictionary(m => m.Key, m => m.Value));

    private static GcKindLookup Lookup(IGcHeaderSource? source, TimeProvider? time = null) =>
        new(source, time ?? new FakeTimeProvider(), TimeSpan.FromSeconds(60));

    [Theory]
    [InlineData("project", null, TurnGcKind.Project, null)]
    [InlineData("project", "p1", TurnGcKind.Project, "p1")]
    [InlineData("pr", "p1", TurnGcKind.Project, "p1")]
    [InlineData("general", null, TurnGcKind.Other, null)]
    [InlineData("dm", "p1", TurnGcKind.Other, null)]
    [InlineData("thinking", "p1", TurnGcKind.Other, null)]
    [InlineData("council", null, TurnGcKind.Council, null)]
    [InlineData("vote", null, TurnGcKind.Vote, null)]
    public void A_header_maps_to_kind_and_project(string type, string? project, TurnGcKind kind, string? expectedProject)
    {
        var info = GcKindLookup.Map(project is null ? Header(type) : Header(type, (GcMetadataKeys.Project, project)));

        Assert.Equal(new GcKindInfo(kind, expectedProject), info);
    }

    [Theory]
    [InlineData("council", TurnGcKind.Council)]
    [InlineData("vote", TurnGcKind.Vote)]
    public void A_kind_slug_in_metadata_names_council_or_vote(string slug, TurnGcKind kind) =>
        Assert.Equal(kind, GcKindLookup.Map(Header("general", (GcMetadataKeys.Kind, slug), (GcMetadataKeys.Project, "p1"))).Kind);

    [Fact]
    public async Task A_source_failure_reads_as_other()
    {
        var info = await Lookup(new FakeSource(() => throw new InvalidOperationException("db down"))).LookupAsync(_gc, Ct);

        Assert.Equal(GcKindInfo.Other, info);
    }

    [Fact]
    public async Task An_unknown_gc_and_a_missing_source_read_as_other()
    {
        Assert.Equal(GcKindInfo.Other, await Lookup(new FakeSource(() => null)).LookupAsync(_gc, Ct));
        Assert.Equal(GcKindInfo.Other, await Lookup(null).LookupAsync(_gc, Ct));
    }

    [Fact]
    public async Task Answers_are_cached_until_the_ttl_passes()
    {
        var clock = new FakeTimeProvider();
        var source = new FakeSource(() => Header("project", (GcMetadataKeys.Project, "p1")));
        var lookup = Lookup(source, clock);

        await lookup.LookupAsync(_gc, Ct);
        await lookup.LookupAsync(_gc, Ct);
        Assert.Equal(1, source.Calls);

        clock.Advance(TimeSpan.FromSeconds(61));
        await lookup.LookupAsync(_gc, Ct);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData("p2", "p1", PresenceGcContext.OtherProjectGc)]
    [InlineData("p1", "p1", PresenceGcContext.ProjectGc)]
    public async Task The_looked_up_kind_flows_through_the_real_adapter(string gcProject, string own, PresenceGcContext expected)
    {
        var info = await Lookup(new FakeSource(() => Header("project", (GcMetadataKeys.Project, gcProject)))).LookupAsync(_gc, Ct);
        var projector = new FakeProjector();
        var activity = new TurnActivity(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, TurnActivityKind.ModelCallStarted, Guid.NewGuid(), _gc, info.Kind, info.ProjectId, own);

        await new TurnPresenceSource(projector, new NameTableToolClassifier(new PresenceSourcesOptions())).OnTurnActivityAsync(activity, Ct);

        Assert.Equal(expected, Assert.Single(projector.Events).GcContext);
    }
}
