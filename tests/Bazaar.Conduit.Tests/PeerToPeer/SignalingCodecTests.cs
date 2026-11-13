using Bazaar.Conduit.PeerToPeer;

namespace Bazaar.Conduit.Tests.PeerToPeer;

public sealed class SignalingCodecTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var message = new SignalMessage(SignalKinds.Candidate, "session-7", "203.0.113.5:50000");

        var text = SignalingCodec.Encode(message);

        Assert.True(SignalingCodec.TryDecode(text, out var decoded));
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void Encode_UsesCamelCaseFieldNames()
    {
        var text = SignalingCodec.Encode(new SignalMessage(SignalKinds.End, "s", "bye"));

        Assert.Equal("{\"kind\":\"end\",\"sessionId\":\"s\",\"payload\":\"bye\"}", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"kind\":\"candidate\",\"sessionId\":\"s\"}")]
    [InlineData("{\"kind\":\"teleport\",\"sessionId\":\"s\",\"payload\":\"x\"}")]
    public void TryDecode_RejectsBadInputWithoutThrowing(string text)
    {
        Assert.False(SignalingCodec.TryDecode(text, out var message));
        Assert.Null(message);
    }

    [Fact]
    public void KnownKinds_AreTheFourProtocolKinds()
    {
        Assert.True(SignalKinds.IsKnown("offer"));
        Assert.True(SignalKinds.IsKnown("answer"));
        Assert.True(SignalKinds.IsKnown("candidate"));
        Assert.True(SignalKinds.IsKnown("end"));
        Assert.False(SignalKinds.IsKnown("Candidate"));
    }
}
