using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The kanban NOTIFY payload parser (doc 38, WP-R6). A bad payload is dropped and logged by the listener, never thrown out of
/// its loop, so the parser must return false for every shape it cannot read instead of throwing. Pure; no database.
/// </summary>
public sealed class RyuseiEventListenerPayloadTests
{
    [Fact]
    public void A_kanban_payload_yields_its_seq()
    {
        var parsed = RyuseiEventListener.TryParseTaskEventSeq("{\"task_id\":\"6f1c\",\"seq\":42,\"kind\":\"transitioned\"}", out var seq);

        Assert.True(parsed);
        Assert.Equal(42L, seq);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("\"seq\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"task_id\":\"6f1c\"}")]
    [InlineData("{\"seq\":\"forty-two\"}")]
    [InlineData("{\"seq\":4.5}")]
    [InlineData("{\"seq\":null}")]
    public void A_payload_without_an_integer_seq_is_dropped_without_throwing(string payload)
    {
        var parsed = RyuseiEventListener.TryParseTaskEventSeq(payload, out var seq);

        Assert.False(parsed);
        Assert.Equal(0L, seq);
    }
}
