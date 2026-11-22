using Bazaar.Approvals;
using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles.Tests;

public sealed class OwnerSignedWideningGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVerifier(OwnerSignatureVerdict verdict) : IOwnerSignatureVerifier
    {
        public int Calls { get; private set; }

        public Task<OwnerSignatureVerdict> VerifyAsync(OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(verdict);
        }
    }

    private sealed class Biometric(bool pass) : IWideningBiometricCheck
    {
        public Task<bool> PassedAsync(GrantWideningRequest request, CancellationToken ct = default) => Task.FromResult(pass);
    }

    private static readonly Guid _owner = Guid.NewGuid();
    private static readonly Guid _role = Guid.NewGuid();
    private static readonly Guid _person = Guid.NewGuid();
    private static readonly Grant[] _added = [new(Capabilities.Read, Scope.Company), new(Capabilities.Post, Scope.Gc("gc-1"))];

    private static GrantWideningRequest Request(OwnerWideningApproval? approval, PrincipalKind kind = PrincipalKind.Human) =>
        new(new RoleActor(_owner, kind, approval), "widen", _role, _person, _added);

    private static OwnerWideningApproval Signed(string action = WideningArgs.Action, Guid? human = null, byte[]? hash = null)
    {
        var args = hash ?? WideningArgs.Hash(Request(null));
        var now = DateTimeOffset.UtcNow;
        return new OwnerWideningApproval(
            new OwnerPayload(action, Guid.NewGuid(), args, null, human ?? _owner, "device-1", now, now.AddMinutes(5), new byte[16]),
            [1, 2, 3]);
    }

    private static OwnerSignedWideningGate Gate(OwnerSignatureVerdict verdict, params IWideningBiometricCheck[] bio) =>
        new(new FakeVerifier(verdict), TimeProvider.System, bio);

    [Fact]
    public async Task A_valid_signed_approval_for_this_change_passes()
    {
        var approved = await Gate(OwnerSignatureVerdict.Valid).ApproveAsync(Request(Signed()), Ct);
        Assert.True(approved);
    }

    [Fact]
    public async Task No_approval_is_refused_without_asking_the_verifier()
    {
        var verifier = new FakeVerifier(OwnerSignatureVerdict.Valid);
        var gate = new OwnerSignedWideningGate(verifier, TimeProvider.System, []);
        Assert.False(await gate.ApproveAsync(Request(null), Ct));
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(OwnerSignatureVerdict.BadSignature)]
    [InlineData(OwnerSignatureVerdict.UnknownDevice)]
    [InlineData(OwnerSignatureVerdict.RevokedDevice)]
    [InlineData(OwnerSignatureVerdict.NotTheOwner)]
    [InlineData(OwnerSignatureVerdict.Expired)]
    [InlineData(OwnerSignatureVerdict.NonceReplayed)]
    public async Task Any_verifier_refusal_refuses(OwnerSignatureVerdict verdict) =>
        Assert.False(await Gate(verdict).ApproveAsync(Request(Signed()), Ct));

    [Fact]
    public async Task An_approval_for_another_action_is_refused() =>
        Assert.False(await Gate(OwnerSignatureVerdict.Valid).ApproveAsync(Request(Signed(action: "pr.merge")), Ct));

    [Fact]
    public async Task An_approval_signed_by_someone_else_is_refused() =>
        Assert.False(await Gate(OwnerSignatureVerdict.Valid).ApproveAsync(Request(Signed(human: Guid.NewGuid())), Ct));

    [Fact]
    public async Task An_approval_for_a_different_change_is_refused()
    {
        var other = new GrantWideningRequest(new RoleActor(_owner, PrincipalKind.Human), "x", _role, _person, [new Grant(Capabilities.Spend, Scope.Company)]);
        var approval = Signed(hash: WideningArgs.Hash(other));
        Assert.False(await Gate(OwnerSignatureVerdict.Valid).ApproveAsync(Request(approval), Ct));
    }

    [Fact]
    public async Task Agents_never_pass() =>
        Assert.False(await Gate(OwnerSignatureVerdict.Valid).ApproveAsync(Request(Signed(), PrincipalKind.Agent), Ct));

    [Fact]
    public async Task A_registered_biometric_check_must_also_pass()
    {
        Assert.True(await Gate(OwnerSignatureVerdict.Valid, new Biometric(true)).ApproveAsync(Request(Signed()), Ct));
        Assert.False(await Gate(OwnerSignatureVerdict.Valid, new Biometric(true), new Biometric(false)).ApproveAsync(Request(Signed()), Ct));
    }

    [Fact]
    public void The_hash_ignores_grant_order_and_binds_every_field()
    {
        var a = Request(null);
        var reordered = a with { Added = [.. _added.Reverse()] };
        Assert.Equal(WideningArgs.Hash(a), WideningArgs.Hash(reordered));
        Assert.NotEqual(WideningArgs.Hash(a), WideningArgs.Hash(a with { RoleId = Guid.NewGuid() }));
        Assert.NotEqual(WideningArgs.Hash(a), WideningArgs.Hash(a with { PrincipalId = null }));
        Assert.NotEqual(WideningArgs.Hash(a), WideningArgs.Hash(a with { Added = [_added[0]] }));
    }
}
