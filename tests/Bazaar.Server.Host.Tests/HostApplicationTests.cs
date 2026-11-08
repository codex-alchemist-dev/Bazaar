using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

public sealed class HostApplicationTests
{
    /// <summary>
    /// The runnable app needs the responder's notify source and the wire endpoint. Program.cs once registered the responder
    /// without them, and the host failed at start; this resolves the same graph Program.cs builds, so that regression fails here.
    /// </summary>
    [Fact]
    public async Task The_full_host_graph_resolves_and_the_responder_is_a_hosted_service()
    {
        const string dbEnv = "BAZAAR_HOST_APP_TEST_DB";
        const string tokenEnv = "BAZAAR_HOST_APP_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-graph-test");
        try
        {
            // A real host builder, not a bare container: the startup sequence needs IHostApplicationLifetime. Build does not start it.
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Host:DataRoot"] = Path.GetTempPath(),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            });
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();
            var sp = host.Services;

            Assert.NotNull(sp.GetRequiredService<IGcNotificationSource>());
            Assert.NotNull(sp.GetRequiredService<AgentResponder>());
            Assert.Contains(sp.GetServices<IHostedService>(), service => service is AgentResponderService);
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }
}
