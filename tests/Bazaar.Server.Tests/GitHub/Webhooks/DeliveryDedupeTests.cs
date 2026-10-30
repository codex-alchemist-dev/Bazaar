using System;
using Bazaar.Server.GitHub.Webhooks;

namespace Bazaar.Server.Tests.GitHub.Webhooks;

public class DeliveryDedupeTests
{
    [Fact]
    public void FirstDeliveryIsNewAndRepeatIsDuplicate()
    {
        var dedupe = new DeliveryDedupe(capacity: 10);

        Assert.True(dedupe.TryRecord("72d3162e-cc78-11e3-81ab-4c9367dc0958"));
        Assert.False(dedupe.TryRecord("72d3162e-cc78-11e3-81ab-4c9367dc0958"));
        Assert.Equal(1, dedupe.Count);
    }

    [Fact]
    public void ContainsDoesNotRecord()
    {
        var dedupe = new DeliveryDedupe(capacity: 10);

        Assert.False(dedupe.Contains("a"));
        Assert.Equal(0, dedupe.Count);
        Assert.True(dedupe.TryRecord("a"));
        Assert.True(dedupe.Contains("a"));
    }

    [Fact]
    public void BoundedWindowEvictsOldestId()
    {
        var dedupe = new DeliveryDedupe(capacity: 3);
        Assert.True(dedupe.TryRecord("1"));
        Assert.True(dedupe.TryRecord("2"));
        Assert.True(dedupe.TryRecord("3"));

        Assert.True(dedupe.TryRecord("4"));

        Assert.Equal(3, dedupe.Count);
        Assert.False(dedupe.Contains("1"));
        Assert.True(dedupe.TryRecord("1"), "an evicted id is treated as new again");
        Assert.False(dedupe.TryRecord("4"), "newer ids stay inside the window");
    }

    [Fact]
    public void IdsAreCaseSensitive()
    {
        var dedupe = new DeliveryDedupe(capacity: 10);

        Assert.True(dedupe.TryRecord("abc"));
        Assert.True(dedupe.TryRecord("ABC"));
    }

    [Fact]
    public void NonPositiveCapacityIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryDedupe(capacity: 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyDeliveryIdIsRejected(string? deliveryId)
    {
        var dedupe = new DeliveryDedupe(capacity: 10);

        Assert.ThrowsAny<ArgumentException>(() => dedupe.TryRecord(deliveryId!));
    }
}
