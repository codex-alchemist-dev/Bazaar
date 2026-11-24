using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Wire;

/// <summary>The <c>gc.list</c> handler over a fake membership source (no database).</summary>
public sealed class GcListHandlerTests
{
    private static readonly JsonWireCodec _codec = new();
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-20T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Guid _me = Guid.CreateVersion7();

    private static GcMemberListing Listing(string kind, int minutesAgo, bool archived = false, string? title = null) =>
        new(Guid.CreateVersion7(), title ?? kind, kind, 10, archived, 2, _now.AddMinutes(-minutesAgo));

    private static async Task<WireReply> ListAsync(
        IReadOnlyList<GcMemberListing> rows, PrincipalRef who, GcListRequest request, GcDefaultRule rule = GcDefaultRule.MostRecentlyActive)
    {
        var handler = new GcListHandler(new FakeSource(rows), new RuleStore(rule));
        var connection = new FakeConnection(who);
        var context = new WireRequestContext(connection, new Envelope(1, WireTypes.GcList, EnvelopeKind.Request, "r1", null, null, null, null,
            _codec.EncodePayload(request)));
        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
    }

    private static GcListReply Decode(WireReply reply)
    {
        Assert.True(reply.IsOk, reply.Error?.Code);
        return _codec.DecodePayload<GcListReply>(new Envelope(1, "r", EnvelopeKind.Response, null, null, null, null, null, reply.Payload));
    }

    [Fact]
    public async Task Lists_the_callers_memberships_most_recent_first_and_picks_the_newest_ordinary_gc_as_default()
    {
        var old = Listing("general", 60);
        var fresh = Listing("project", 5);
        var reply = Decode(await ListAsync([old, fresh], new PrincipalRef(_me, "human"), new GcListRequest(50)));

        Assert.Equal([fresh.GcId, old.GcId], reply.Items.Select(i => i.GcId));
        Assert.Equal("project", reply.Items[0].Kind);
        Assert.Equal(fresh.GcId, reply.DefaultGcId);
        Assert.False(reply.HasMore);
    }

    [Fact]
    public async Task A_caller_with_no_memberships_gets_an_empty_list_and_no_default()
    {
        var reply = Decode(await ListAsync([], new PrincipalRef(_me, "human"), new GcListRequest(50)));

        Assert.Empty(reply.Items);
        Assert.Null(reply.DefaultGcId);
    }

    [Fact]
    public async Task Archived_rows_are_never_listed_or_chosen_as_default()
    {
        var archived = Listing("general", 1, archived: true);
        var live = Listing("general", 30);
        var reply = Decode(await ListAsync([archived, live], new PrincipalRef(_me, "human"), new GcListRequest(50)));

        Assert.Equal([live.GcId], reply.Items.Select(i => i.GcId));
        Assert.Equal(live.GcId, reply.DefaultGcId);
    }

    [Fact]
    public async Task A_dm_is_listed_to_its_member_but_is_never_the_default()
    {
        var dm = Listing("dm", 1);
        var general = Listing("general", 90);
        var reply = Decode(await ListAsync([dm, general], new PrincipalRef(_me, "human"), new GcListRequest(50)));

        Assert.Equal([dm.GcId, general.GcId], reply.Items.Select(i => i.GcId));
        Assert.Equal(general.GcId, reply.DefaultGcId);
    }

    [Fact]
    public async Task The_owner_gets_only_their_own_memberships_too()
    {
        var mine = Listing("general", 3);
        var reply = Decode(await ListAsync([mine], new PrincipalRef(_me, "owner"), new GcListRequest(50)));

        Assert.Equal([mine.GcId], reply.Items.Select(i => i.GcId));
    }

    [Fact]
    public async Task Thinking_gcs_are_left_out_unless_asked_for_and_are_never_the_default()
    {
        var thinking = Listing("thinking", 1);
        var general = Listing("general", 50);
        var who = new PrincipalRef(_me, "agent");

        var without = Decode(await ListAsync([thinking, general], who, new GcListRequest(50)));
        Assert.Equal([general.GcId], without.Items.Select(i => i.GcId));

        var with = Decode(await ListAsync([thinking, general], who, new GcListRequest(50, IncludeThinking: true)));
        Assert.Equal([thinking.GcId, general.GcId], with.Items.Select(i => i.GcId));
        Assert.Equal("thinking", with.Items[0].Kind);
        Assert.Equal(general.GcId, with.DefaultGcId);
    }

    [Fact]
    public async Task Only_private_kinds_means_no_default()
    {
        var reply = Decode(await ListAsync([Listing("dm", 1), Listing("ward", 2)], new PrincipalRef(_me, "human"), new GcListRequest(50)));

        Assert.Equal(2, reply.Items.Count);
        Assert.Null(reply.DefaultGcId);
    }

    [Fact]
    public async Task The_none_rule_returns_no_default()
    {
        var reply = Decode(await ListAsync([Listing("general", 1)], new PrincipalRef(_me, "human"), new GcListRequest(50), GcDefaultRule.None));

        Assert.Single(reply.Items);
        Assert.Null(reply.DefaultGcId);
    }

    [Fact]
    public async Task The_limit_caps_the_list_and_sets_has_more_but_the_default_still_sees_every_membership()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Listing(i == 4 ? "general" : "dm", i)).ToList();
        var reply = Decode(await ListAsync(rows, new PrincipalRef(_me, "human"), new GcListRequest(2)));

        Assert.Equal(2, reply.Items.Count);
        Assert.True(reply.HasMore);
        Assert.Equal(rows[4].GcId, reply.DefaultGcId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task A_limit_outside_one_to_two_hundred_is_a_bad_request(int limit)
    {
        var reply = await ListAsync([Listing("general", 1)], new PrincipalRef(_me, "human"), new GcListRequest(limit));

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public void The_default_rule_setting_defaults_to_most_recently_active_and_is_registered()
    {
        Assert.Equal("gc.default_gc_rule", GcSettings.DefaultGcRuleKey.Name);
        Assert.Equal(GcDefaultRule.MostRecentlyActive, GcSettings.DefaultGcRuleKey.Default);
        Assert.Contains(GcSettings.DefaultGcRuleKey, GcSettings.All);
    }

    private sealed class FakeSource(IReadOnlyList<GcMemberListing> rows) : IGcMemberListSource
    {
        public Task<IReadOnlyList<GcMemberListing>> ListAsync(Guid principalId, bool includeArchived, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GcMemberListing>>(rows.Where(r => includeArchived || !r.Archived).ToList());
    }

    private sealed class RuleStore(GcDefaultRule rule) : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key.Name == GcSettings.DefaultGcRuleKey.Name ? (T)(object)rule : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal => principal;

        public string DeviceId => "test-device";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public ISubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
