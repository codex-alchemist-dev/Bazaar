using Bazaar.Kodo.Standby;

namespace Bazaar.Kodo.Tests.Standby;

public sealed class FencingAndLeaseTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3, 3, FencingVerdict.Current)]
    [InlineData(2, 3, FencingVerdict.Stale)]
    [InlineData(0, 0, FencingVerdict.Stale)]
    [InlineData(4, 3, FencingVerdict.Ahead)]
    public void TokenCheckRejectsStaleAlways(long presented, long current, FencingVerdict expected) =>
        Assert.Equal(expected, FencingToken.Check(new FencingToken(presented), new FencingToken(current)));

    [Fact]
    public void TokenIsMonotonicAndRejectsNegative()
    {
        Assert.True(new FencingToken(2).Next() > new FencingToken(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FencingToken(-1));
        Assert.Throws<OverflowException>(() => new FencingToken(long.MaxValue).Next());
    }

    [Fact]
    public void LeaseExpiryHonoursMargin()
    {
        LeaseRecord lease = LeaseRecord.Create("a", new FencingToken(1), _t0, TimeSpan.FromSeconds(10));
        Assert.False(lease.IsExpired(_t0.AddSeconds(9.9)));
        Assert.True(lease.IsExpired(_t0.AddSeconds(10)));
        Assert.False(lease.IsExpired(_t0.AddSeconds(11), TimeSpan.FromSeconds(3)));
        Assert.True(lease.IsExpired(_t0.AddSeconds(13), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void LeaseCreateRejectsBadValues()
    {
        Assert.Throws<ArgumentException>(() => LeaseRecord.Create("", new FencingToken(1), _t0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => LeaseRecord.Create("a", new FencingToken(0), _t0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => LeaseRecord.Create("a", new FencingToken(1), _t0, TimeSpan.Zero));
    }

    [Fact]
    public void NewerPrefersEpochThenExpiry()
    {
        LeaseRecord a = LeaseRecord.Create("a", new FencingToken(1), _t0, TimeSpan.FromSeconds(30));
        LeaseRecord b = LeaseRecord.Create("b", new FencingToken(2), _t0, TimeSpan.FromSeconds(5));
        LeaseRecord a2 = LeaseRecord.Create("a", new FencingToken(1), _t0, TimeSpan.FromSeconds(60));
        Assert.Same(b, LeaseRecord.Newer(a, b));
        Assert.Same(a2, LeaseRecord.Newer(a, a2));
        Assert.Null(LeaseRecord.Newer(null, null));
    }

    [Fact]
    public void CodecIsCanonicalAndDigestStable()
    {
        LeaseRecord lease = LeaseRecord.Create("node-a", new FencingToken(7), _t0.ToOffset(TimeSpan.FromHours(2)), TimeSpan.FromSeconds(15));
        byte[] first = StandbyCodec.Serialize(lease);
        Assert.Equal(first, StandbyCodec.Serialize(lease with { }));
        Assert.Equal(StandbyCodec.DigestLength, StandbyCodec.Digest(lease).Length);
        Assert.Equal(lease, StandbyCodec.TryDeserializeLease(first));
        Assert.NotEqual(StandbyCodec.Digest(lease), StandbyCodec.Digest(lease with { Epoch = 8 }));
    }

    [Fact]
    public void CodecRejectsGarbageAndUnknownMembers()
    {
        Assert.Null(StandbyCodec.TryDeserializeLease("{}"u8));
        Assert.Null(StandbyCodec.TryDeserializeLease("nope"u8));
        LeaseRecord lease = LeaseRecord.Create("a", new FencingToken(1), _t0, TimeSpan.FromSeconds(5));
        string json = System.Text.Encoding.UTF8.GetString(StandbyCodec.Serialize(lease)).Replace("{", "{\"extra\":1,", StringComparison.Ordinal);
        Assert.Null(StandbyCodec.TryDeserializeLease(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void StateRoundTripsAndChecksInvariants()
    {
        StandbyState state = new()
        {
            Version = 1,
            NodeId = "a",
            Role = StandbyRole.Fenced,
            Epoch = 2,
            HighestEpochSeen = 3,
            Lease = null,
            Cause = StandbyCauses.SplitBrain,
            At = _t0,
        };
        Assert.True(state.IsSplitBrain);
        Assert.Equal(state, StandbyCodec.TryDeserializeState(StandbyCodec.Serialize(state)));
        Assert.Throws<ArgumentException>(() => StandbyCodec.Serialize(state with { HighestEpochSeen = 1 }));
    }

    [Fact]
    public void SelfFenceRules()
    {
        LeaseRecord lease = LeaseRecord.Create("a", new FencingToken(2), _t0, TimeSpan.FromSeconds(10));
        Assert.False(SelfFence.Evaluate(StandbyRole.Primary, 2, lease, 2, _t0.AddSeconds(5)).Fence);
        Assert.Equal(StandbyCauses.LeaseLost, SelfFence.Evaluate(StandbyRole.Primary, 2, lease, 2, _t0.AddSeconds(10)).Cause);
        Assert.Equal(StandbyCauses.Superseded, SelfFence.Evaluate(StandbyRole.Primary, 2, lease, 3, _t0).Cause);
        Assert.Equal(StandbyCauses.LeaseMissing, SelfFence.Evaluate(StandbyRole.Primary, 2, null, 2, _t0).Cause);
        Assert.Equal(StandbyCauses.LeaseMissing, SelfFence.Evaluate(StandbyRole.Primary, 3, lease, 3, _t0).Cause);
        Assert.False(SelfFence.Evaluate(StandbyRole.Standby, 0, null, 0, _t0).Fence);
    }
}
