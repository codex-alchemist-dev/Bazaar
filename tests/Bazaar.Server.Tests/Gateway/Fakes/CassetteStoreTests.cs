using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Fakes;

namespace Bazaar.Server.Tests.Gateway.Fakes;

public class CassetteStoreTests
{
    private const string _provider = "fake-provider";
    private const string _model = "claude-haiku-5-5";

    private static readonly IReadOnlyList<ModelEvent> _recorded =
    [
        new ModelEvent.TextDelta("Hel"),
        new ModelEvent.TextDelta("lo"),
        new ModelEvent.Done(StopReason.EndTurn, _model),
    ];

    private static ModelRequest Request(
        string system = "You are Charles.",
        string user = "hello",
        string toolSchema = "{\"type\":\"object\"}",
        double? temperature = null,
        string agentId = "agent-1") =>
        new(
            agentId,
            "custom/charles",
            [new ModelMessage(ModelRole.System, system), new ModelMessage(ModelRole.User, user)],
            [new ModelToolDefinition("search", "Search the vault.", toolSchema)],
            Temperature: temperature);

    private static CassetteStore StoreWithOneEntry()
    {
        var store = new CassetteStore();
        store.Record(CassetteKey.For(_provider, _model, Request()), _recorded);
        return store;
    }

    [Fact]
    public void Replay_returns_the_recorded_events_for_a_matching_request()
    {
        var store = StoreWithOneEntry();

        var events = store.Replay(_provider, _model, Request());

        Assert.Equal(_recorded, events);
    }

    [Fact]
    public void Replay_ignores_agent_id_and_route_which_are_not_part_of_the_key()
    {
        var store = StoreWithOneEntry();

        var events = store.Replay(_provider, _model, Request(agentId: "another-agent"));

        Assert.Equal(_recorded, events);
    }

    [Fact]
    public void Replay_of_an_unmatched_request_throws_with_the_request_digest()
    {
        var store = StoreWithOneEntry();
        var unmatched = Request(user: "a different question");
        var expectedDigest = CassetteKey.For(_provider, _model, unmatched).Digest();

        var error = Assert.Throws<CassetteMismatchException>(() => store.Replay(_provider, _model, unmatched));

        Assert.Equal(expectedDigest, error.RequestDigest);
        Assert.Contains(expectedDigest, error.Message);
    }

    [Fact]
    public void Replay_mismatch_names_only_the_fields_that_differ_from_the_nearest_entry()
    {
        var store = StoreWithOneEntry();

        var error = Assert.Throws<CassetteMismatchException>(() =>
            store.Replay(_provider, _model, Request(user: "a different question")));

        Assert.Equal(["messages_digest"], error.Differences);
        Assert.NotNull(error.NearestDigest);
    }

    [Fact]
    public void Replay_mismatch_reports_a_changed_system_prompt_and_a_changed_tool_schema()
    {
        var store = StoreWithOneEntry();

        var system = Assert.Throws<CassetteMismatchException>(() =>
            store.Replay(_provider, _model, Request(system: "You are Charles, v2.")));
        var tools = Assert.Throws<CassetteMismatchException>(() =>
            store.Replay(_provider, _model, Request(toolSchema: "{\"type\":\"array\"}")));

        Assert.Equal(["system_hash"], system.Differences);
        Assert.Equal(["tools_digest"], tools.Differences);
    }

    [Fact]
    public void Replay_mismatch_reports_a_changed_sampling_parameter()
    {
        var store = StoreWithOneEntry();

        var error = Assert.Throws<CassetteMismatchException>(() =>
            store.Replay(_provider, _model, Request(temperature: 0.2)));

        Assert.Equal(["sampling_digest"], error.Differences);
    }

    [Fact]
    public void Replay_against_an_empty_store_throws_and_does_not_return_a_default()
    {
        var store = new CassetteStore();

        var error = Assert.Throws<CassetteMismatchException>(() => store.Replay(_provider, _model, Request()));

        Assert.Null(error.NearestDigest);
        Assert.Equal(CassetteKey.FieldNames, error.Differences);
    }

    [Fact]
    public void Replay_for_another_model_never_falls_through_to_the_recorded_model()
    {
        var store = StoreWithOneEntry();

        var error = Assert.Throws<CassetteMismatchException>(() => store.Replay(_provider, "claude-sonnet-5-5", Request()));

        Assert.Contains("model", error.Differences);
    }

    [Fact]
    public void Record_refuses_a_second_entry_with_the_same_digest()
    {
        var store = StoreWithOneEntry();

        Assert.Throws<InvalidOperationException>(() =>
            store.Record(CassetteKey.For(_provider, _model, Request()), [new ModelEvent.TextDelta("other")]));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Key_digest_is_stable_for_the_same_request()
    {
        var first = CassetteKey.For(_provider, _model, Request());
        var second = CassetteKey.For(_provider, _model, Request());

        Assert.Equal(first, second);
        Assert.Equal(first.Digest(), second.Digest());
        Assert.Empty(first.Differences(second));
    }

    [Fact]
    public void Key_digest_changes_when_one_character_of_the_user_message_changes()
    {
        var original = CassetteKey.For(_provider, _model, Request(user: "hello"));
        var changed = CassetteKey.For(_provider, _model, Request(user: "hellO"));

        Assert.NotEqual(original.Digest(), changed.Digest());
        Assert.Equal(["messages_digest"], original.Differences(changed));
    }

    [Fact]
    public void Key_rejects_blank_provider_or_model()
    {
        Assert.Throws<ArgumentException>(() => CassetteKey.For(" ", _model, Request()));
        Assert.Throws<ArgumentException>(() => CassetteKey.For(_provider, "", Request()));
    }

    [Fact]
    public void Json_lines_round_trip_keeps_every_entry_and_its_events()
    {
        var store = StoreWithOneEntry();
        store.Record(CassetteKey.For(_provider, _model, Request(user: "second")), [new ModelEvent.Failure("rate_limited", "429", 7)]);

        var text = store.ToJsonLines();
        var loaded = CassetteStore.FromJsonLines(text);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(_recorded, loaded.Replay(_provider, _model, Request()));
        Assert.Equal(
            [new ModelEvent.Failure("rate_limited", "429", 7)],
            loaded.Replay(_provider, _model, Request(user: "second")));
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void A_changed_key_byte_in_a_stored_record_fails_the_load_loudly()
    {
        var text = StoreWithOneEntry().ToJsonLines();
        var tampered = text.Replace(_model, "claude-haiku-5-6", StringComparison.Ordinal);

        Assert.NotEqual(text, tampered);
        var error = Assert.Throws<CassetteTamperException>(() => CassetteStore.FromJsonLines(tampered));
        Assert.Equal(1, error.LineNumber);
    }

    [Fact]
    public void A_changed_digest_byte_in_a_stored_record_fails_the_load_loudly()
    {
        var store = StoreWithOneEntry();
        var digest = store.Entries[0].Digest;
        var flipped = (digest[0] == 'a' ? "b" : "a") + digest[1..];
        var tampered = store.ToJsonLines().Replace(digest, flipped, StringComparison.Ordinal);

        Assert.Throws<CassetteTamperException>(() => CassetteStore.FromJsonLines(tampered));
    }

    [Fact]
    public void A_line_that_is_not_a_record_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CassetteStore.FromJsonLines("{not json"));
        Assert.Throws<InvalidDataException>(() => CassetteStore.FromJsonLines("{\"digest\":\"x\"}"));
    }

    [Fact]
    public async Task Cassette_gateway_replays_the_recorded_events_through_the_gateway_seam()
    {
        var gateway = new CassetteGateway(StoreWithOneEntry(), _provider, _model);

        var events = new List<ModelEvent>();
        await foreach (var e in gateway.CallAsync(Request(), TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Assert.Equal(_recorded, events);
    }

    [Fact]
    public void Cassette_gateway_throws_at_the_call_for_an_unmatched_request()
    {
        var gateway = new CassetteGateway(StoreWithOneEntry(), _provider, _model);

        // Fail-loud at the call, before any enumeration: nothing can be consumed as a default.
        Assert.Throws<CassetteMismatchException>(() =>
            gateway.CallAsync(Request(user: "unrecorded"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Cassette_gateway_requires_a_provider_and_a_model()
    {
        var store = new CassetteStore();

        Assert.Throws<ArgumentException>(() => new CassetteGateway(store, "", _model));
        Assert.Throws<ArgumentException>(() => new CassetteGateway(store, _provider, " "));
    }
}
