using Bazaar.Contracts.Enrolment;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DeviceEnrolment.Tests;

/// <summary>The shipped defaults refuse what they cannot verify (doc 32 section 5b step 4), and both sides compute one challenge.</summary>
public sealed class StrictDefaultTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _human = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _device = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_default_attestors_refuse_every_enrolment()
    {
        var store = new MemoryStore();
        store.Devices.Add((_device, _human, DeviceClass.Windows));
        var audit = new MemoryAudit();
        var services = new ServiceCollection();
        services.AddSingleton<IEnrolmentStore>(store);
        services.AddSingleton<IEnrolmentAudit>(audit);
        services.AddSingleton<Bazaar.Approvals.IOwnerSignatureVerifier>(new FakeOwnerSignature(Bazaar.Approvals.OwnerSignatureVerdict.Valid));
        services.AddSingleton<Bazaar.Server.Biometric.IBiometricAssertionVerifier>(new FakeBiometric(Bazaar.Server.Biometric.BiometricVerdict.Valid));
        services.AddBazaarDeviceEnrolment();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<RefusingApprovalKeyAttestor>(provider.GetRequiredService<IApprovalKeyAttestor>());
        Assert.IsType<RefusingCredentialAttestor>(provider.GetRequiredService<ICredentialAttestor>());

        var service = provider.GetRequiredService<IDeviceEnrolment>();
        var challenge = await service.BeginAsync(_human, _device, _now, Ct);
        var code = await service.IssueConsoleCodeAsync(_human, _now, Ct);
        var request = new EnrolmentRequest(_human, _device, DeviceClass.Windows, challenge.Nonce, Keys.P256(), [[1]], [9], [1], [2], new ConsoleCodeStarter(code));

        var outcome = await service.CompleteAsync(request, _now, Ct);

        Assert.Equal(EnrolmentVerdict.ApprovalKeyNotHardware, outcome.Verdict);
        Assert.Empty(store.Commits);
        Assert.Equal([EnrolmentVerdict.ApprovalKeyNotHardware], audit.Refusals);
    }

    [Fact]
    public async Task A_host_attestor_registered_first_replaces_the_refusing_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApprovalKeyAttestor>(new FakeKeyAttestor(new ApprovalKeyAttestationResult(true, true, true)));
        services.AddSingleton<IEnrolmentStore>(new MemoryStore());
        services.AddSingleton<IEnrolmentAudit>(new MemoryAudit());
        services.AddSingleton<Bazaar.Approvals.IOwnerSignatureVerifier>(new FakeOwnerSignature(Bazaar.Approvals.OwnerSignatureVerdict.Valid));
        services.AddSingleton<Bazaar.Server.Biometric.IBiometricAssertionVerifier>(new FakeBiometric(Bazaar.Server.Biometric.BiometricVerdict.Valid));
        services.AddBazaarDeviceEnrolment();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<FakeKeyAttestor>(provider.GetRequiredService<IApprovalKeyAttestor>());
        Assert.IsType<RefusingCredentialAttestor>(provider.GetRequiredService<ICredentialAttestor>());
    }

    [Fact]
    public async Task The_refusing_credential_attestor_never_reports_a_valid_proof()
    {
        var reg = await new RefusingCredentialAttestor().VerifyAsync([1], [2], [3], [4], Ct);

        Assert.False(reg.ProofValid);
        Assert.False(reg.Attested);
        Assert.False(reg.SingleDevice);
    }

    [Fact]
    public void The_challenge_formula_is_shared_between_client_and_server()
    {
        var nonce = new byte[32];
        var key = Keys.P256();

        Assert.Equal(EnrolmentChallengeFormula.Compute(nonce, _human, _device, key), EnrolmentArgs.Challenge(nonce, _human, _device, key));
    }
}
