using Bazaar.Approvals;
using Bazaar.GitHub.Adapter;
using Bazaar.Sanitization;
using Bazaar.Server.AccountApproval;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.AccountApproval.OwnerQueue;
using Bazaar.Server.AccountApproval.Postgres;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Portability;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Approvals.Db;
using Bazaar.Server.Security.Policy;
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
                ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-test-" + Guid.NewGuid().ToString("N")),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            });
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();
            var sp = host.Services;

            Assert.NotNull(sp.GetRequiredService<IGcNotificationSource>());
            Assert.NotNull(sp.GetRequiredService<AgentResponder>());
            Assert.IsType<PostgresImportJobStore>(sp.GetRequiredService<IImportJobStore>());
            // The owner-signed merge gate and the one verifier it shares with the kill switch are live in the host graph (doc 21, C25).
            Assert.NotNull(sp.GetRequiredService<IOwnerSignedMergeService>());
            Assert.NotNull(sp.GetRequiredService<IOwnerSignatureVerifier>());
            // The Talent tool-grant routes need the owner verifier, the Postgres stores and the approver to resolve from the host graph (doc 25, doc 32 5a).
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.TalentPool.Component.TalentToolGrantApprover>());
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.TalentPool.ITalentToolGrantApproval>());
            // The pending-grants route reads the store and the hire state directly, so it does not need the Council service, which the host does not register yet.
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.TalentPool.ITalentTemplateStore>());
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.Council.Hiring.IHireDecisionStore>());
            Assert.Contains(sp.GetServices<IHostedService>(), service => service is AgentResponderService);

            // Account approval: the owner hand-off reaches the durable approval store and the shared request cap.
            Assert.IsType<PostgresAccountApprovalStore>(sp.GetRequiredService<IAccountApprovalStore>());
            Assert.IsType<PostgresApprovalStore>(sp.GetRequiredService<IApprovalStore>());
            Assert.IsType<OwnerApprovalEnqueuer>(sp.GetRequiredService<IOwnerApprovalEnqueuer>());
            Assert.IsType<OwnerQueueApprovalHandOff>(sp.GetRequiredService<IOwnerApprovalHandOff>());
            Assert.NotNull(sp.GetRequiredService<ApprovalService>());
            Assert.NotNull(sp.GetRequiredService<AccountApprovalService>());
            Assert.NotNull(sp.GetRequiredService<SiteAccessService>());
            Assert.Same(sp.GetRequiredService<IRateBucketStore>(), sp.GetRequiredService<IRateBucketStore>());

            // The module host (doc 48 section 1) is in the graph and fails closed: nothing is packaged, so nothing is in force.
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.ModuleHost.ModuleActivationHost>());
            Assert.IsType<DenyAllOwnerApprovalVerifier>(sp.GetRequiredService<Bazaar.Install.Abstractions.IOwnerApprovalVerifier>());
            Assert.Contains(sp.GetServices<IHostedService>(), service => service is Bazaar.Server.ModuleHost.ModuleActivationHostedService);
            Assert.Contains(sp.GetServices<IHostedService>(), service => service is Bazaar.Server.ModuleHost.ModuleHealthPoller);
            // The display, density and browser pool components (doc 59) are in the graph; DisplayCompositionTests proves they refuse by default.
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.VmDisplay.Abstractions.IGuestDisplay>());
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.VmDensity.Abstractions.IDensityGovernor>());
            Assert.NotNull(sp.GetRequiredService<Bazaar.Server.BrowserPool.Abstractions.IBrowserPool>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }

    /// <summary>
    /// The fun systems use the sanitizer abstractions only. The host supplies the implementation while fun-systems is enabled,
    /// and leaves it out when the component is disabled, so a disabled fun system adds no sanitizer registration.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    public void The_sanitizer_is_registered_only_while_fun_systems_is_enabled(string? funEnabled, bool expected)
    {
        const string dbEnv = "BAZAAR_HOST_APP_TEST_DB";
        const string tokenEnv = "BAZAAR_HOST_APP_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-graph-test");
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["Bazaar:Host:DataRoot"] = Path.GetTempPath(),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            };
            if (funEnabled is not null)
            {
                values["Bazaar:Components:fun-systems:Enabled"] = funEnabled;
                // Lucky runs also need the sanitizer, so it is switched off with the fun systems.
                values["Bazaar:Components:" + Bazaar.Lucky.LuckyComponent.ManifestDefinition.Id + ":Enabled"] = funEnabled;
            }

            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(values);
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();

            Assert.Equal(expected, host.Services.GetService<ITextSanitizer>() is not null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }
}
