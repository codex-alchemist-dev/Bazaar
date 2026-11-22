using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.TalentPool.Avalonia;
using Bazaar.Server.TalentPool;
using Bazaar.Server.TalentPool.Component;

namespace Bazaar.Client.TalentPool.Tests;

public sealed class ToolGrantTests : IDisposable
{
    private static readonly Guid _owner = Guid.Parse("0f0f0f0f-0000-4000-8000-000000000001");
    private static readonly string[] _tools = ["shell", "browser"];

    private readonly Signer _signer = new();
    private readonly Sender _sender = new();
    private static ToolGrantCard Card(string[]? tools = null) => new("code-reviewer", 1, "Code Reviewer", tools ?? _tools);

    public void Dispose() => _signer.Dispose();

    private ToolGrantGate Gate(BiometricOutcome outcome = BiometricOutcome.Succeeded) => new(new Biometric(outcome), _signer, _sender, _owner);

    [Fact]
    public async Task Approve_signs_the_owner_payload_and_sends_it_once()
    {
        var gate = Gate();
        var result = await gate.ApproveAsync(Card(), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);

        Assert.True(result.IsSent);
        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("code-reviewer", sent.TemplateId);
        Assert.Equal(ToolGrantGate.Action, sent.Payload.Action);
        Assert.Equal(_owner, sent.Payload.HumanId);
        Assert.True(OwnerSignatureCrypto.Verify(_signer.PublicKeySpki, OwnerSignedPayload.Build(sent.Payload), sent.Signature));

        var again = await gate.ApproveAsync(Card(), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);
        Assert.Equal(ToolGrantRefusal.AlreadyDecided, again.Refusal);
        Assert.Single(_sender.Sent);
    }

    [Theory]
    [InlineData(ApprovalInputSource.Dictation)]
    [InlineData(ApprovalInputSource.Other)]
    public async Task Only_the_card_button_counts(ApprovalInputSource source)
    {
        var result = await Gate().ApproveAsync(Card(), source, TestContext.Current.CancellationToken);
        Assert.Equal(ToolGrantRefusal.NotFromCard, result.Refusal);
        Assert.Empty(_sender.Sent);
        Assert.Empty(_signer.Signed);
    }

    [Theory]
    [InlineData(BiometricOutcome.Cancelled, ToolGrantRefusal.BiometricCancelled)]
    [InlineData(BiometricOutcome.Failed, ToolGrantRefusal.BiometricFailed)]
    [InlineData(BiometricOutcome.Unavailable, ToolGrantRefusal.BiometricFailed)]
    public async Task No_biometric_signs_and_sends_nothing_and_allows_a_retry(BiometricOutcome outcome, ToolGrantRefusal expected)
    {
        var failing = Gate(outcome);
        var result = await failing.ApproveAsync(Card(), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Refusal);
        Assert.Empty(_signer.Signed);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_card_with_no_tools_is_invalid()
    {
        var result = await Gate().ApproveAsync(Card([]), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);
        Assert.Equal(ToolGrantRefusal.InvalidCard, result.Refusal);
    }

    [Fact]
    public async Task A_failed_send_is_final_and_not_retried()
    {
        _sender.Fails = true;
        var gate = Gate();
        Assert.Equal(ToolGrantRefusal.SendFailed, (await gate.ApproveAsync(Card(), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken)).Refusal);
        Assert.Equal(ToolGrantRefusal.AlreadyDecided, (await gate.ApproveAsync(Card(), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken)).Refusal);
    }

    [Fact]
    public void Args_hash_matches_the_servers()
    {
        Assert.Equal(TalentToolGrantApprover.ArgsHashFor("code-reviewer@1", ["shell", "browser"]), ToolGrantGate.ArgsHashFor("code-reviewer@1", ["browser", "shell"]));
        Assert.Equal(TalentToolGrantApprover.Action, ToolGrantGate.Action);
    }

    [Fact]
    public async Task The_server_accepts_what_the_phone_signs()
    {
        var store = new InMemoryTalentTemplateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.AddAsync(new TalentTemplate("code-reviewer", 1, new SoulManifest("Code Reviewer", "p", _tools, ["anthropic:claude-haiku-5-5"], []), new string('a', 64), "a", DateTimeOffset.UnixEpoch), ct);
        var keys = new Keys(new OwnerDeviceKey(_owner.ToString("D"), _signer.DeviceId, _signer.PublicKeySpki));
        var recorder = new Recorder();
        var approver = new TalentToolGrantApprover(store, new OwnerSignatureVerifier(keys, new InMemoryOwnerNonceStore()), recorder, TimeProvider.System);

        await Gate().ApproveAsync(Card(), ApprovalInputSource.CardButton, ct);
        var sent = _sender.Sent[0];

        Assert.Equal(ToolGrantApprovalResult.Approved, await approver.ApproveAsync(sent.TemplateId, sent.Version, sent.Payload, sent.Signature, ct));
        Assert.Equal("code-reviewer@1", Assert.Single(recorder.Recorded));
        // A replay of the same signed payload is refused by the nonce store.
        Assert.Equal(ToolGrantApprovalResult.SignatureRefused, await approver.ApproveAsync(sent.TemplateId, sent.Version, sent.Payload, sent.Signature, ct));
    }

    [AvaloniaFact]
    public void Card_shows_the_tools_and_approve_works_and_there_is_no_deny()
    {
        var vm = new ToolGrantCardViewModel(Gate(), Card());
        var window = new Window { Width = 400, Height = 600, DataContext = vm, Content = new ToolGrantCardView() };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Grant tools to Code Reviewer (version 1)", texts);
        Assert.Contains("shell", texts);
        Assert.Contains("browser", texts);
        Assert.Single(window.GetVisualDescendants().OfType<Button>());

        vm.ApproveAsync().GetAwaiter().GetResult();
        Assert.Equal("Tool grant approved.", vm.Notice);
        Assert.False(vm.CanApprove);
    }

    private sealed class Biometric(BiometricOutcome outcome) : IBiometricPrompt
    {
        public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(outcome);
    }

    private sealed class Signer : IDeviceApprovalSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string DeviceId => "device-1";
        public List<byte[]> Signed { get; } = [];
        public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();
        public void Dispose() => _key.Dispose();
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            Signed.Add(canonicalPayload.ToArray());
            return ValueTask.FromResult(_key.SignData(canonicalPayload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    private sealed class Sender : IToolGrantSender
    {
        public bool Fails { get; set; }
        public List<(string TemplateId, int Version, OwnerPayload Payload, byte[] Signature)> Sent { get; } = [];
        public ValueTask SendAsync(string templateId, int version, OwnerPayload payload, byte[] signature, CancellationToken cancellationToken)
        {
            if (Fails) { throw new InvalidOperationException("offline"); }
            Sent.Add((templateId, version, payload, signature));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Keys(OwnerDeviceKey key) : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default) => Task.FromResult<OwnerDeviceKey?>(deviceId == key.DeviceId ? key : null);
    }

    private sealed class Recorder : IToolGrantRecorder
    {
        public List<string> Recorded { get; } = [];
        public Task RecordAsync(string hireKey, IReadOnlyList<string> tools, string approvedBy, CancellationToken ct) { Recorded.Add(hireKey); return Task.CompletedTask; }
    }
}
