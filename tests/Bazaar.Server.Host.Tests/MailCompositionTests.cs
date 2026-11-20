using Bazaar.Server.Host.Composition;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Server.MailDns;
using Bazaar.Server.MailRuntime;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

public sealed class MailCompositionTests
{
    private const string _dbEnv = "BAZAAR_MAIL_COMPOSITION_TEST_DB";

    private static IHost Build(bool? mailEnabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-mail-test-" + Guid.NewGuid().ToString("N")),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _dbEnv,
        };
        if (mailEnabled is { } enabled)
        {
            values[MailCompositionExtensions.EnabledKey] = enabled ? "true" : "false";
        }

        // A real host builder: the startup sequence needs IHostApplicationLifetime. Build does not start it, so no database is touched.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddBazaarHostApplication(builder.Configuration);
        return builder.Build();
    }

    [Fact]
    public void Enabled_resolves_the_quarantine_store_and_registers_both_loops_and_one_dns_step()
    {
        Environment.SetEnvironmentVariable(_dbEnv, "Host=localhost;Database=unused");
        try
        {
            using var host = Build(mailEnabled: true);
            var sp = host.Services;

            Assert.IsType<PostgresMailQuarantineStore>(sp.GetRequiredService<IMailQuarantineStore>());
            Assert.NotNull(sp.GetRequiredService<IMailGcAppender>());
            Assert.IsNotType<FailClosedMailBodyProtector>(sp.GetRequiredService<Bazaar.Server.MailOutbound.IMailBodyProtector>());
            Assert.IsNotType<FailClosedMailBodySealer>(sp.GetRequiredService<Bazaar.Server.MailInbound.IMailBodySealer>());
            var hosted = sp.GetServices<IHostedService>().ToList();
            Assert.Single(hosted, s => s is InboundMailHostedService);
            Assert.Single(hosted, s => s is OutboundMailResumeHostedService);
            Assert.Single(sp.GetServices<ISetupStep>(), s => s is MailDnsSetupStep);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_dbEnv, null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Disabled_registers_no_mail_runtime(bool? mailEnabled)
    {
        Environment.SetEnvironmentVariable(_dbEnv, "Host=localhost;Database=unused");
        try
        {
            using var host = Build(mailEnabled);
            var sp = host.Services;

            Assert.Null(sp.GetService<IMailQuarantineStore>());
            Assert.Null(sp.GetService<IMailGcAppender>());
            Assert.DoesNotContain(sp.GetServices<IHostedService>(), s => s is InboundMailHostedService or OutboundMailResumeHostedService);
            Assert.DoesNotContain(sp.GetServices<ISetupStep>(), s => s is MailDnsSetupStep);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_dbEnv, null);
        }
    }
}
