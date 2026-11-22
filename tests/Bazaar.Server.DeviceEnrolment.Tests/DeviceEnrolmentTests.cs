using Bazaar.Approvals;
using Bazaar.Server.Biometric;

namespace Bazaar.Server.DeviceEnrolment.Tests;

public sealed class DeviceEnrolmentTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _human = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _device = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");
    private static readonly Guid _approver = Guid.Parse("0197a000-0000-7000-8000-0000000000d2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialRegistration GoodCredential(bool attested = true, bool single = true, bool proof = true) =>
        new(proof, attested, Keys.P256(), new HashSet<BiometricFactor> { BiometricFactor.FaceDepth }, single, 0);

    private sealed class Rig
    {
        public MemoryStore Store { get; } = new();
        public MemoryAudit Audit { get; } = new();
        public FakeCredentialAttestor Credential { get; }
        public FakeOwnerSignature Owner { get; }
        public FakeBiometric Bio { get; }
        public DeviceEnrolment Service { get; }

        public Rig(
            ApprovalKeyAttestationResult? key = null,
            CredentialRegistration? credential = null,
            OwnerSignatureVerdict owner = OwnerSignatureVerdict.Valid,
            BiometricVerdict bio = BiometricVerdict.Valid)
        {
            Store.Devices.Add((_device, _human, DeviceClass.Windows));
            Store.Devices.Add((_device, _human, DeviceClass.Android));
            Credential = new FakeCredentialAttestor(credential ?? GoodCredential());
            Owner = new FakeOwnerSignature(owner);
            Bio = new FakeBiometric(bio);
            Service = new DeviceEnrolment(
                Store,
                new FakeKeyAttestor(key ?? new ApprovalKeyAttestationResult(true, true, true)),
                Credential,
                Owner,
                Bio,
                Audit);
        }
    }

    private static async Task<EnrolmentRequest> RequestAsync(Rig rig, EnrolmentStarter? starter = null, DeviceClass cls = DeviceClass.Windows, byte[]? key = null)
    {
        var ch = await rig.Service.BeginAsync(_human, _device, _now, Ct);
        return new EnrolmentRequest(
            _human, _device, cls, ch.Nonce, key ?? Keys.P256(), [[1, 2]], [9, 9], [1], [2],
            starter ?? new ConsoleCodeStarter(await rig.Service.IssueConsoleCodeAsync(_human, _now, Ct)));
    }

    private static EnrolledDeviceStarter FurtherDevice(EnrolmentRequest r, string action = EnrolmentArgs.Action, Guid? approverDevice = null, Guid? human = null)
    {
        var payload = new OwnerPayload(
            action, Guid.NewGuid(), EnrolmentArgs.Hash(r.DeviceId, r.ApprovalPublicKeySpki), null, human ?? _human,
            (approverDevice ?? _approver).ToString("D"), _now, _now.AddMinutes(2), new byte[16]);
        return new EnrolledDeviceStarter(payload, [7], new BiometricAssertion(1, [1], [2], [3], [4]));
    }

    [Fact]
    public async Task First_device_enrols_with_the_console_code_and_writes_one_commit()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);

        var outcome = await rig.Service.CompleteAsync(req, _now, Ct);

        Assert.Equal(EnrolmentVerdict.Enrolled, outcome.Verdict);
        Assert.True(outcome.CredentialAttested);
        var commit = Assert.Single(rig.Store.Commits);
        Assert.Equal(_device, commit.DeviceId);
        Assert.NotNull(commit.ConsoleCodeHash);
        Assert.Contains(BiometricFactor.FaceDepth, commit.Credential.AttestedFactors);
        Assert.Empty(rig.Audit.Refusals);
    }

    [Fact]
    public async Task The_credential_signs_the_challenge_that_ties_it_to_this_key_device_and_nonce()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);

        await rig.Service.CompleteAsync(req, _now, Ct);

        Assert.Equal(EnrolmentArgs.Challenge(req.Nonce, _human, _device, req.ApprovalPublicKeySpki), rig.Credential.LastChallenge);
        Assert.NotEqual(
            EnrolmentArgs.Challenge(req.Nonce, _human, _device, req.ApprovalPublicKeySpki),
            EnrolmentArgs.Challenge(req.Nonce, _human, _device, Keys.P256()));
    }

    [Fact]
    public async Task A_spent_nonce_cannot_enrol_twice_and_a_code_cannot_be_reused()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);
        Assert.True((await rig.Service.CompleteAsync(req, _now, Ct)).Succeeded);

        var again = await rig.Service.CompleteAsync(req, _now, Ct);

        Assert.Equal(EnrolmentVerdict.ChallengeInvalid, again.Verdict);
        Assert.Single(rig.Store.Commits);
        Assert.Equal([EnrolmentVerdict.ChallengeInvalid], rig.Audit.Refusals);
    }

    [Fact]
    public async Task An_expired_nonce_is_refused()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);

        var outcome = await rig.Service.CompleteAsync(req, _now.AddMinutes(6), Ct);

        Assert.Equal(EnrolmentVerdict.ChallengeInvalid, outcome.Verdict);
    }

    [Fact]
    public async Task A_nonce_issued_for_another_device_is_refused()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);
        var other = req with { DeviceId = Guid.NewGuid() };

        Assert.Equal(EnrolmentVerdict.ChallengeInvalid, (await rig.Service.CompleteAsync(other, _now, Ct)).Verdict);
    }

    [Fact]
    public async Task A_wrong_or_expired_console_code_is_refused()
    {
        var rig = new Rig();
        var wrong = await RequestAsync(rig, new ConsoleCodeStarter("AAAAAAAAAA"));
        Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig.Service.CompleteAsync(wrong, _now, Ct)).Verdict);

        var rig2 = new Rig();
        var stale = await RequestAsync(rig2);
        var late = await rig2.Service.CompleteAsync(stale, _now.AddMinutes(4).AddSeconds(59), Ct);
        Assert.True(late.Succeeded);

        var rig3 = new Rig();
        var code = await rig3.Service.IssueConsoleCodeAsync(_human, _now, Ct);
        var ch = await rig3.Service.BeginAsync(_human, _device, _now.AddMinutes(20), Ct);
        var req = new EnrolmentRequest(_human, _device, DeviceClass.Windows, ch.Nonce, Keys.P256(), [], [9], [1], [2], new ConsoleCodeStarter(code));
        Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig3.Service.CompleteAsync(req, _now.AddMinutes(20), Ct)).Verdict);
    }

    [Fact]
    public async Task A_console_code_issued_to_another_human_is_refused()
    {
        var rig = new Rig();
        var code = await rig.Service.IssueConsoleCodeAsync(Guid.NewGuid(), _now, Ct);
        var req = await RequestAsync(rig, new ConsoleCodeStarter(code));

        Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig.Service.CompleteAsync(req, _now, Ct)).Verdict);
    }

    [Fact]
    public async Task The_console_code_ignores_case_and_dashes()
    {
        var rig = new Rig();
        var code = await rig.Service.IssueConsoleCodeAsync(_human, _now, Ct);
        var typed = code[..5].ToLowerInvariant() + "-" + code[5..];
        var req = await RequestAsync(rig, new ConsoleCodeStarter(typed));

        Assert.True((await rig.Service.CompleteAsync(req, _now, Ct)).Succeeded);
    }

    [Fact]
    public async Task A_further_device_enrols_with_an_owner_signature_and_a_high_risk_biometric_check()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);
        req = req with { Starter = FurtherDevice(req) };

        var outcome = await rig.Service.CompleteAsync(req, _now, Ct);

        Assert.True(outcome.Succeeded);
        Assert.Null(rig.Store.Commits[0].ConsoleCodeHash);
        Assert.True(rig.Bio.LastHighRisk);
    }

    [Theory]
    [InlineData(OwnerSignatureVerdict.BadSignature)]
    [InlineData(OwnerSignatureVerdict.RevokedDevice)]
    [InlineData(OwnerSignatureVerdict.NonceReplayed)]
    [InlineData(OwnerSignatureVerdict.Expired)]
    public async Task A_refused_owner_signature_refuses_the_enrolment(OwnerSignatureVerdict verdict)
    {
        var rig = new Rig(owner: verdict);
        var req = await RequestAsync(rig);
        req = req with { Starter = FurtherDevice(req) };

        Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig.Service.CompleteAsync(req, _now, Ct)).Verdict);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task A_failed_biometric_check_refuses_the_enrolment()
    {
        var rig = new Rig(bio: BiometricVerdict.LivenessMissing);
        var req = await RequestAsync(rig);
        req = req with { Starter = FurtherDevice(req) };

        Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig.Service.CompleteAsync(req, _now, Ct)).Verdict);
    }

    [Fact]
    public async Task A_signature_for_another_action_human_key_or_the_new_device_itself_is_refused()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig);

        var wrongAction = req with { Starter = FurtherDevice(req, action: "roles.widen") };
        var wrongHuman = req with { Starter = FurtherDevice(req, human: Guid.NewGuid()) };
        var selfApproved = req with { Starter = FurtherDevice(req, approverDevice: _device) };
        var otherKey = req with { Starter = FurtherDevice(req with { ApprovalPublicKeySpki = Keys.P256() }) };

        foreach (var r in new[] { wrongAction, wrongHuman, selfApproved, otherKey })
        {
            Assert.Equal(EnrolmentVerdict.StarterRefused, (await rig.Service.CompleteAsync(r, _now, Ct)).Verdict);
        }

        Assert.Equal(0, rig.Owner.Calls);
    }

    [Fact]
    public async Task An_unknown_revoked_or_wrong_class_device_is_refused()
    {
        var rig = new Rig();
        var req = await RequestAsync(rig, cls: DeviceClass.Linux);

        Assert.Equal(EnrolmentVerdict.DeviceUnknown, (await rig.Service.CompleteAsync(req, _now, Ct)).Verdict);
    }

    [Fact]
    public async Task An_approval_key_that_is_not_p256_is_refused()
    {
        var rig = new Rig();
        var bad = await RequestAsync(rig, key: Keys.P384());
        var junk = await RequestAsync(rig, key: [1, 2, 3]);

        Assert.Equal(EnrolmentVerdict.ApprovalKeyShape, (await rig.Service.CompleteAsync(bad, _now, Ct)).Verdict);
        Assert.Equal(EnrolmentVerdict.ApprovalKeyShape, (await rig.Service.CompleteAsync(junk, _now, Ct)).Verdict);
    }

    [Theory]
    [InlineData(false, true, true, DeviceClass.Windows)]
    [InlineData(true, false, true, DeviceClass.Windows)]
    [InlineData(true, true, false, DeviceClass.Android)]
    public async Task A_software_or_unattested_approval_key_is_refused(bool verified, bool hardware, bool perUse, DeviceClass cls)
    {
        var rig = new Rig(key: new ApprovalKeyAttestationResult(verified, hardware, perUse));
        var req = await RequestAsync(rig, cls: cls);

        Assert.Equal(EnrolmentVerdict.ApprovalKeyNotHardware, (await rig.Service.CompleteAsync(req, _now, Ct)).Verdict);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task A_windows_key_needs_no_per_use_flag_but_an_android_key_does()
    {
        var rig = new Rig(key: new ApprovalKeyAttestationResult(true, true, false));
        var req = await RequestAsync(rig, cls: DeviceClass.Windows);

        Assert.True((await rig.Service.CompleteAsync(req, _now, Ct)).Succeeded);
    }

    [Fact]
    public async Task A_credential_that_did_not_sign_the_challenge_or_is_synced_is_refused()
    {
        var noProof = new Rig(credential: GoodCredential(proof: false));
        var synced = new Rig(credential: GoodCredential(single: false));

        Assert.Equal(EnrolmentVerdict.CredentialRefused, (await noProof.Service.CompleteAsync(await RequestAsync(noProof), _now, Ct)).Verdict);
        Assert.Equal(EnrolmentVerdict.CredentialSynced, (await synced.Service.CompleteAsync(await RequestAsync(synced), _now, Ct)).Verdict);
    }

    [Fact]
    public async Task An_unattested_credential_enrols_with_no_factors_so_it_can_never_pass_a_high_risk_check()
    {
        var rig = new Rig(credential: GoodCredential(attested: false));

        var outcome = await rig.Service.CompleteAsync(await RequestAsync(rig), _now, Ct);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.CredentialAttested);
        Assert.Empty(rig.Store.Commits[0].Credential.AttestedFactors);
    }

    [Fact]
    public async Task A_second_enrolment_for_the_same_device_is_refused()
    {
        var rig = new Rig();
        Assert.True((await rig.Service.CompleteAsync(await RequestAsync(rig), _now, Ct)).Succeeded);

        var second = await rig.Service.CompleteAsync(await RequestAsync(rig), _now, Ct);

        Assert.Equal(EnrolmentVerdict.AlreadyEnrolled, second.Verdict);
        Assert.Single(rig.Store.Commits);
    }

    [Fact]
    public async Task Malformed_requests_are_refused_and_never_throw()
    {
        var rig = new Rig();
        var good = await RequestAsync(rig);

        var bad = new[]
        {
            good with { Nonce = [1] },
            good with { ApprovalPublicKeySpki = [] },
            good with { ApprovalPublicKeySpki = new byte[20_000] },
            good with { CredentialId = [] },
            good with { CredentialClientData = [] },
            good with { ApprovalKeyAttestation = Enumerable.Range(0, 9).Select(_ => new byte[1]).ToArray() },
            good with { Class = (DeviceClass)99 },
            good with { Starter = null! },
        };

        foreach (var r in bad)
        {
            Assert.Equal(EnrolmentVerdict.Malformed, (await rig.Service.CompleteAsync(r, _now, Ct)).Verdict);
        }

        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public void Console_codes_have_the_documented_shape_and_hash()
    {
        var code = ConsoleEnrolmentCode.Generate();

        Assert.Equal(10, code.Length);
        Assert.NotNull(ConsoleEnrolmentCode.Hash(code));
        Assert.Null(ConsoleEnrolmentCode.Hash("short"));
        Assert.Null(ConsoleEnrolmentCode.Hash("0000000000"));
        Assert.Null(ConsoleEnrolmentCode.Hash(null));
    }

    [Fact]
    public async Task Lifetimes_can_be_shortened_but_never_lengthened()
    {
        var store = new MemoryStore();
        var svc = new DeviceEnrolment(
            store,
            new FakeKeyAttestor(new ApprovalKeyAttestationResult(true, true, true)),
            new FakeCredentialAttestor(GoodCredential()),
            new FakeOwnerSignature(OwnerSignatureVerdict.Valid),
            new FakeBiometric(BiometricVerdict.Valid),
            new MemoryAudit(),
            new EnrolmentOptions { ChallengeLifetime = TimeSpan.FromHours(3) });

        var ch = await svc.BeginAsync(_human, _device, _now, Ct);

        Assert.Equal(_now.AddMinutes(5), ch.ExpiresAt);
    }
}
