using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Probes;

namespace Bazaar.Server.Tests.Setup.Probes;

public sealed class SecretsResolutionProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SetupPlan Plan(string kekRef, bool withStoreRef = false) => new()
    {
        Custody = new SetupCustodySection { KekProvider = "env", KekRef = kekRef },
        Models = new SetupModelsSection
        {
            Sources = withStoreRef ? [new SetupModelSource { Id = "openai", KeyRef = "secret://bazaar/providers/openai/key" }] : [],
        },
    };

    private static SecretsResolutionProbe Probe(bool envSet = true, bool fileThere = true) => new(_ => envSet, _ => fileThere);

    [Fact]
    public async Task A_set_env_variable_or_a_present_file_resolves()
    {
        Assert.Equal(SetupProbeOutcome.Healthy, (await Probe().ProbeAsync(ProbeTestPlans.Context(Plan("env:BAZAAR_KEK")), Ct)).Outcome);
        Assert.Equal(SetupProbeOutcome.Healthy, (await Probe().ProbeAsync(ProbeTestPlans.Context(Plan("file:/etc/bazaar/kek")), Ct)).Outcome);
    }

    [Fact]
    public async Task A_missing_env_variable_or_file_fails_and_the_reference_is_never_repeated()
    {
        var env = await Probe(envSet: false).ProbeAsync(ProbeTestPlans.Context(Plan("env:SECRET_KEK_VAR")), Ct);
        var file = await Probe(fileThere: false).ProbeAsync(ProbeTestPlans.Context(Plan("file:/etc/hidden/kek")), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, env.Outcome);
        Assert.Equal(SetupProbeOutcome.Failed, file.Outcome);
        Assert.DoesNotContain("SECRET_KEK_VAR", ProbeTestPlans.AllText(env), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", ProbeTestPlans.AllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dpapi:C:\\ProgramData\\Bazaar\\kek.bin")]
    [InlineData("systemd-creds:bazaar-kek")]
    [InlineData("")]
    public async Task A_reference_kind_that_needs_an_os_provider_is_unavailable(string kekRef)
    {
        var reading = await Probe().ProbeAsync(ProbeTestPlans.Context(Plan(kekRef)), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
    }

    [Fact]
    public async Task Secret_store_references_keep_the_check_not_evaluated_because_they_have_no_existence_check()
    {
        var reading = await Probe().ProbeAsync(ProbeTestPlans.Context(Plan("env:BAZAAR_KEK", withStoreRef: true)), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
        Assert.Contains("1 secret-store", reading.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_env_lookup_reads_the_real_environment_without_returning_the_value()
    {
        const string name = "BAZAAR_PROBE_TEST_KEK_9F3A";
        Environment.SetEnvironmentVariable(name, "value-that-must-not-leak");
        try
        {
            var reading = await new SecretsResolutionProbe().ProbeAsync(ProbeTestPlans.Context(Plan("env:" + name)), Ct);

            Assert.Equal(SetupProbeOutcome.Healthy, reading.Outcome);
            Assert.DoesNotContain("value-that-must-not-leak", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
