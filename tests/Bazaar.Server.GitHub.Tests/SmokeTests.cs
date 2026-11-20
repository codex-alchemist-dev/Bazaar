namespace Bazaar.Server.GitHub.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Component_registration_is_callable()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.Same(services, services.AddBazaarGitHub());
    }
}
