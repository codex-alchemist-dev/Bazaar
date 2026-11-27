using Bazaar.Approvals;
using Cdax.Core;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Biometric;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Roles.Biometric.Tests;

public class BiometricWideningCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVerifier(BiometricVerdict verdict) : IBiometricAssertionVerifier
    {
        public int Calls { get; private set; }
        public bool? HighRisk { get; private set; }
        public OwnerPayload? Payload { get; private set; }

        public Task<BiometricVerdict> VerifyAsync(BiometricAssertion assertion, OwnerPayload payload, bool highRisk, DateTimeOffset now, CancellationToken ct = default)
        {
            Calls++;
            HighRisk = highRisk;
            Payload = payload;
            return Task.FromResult(verdict);
        }
    }

    private static readonly Guid _owner = Guid.NewGuid();
    private static readonly BiometricAssertion _assertion = new(1, [1], [2], [3], [4]);

    private static OwnerPayload Payload() =>
        new(WideningArgs.Action, Guid.NewGuid(), [9], null, _owner, "device-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), new byte[16]);

    private static GrantWideningRequest Request(OwnerWideningApproval? approval) =>
        new(new RoleActor(_owner, PrincipalKind.Human, approval), "widen", Guid.NewGuid(), null, []);

    [Fact]
    public async Task A_valid_assertion_over_the_signed_payload_passes_as_high_risk()
    {
        var verifier = new FakeVerifier(BiometricVerdict.Valid);
        var payload = Payload();
        var approval = new OwnerWideningApproval(payload, [1], _assertion);

        var passed = await new BiometricWideningCheck(verifier, TimeProvider.System).PassedAsync(Request(approval), approval, Ct);

        Assert.True(passed);
        Assert.True(verifier.HighRisk);
        Assert.Same(payload, verifier.Payload);
    }

    [Fact]
    public async Task No_assertion_is_refused_without_asking_the_verifier()
    {
        var verifier = new FakeVerifier(BiometricVerdict.Valid);
        var approval = new OwnerWideningApproval(Payload(), [1]);

        Assert.False(await new BiometricWideningCheck(verifier, TimeProvider.System).PassedAsync(Request(approval), approval, Ct));
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(BiometricVerdict.Malformed)]
    [InlineData(BiometricVerdict.UnknownCredential)]
    [InlineData(BiometricVerdict.CredentialRevoked)]
    [InlineData(BiometricVerdict.SyncedCredential)]
    [InlineData(BiometricVerdict.ChallengeMismatch)]
    [InlineData(BiometricVerdict.BadSignature)]
    [InlineData(BiometricVerdict.SignCountReplay)]
    [InlineData(BiometricVerdict.Stale)]
    [InlineData(BiometricVerdict.FactorNotAllowed)]
    [InlineData(BiometricVerdict.LivenessMissing)]
    public async Task Any_other_verdict_refuses(BiometricVerdict verdict)
    {
        var approval = new OwnerWideningApproval(Payload(), [1], _assertion);

        Assert.False(await new BiometricWideningCheck(new FakeVerifier(verdict), TimeProvider.System).PassedAsync(Request(approval), approval, Ct));
    }

    [Fact]
    public void The_component_registers_the_check_and_depends_on_roles()
    {
        var component = new RolesBiometricComponent();
        Assert.Equal("roles-biometric", component.Manifest.Id);
        Assert.Equal(["roles"], component.Manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddSingleton<IBiometricAssertionVerifier>(new FakeVerifier(BiometricVerdict.Valid));
        component.AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<BiometricWideningCheck>(Assert.Single(provider.GetServices<IWideningBiometricCheck>()));
    }
}
