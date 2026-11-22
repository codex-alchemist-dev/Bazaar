using Bazaar.Approvals;
using Bazaar.Erasure.Abstractions;

namespace Bazaar.Erasure.Tests;

public sealed class LegalHoldServiceTests
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

    private sealed class MemoryStore : ILegalHoldStore
    {
        public List<(Guid Id, LegalHoldScopeKind Kind, string Scope, string Reason, string Actor, string Key)> Created { get; } = [];
        public List<(Guid Id, string Reason, string Key)> Released { get; } = [];

        public Task CreateAsync(Guid holdId, LegalHoldScopeKind kind, string scopeValue, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct)
        {
            Created.Add((holdId, kind, scopeValue, reason, actor, signatureKeyId));
            return Task.CompletedTask;
        }

        public Task<bool> ReleaseAsync(Guid holdId, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct)
        {
            if (!Created.Any(c => c.Id == holdId) || Released.Any(r => r.Id == holdId))
                return Task.FromResult(false);
            Released.Add((holdId, reason, signatureKeyId));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<LegalHold>> ListActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LegalHold>>(
                [.. Created.Where(c => Released.All(r => r.Id != c.Id))
                    .Select(c => new LegalHold(c.Id, c.Kind, c.Scope, c.Reason, c.Actor, DateTimeOffset.UnixEpoch))]);
    }

    private static readonly Guid _owner = Guid.NewGuid();
    private static readonly Guid _person = Guid.NewGuid();

    private static (LegalHoldService Service, MemoryStore Store, FakeVerifier Verifier) Make(OwnerSignatureVerdict verdict = OwnerSignatureVerdict.Valid)
    {
        var store = new MemoryStore();
        var verifier = new FakeVerifier(verdict);
        return (new LegalHoldService(store, verifier, TimeProvider.System), store, verifier);
    }

    private static OwnerHoldApproval Approval(string action, byte[] hash, Guid? human = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new OwnerHoldApproval(
            new OwnerPayload(action, Guid.NewGuid(), hash, null, human ?? _owner, "device-1", now, now.AddMinutes(5), new byte[16]),
            [1, 2, 3]);
    }

    private static OwnerHoldApproval CreateApproval(string reason = "case 42", Guid? human = null) =>
        Approval(LegalHoldArgs.CreateAction, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, _person.ToString("D"), reason), human);

    [Fact]
    public async Task A_signed_approval_creates_a_hold_and_stores_the_signing_device()
    {
        var (svc, store, _) = Make();
        var outcome = await svc.CreateAsync(_owner, LegalHoldScopeKind.Person, _person.ToString("D").ToUpperInvariant(), " case 42 ", CreateApproval(), Ct);

        Assert.True(outcome.Succeeded);
        var c = Assert.Single(store.Created);
        Assert.Equal(outcome.HoldId, c.Id);
        Assert.Equal(_person.ToString("D"), c.Scope);
        Assert.Equal("case 42", c.Reason);
        Assert.Equal(_owner.ToString("D"), c.Actor);
        Assert.Equal("device-1", c.Key);
    }

    [Fact]
    public async Task No_approval_stores_nothing_and_does_not_call_the_verifier()
    {
        var (svc, store, verifier) = Make();
        var outcome = await svc.CreateAsync(_owner, LegalHoldScopeKind.Person, _person.ToString("D"), "case 42", null, Ct);

        Assert.Equal(LegalHoldRefusal.NotApproved, outcome.Refusal);
        Assert.Empty(store.Created);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(OwnerSignatureVerdict.BadSignature)]
    [InlineData(OwnerSignatureVerdict.UnknownDevice)]
    [InlineData(OwnerSignatureVerdict.RevokedDevice)]
    [InlineData(OwnerSignatureVerdict.NotTheOwner)]
    [InlineData(OwnerSignatureVerdict.Expired)]
    [InlineData(OwnerSignatureVerdict.NonceReplayed)]
    public async Task Any_verifier_refusal_stores_nothing(OwnerSignatureVerdict verdict)
    {
        var (svc, store, _) = Make(verdict);
        var outcome = await svc.CreateAsync(_owner, LegalHoldScopeKind.Person, _person.ToString("D"), "case 42", CreateApproval(), Ct);

        Assert.Equal(LegalHoldRefusal.SignatureRefused, outcome.Refusal);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task An_approval_for_another_action_signer_or_change_is_refused()
    {
        var (svc, store, _) = Make();
        var wrongAction = Approval(LegalHoldArgs.ReleaseAction, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, _person.ToString("D"), "case 42"));
        var otherPerson = Approval(LegalHoldArgs.CreateAction, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, Guid.NewGuid().ToString("D"), "case 42"));

        foreach (var approval in new[] { wrongAction, otherPerson, CreateApproval(human: Guid.NewGuid()), CreateApproval("different reason") })
        {
            var outcome = await svc.CreateAsync(_owner, LegalHoldScopeKind.Person, _person.ToString("D"), "case 42", approval, Ct);
            Assert.Equal(LegalHoldRefusal.NotApproved, outcome.Refusal);
        }

        Assert.Empty(store.Created);
    }

    [Theory]
    [InlineData(LegalHoldScopeKind.Person, "not-a-guid", "why")]
    [InlineData(LegalHoldScopeKind.Person, "00000000-0000-0000-0000-000000000000", "why")]
    [InlineData(LegalHoldScopeKind.Gc, "  ", "why")]
    [InlineData(LegalHoldScopeKind.Company, "acme", "  ")]
    public async Task An_unusable_scope_or_reason_is_refused_before_any_check(LegalHoldScopeKind kind, string scope, string reason)
    {
        var (svc, store, verifier) = Make();
        var outcome = await svc.CreateAsync(_owner, kind, scope, reason, CreateApproval(), Ct);

        Assert.Equal(LegalHoldRefusal.Invalid, outcome.Refusal);
        Assert.Empty(store.Created);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task A_release_needs_its_own_signature_and_ends_the_hold_once()
    {
        var (svc, store, _) = Make();
        var created = await svc.CreateAsync(_owner, LegalHoldScopeKind.Person, _person.ToString("D"), "case 42", CreateApproval(), Ct);
        var release = Approval(LegalHoldArgs.ReleaseAction, LegalHoldArgs.ReleaseHash(created.HoldId));

        Assert.Equal(LegalHoldRefusal.NotApproved, (await svc.ReleaseAsync(_owner, created.HoldId, "done", CreateApproval(), Ct)).Refusal);
        Assert.Single(await svc.ListActiveAsync(Ct));

        Assert.True((await svc.ReleaseAsync(_owner, created.HoldId, "done", release, Ct)).Succeeded);
        Assert.Empty(await svc.ListActiveAsync(Ct));
        Assert.Equal(LegalHoldRefusal.NotActive, (await svc.ReleaseAsync(_owner, created.HoldId, "done", release, Ct)).Refusal);
        Assert.Single(store.Released);
    }

    [Fact]
    public void The_hashes_bind_every_field()
    {
        var a = LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, "x", "r");
        Assert.NotEqual(a, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Gc, "x", "r"));
        Assert.NotEqual(a, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, "y", "r"));
        Assert.NotEqual(a, LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, "x", "s"));
        Assert.NotEqual(LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, "ab", "c"), LegalHoldArgs.CreateHash(LegalHoldScopeKind.Person, "a", "bc"));
    }
}
