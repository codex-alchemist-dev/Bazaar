using Bazaar.Contracts.Gateway;

namespace Bazaar.Contracts.Tests;

public sealed class GatewayIdentityTextTests
{
    [Fact]
    public void Identity_text_round_trips_and_rejects_unknown_values()
    {
        foreach (var id in Enum.GetValues<GatewayIdentity>())
        {
            Assert.Equal(id, GatewayIdentityText.FromText(id.ToText()));
        }

        Assert.Equal("subscription", GatewayIdentity.Subscription.ToText());
        Assert.Throws<InvalidDataException>(() => GatewayIdentityText.FromText("other"));
    }
}
