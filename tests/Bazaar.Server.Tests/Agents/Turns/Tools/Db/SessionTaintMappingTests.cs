using System.Collections.Immutable;
using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Agents.Turns.Tools.Db;

/// <summary>The pure row mapping behind <c>PostgresSessionTaintStore</c>: no database needed.</summary>
public class SessionTaintMappingTests
{
    private static readonly Guid _first = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");
    private static readonly Guid _second = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");

    [Fact]
    public void Empty_session_maps_to_the_clean_value()
    {
        // A session with no rows loads SessionTaint.Clean; the empty-row mapping is the same value.
        var taint = SessionTaintMapping.MapRow("Clean", "[]");

        Assert.Equal(TaintState.Clean, taint.State);
        Assert.Empty(taint.Sources);
        Assert.Equal(SessionTaint.Clean.State, taint.State);
    }

    [Fact]
    public void Row_maps_state_and_sources_in_arrival_order()
    {
        const string json = """
            [
              {"SourceId":"0196a6f0-0000-7000-8000-000000000201","Trust":"External","Raised":"Hard"},
              {"SourceId":"0196a6f0-0000-7000-8000-000000000202","Trust":"Agent","Raised":"Soft"}
            ]
            """;

        var taint = SessionTaintMapping.MapRow("Hard", json);

        Assert.Equal(TaintState.Hard, taint.State);
        Assert.Equal(
            new[]
            {
                new TaintSource(_first, ContentTrust.External, TaintState.Hard),
                new TaintSource(_second, ContentTrust.Agent, TaintState.Soft),
            },
            taint.Sources);
    }

    [Fact]
    public void Serialized_sources_map_back_to_the_same_taint()
    {
        var sources = ImmutableArray.Create(
            new TaintSource(_first, ContentTrust.External, TaintState.Hard),
            new TaintSource(_second, ContentTrust.Agent, TaintState.Soft));
        var taint = new SessionTaint(TaintState.Hard, sources);

        var mapped = SessionTaintMapping.MapRow(
            SessionTaintMapping.ToSqlState(taint.State),
            SessionTaintMapping.SerializeSources(taint.Sources));

        Assert.Equal(taint.State, mapped.State);
        Assert.Equal(taint.Sources, mapped.Sources);
    }

    [Fact]
    public void Empty_sources_serialize_to_an_empty_json_array()
    {
        Assert.Equal("[]", SessionTaintMapping.SerializeSources(ImmutableArray<TaintSource>.Empty));
    }

    [Theory]
    [InlineData(TaintState.Clean, "Clean")]
    [InlineData(TaintState.Soft, "Soft")]
    [InlineData(TaintState.Hard, "Hard")]
    public void State_text_round_trips(TaintState state, string text)
    {
        Assert.Equal(text, SessionTaintMapping.ToSqlState(state));
        Assert.Equal(state, SessionTaintMapping.FromSqlState(text));
    }

    [Fact]
    public void Unknown_state_text_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => SessionTaintMapping.FromSqlState("hard"));
    }
}
