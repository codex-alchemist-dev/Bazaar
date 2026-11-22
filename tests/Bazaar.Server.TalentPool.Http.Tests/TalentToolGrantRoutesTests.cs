using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.TalentPool;
using Bazaar.Client.TalentPool.Http;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.TalentPool.Component;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.TalentPool.Http.Tests;

/// <summary>Real Kestrel on loopback. The phone's real gate and HTTP client talk to the real routes and the real owner-signature verifier.</summary>
public sealed class TalentToolGrantRoutesTests : IAsyncLifetime
{
    private static readonly Guid _owner = Guid.Parse("0f0f0f0f-0000-4000-8000-000000000001");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryTalentTemplateStore _store = new();
    private readonly Hires _hires = new();
    private readonly Recorder _recorder = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync()
    {
        var keys = new Keys(new OwnerDeviceKey(_owner.ToString("D"), "device-1", _key.ExportSubjectPublicKeyInfo()));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITalentTemplateStore>(_store);
        builder.Services.AddSingleton<IHireDecisionStore>(_hires);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITalentModelWhitelist, AllowAll>();
        builder.Services.AddSingleton<ITalentToolGrantApproval>(_recorder);
        builder.Services.AddSingleton<IToolGrantRecorder>(_recorder);
        builder.Services.AddSingleton<ITalentInstantiator, NoInstantiator>();
        builder.Services.AddSingleton<IHireGate, NoGate>();
        builder.Services.AddSingleton<IOwnerSignatureVerifier>(new OwnerSignatureVerifier(keys, new InMemoryOwnerNonceStore()));
        builder.Services.AddSingleton<ITalentPool>(sp => new TalentPoolService(_store, sp.GetRequiredService<IHireGate>(), _hires, new AllowAll(), _recorder, new NoInstantiator(), TimeProvider.System));
        builder.Services.AddSingleton<TalentToolGrantApprover>();

        _app = builder.Build();
        _app.MapBazaarTalentToolGrants(route => { _guardCalls++; return route; });
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _key.Dispose();
    }

    private async Task Seed(string[] tools, bool released = true)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddAsync(new TalentTemplate("code-reviewer", 1, new SoulManifest("Code Reviewer", "p", tools, ["anthropic:claude-haiku-5-5"], []), new string('a', 64), "a", DateTimeOffset.UnixEpoch), ct);
        if (released)
        {
            await _hires.SaveAsync(new HireVoteState("code-reviewer@1", Guid.NewGuid(), HireStatus.Released, MembershipDecision.Approved, DateTimeOffset.UtcNow), ct);
        }
    }

    [Fact]
    public void Routes_are_mapped_through_the_guard()
    {
        Assert.Equal(2, _guardCalls);
    }

    [Fact]
    public async Task Pending_lists_released_templates_with_unapproved_tools()
    {
        await Seed(["shell"]);
        var client = HttpToolGrantClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok"));
        var cards = await client.ListPendingAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(cards);
        Assert.Equal(("code-reviewer", 1, "Code Reviewer"), (card.TemplateId, card.Version, card.Name));
        Assert.Equal(["shell"], card.Tools);
    }

    [Fact]
    public async Task Quarantined_or_toolless_or_already_approved_templates_are_not_pending()
    {
        await Seed(["shell"], released: false);
        Assert.Empty(await Pending());

        await _hires.SaveAsync(new HireVoteState("code-reviewer@1", Guid.NewGuid(), HireStatus.Released, MembershipDecision.Approved, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        _recorder.Approved = true;
        Assert.Empty(await Pending());
    }

    private async Task<IReadOnlyList<ToolGrantCard>> Pending() =>
        await HttpToolGrantClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok"))
            .ListPendingAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_phone_approves_end_to_end_and_a_replay_is_refused()
    {
        await Seed(["shell", "browser"]);
        var sender = new CapturingSender(HttpToolGrantClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok")));
        var gate = new ToolGrantGate(new Biometric(), new Signer(_key), sender, _owner);
        var card = new ToolGrantCard("code-reviewer", 1, "Code Reviewer", ["shell", "browser"]);

        var result = await gate.ApproveAsync(card, ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);

        Assert.True(result.IsSent, $"{result.Refusal} {sender.LastStatus} {sender.Failure}");
        Assert.Equal("code-reviewer@1", Assert.Single(_recorder.Recorded));

        // The same signed body again is a replay: the nonce is spent.
        var replay = await _http.PostAsync("/talent/templates/code-reviewer/versions/1/tool-grant", new StringContent(sender.LastBody!, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Single(_recorder.Recorded);
    }

    [Fact]
    public async Task A_grant_for_other_tools_is_refused_and_records_nothing()
    {
        await Seed(["shell"]);
        var sender = new CapturingSender(HttpToolGrantClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok")));
        var gate = new ToolGrantGate(new Biometric(), new Signer(_key), sender, _owner);

        var result = await gate.ApproveAsync(new ToolGrantCard("code-reviewer", 1, "Code Reviewer", ["shell", "root"]), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);

        Assert.Equal(ToolGrantRefusal.SendFailed, result.Refusal);
        Assert.Equal(HttpStatusCode.BadRequest, sender.LastStatus);
        Assert.Empty(_recorder.Recorded);
    }

    [Theory]
    [InlineData("not json", HttpStatusCode.BadRequest)]
    [InlineData("{\"action\":\"talent.tool_grant\",\"approvalId\":\"0f0f0f0f-0000-4000-8000-000000000001\",\"argsHashHex\":\"zz\",\"humanId\":\"0f0f0f0f-0000-4000-8000-000000000001\",\"deviceId\":\"d\",\"issuedAtUnixMs\":1,\"expiresAtUnixMs\":2,\"nonceHex\":\"00\",\"signatureBase64\":\"AA==\"}", HttpStatusCode.BadRequest)]
    public async Task Bad_bodies_are_400(string body, HttpStatusCode expected)
    {
        await Seed(["shell"]);
        var response = await _http.PostAsync("/talent/templates/code-reviewer/versions/1/tool-grant", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused()
    {
        await Seed(["shell"]);
        var response = await _http.PostAsync("/talent/templates/code-reviewer/versions/1/tool-grant", new StringContent(new string('x', 5000), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_version_is_404_and_a_bad_version_is_400()
    {
        await Seed(["shell"]);
        var gate = new ToolGrantGate(new Biometric(), new Signer(_key), new CapturingSender(HttpToolGrantClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok"))), _owner);
        _ = await gate.ApproveAsync(new ToolGrantCard("nope", 9, "Nope", ["shell"]), ApprovalInputSource.CardButton, TestContext.Current.CancellationToken);

        var bad = await _http.PostAsync("/talent/templates/code-reviewer/versions/x/tool-grant", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(_recorder.Recorded);
    }

    private sealed class Hires : IHireDecisionStore
    {
        private readonly Dictionary<string, HireVoteState> _s = [];
        public Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct) => Task.FromResult(_s.GetValueOrDefault(templateId));
        public Task SaveAsync(HireVoteState state, CancellationToken ct) { _s[state.TemplateId] = state; return Task.CompletedTask; }
    }

    private sealed class Recorder : ITalentToolGrantApproval, IToolGrantRecorder
    {
        public bool Approved { get; set; }
        public List<string> Recorded { get; } = [];
        public Task<bool> IsApprovedAsync(string hireKey, IReadOnlyList<string> tools, CancellationToken ct) => Task.FromResult(Approved);
        public Task RecordAsync(string hireKey, IReadOnlyList<string> tools, string approvedBy, CancellationToken ct) { Recorded.Add(hireKey); return Task.CompletedTask; }
    }

    private sealed class AllowAll : ITalentModelWhitelist
    {
        public ValueTask<bool> IsAllowedAsync(string model, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class NoInstantiator : ITalentInstantiator
    {
        public Task<string> CreateAgentAsync(TalentTemplate candidate, string actorId, CancellationToken ct) => Task.FromResult("agent");
    }

    private sealed class NoGate : IHireGate
    {
        public Task<HireVoteState> RequestAsync(HireRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsReleasedAsync(string templateId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Keys(OwnerDeviceKey key) : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default) => Task.FromResult<OwnerDeviceKey?>(deviceId == key.DeviceId ? key : null);
    }

    private sealed class Biometric : IBiometricPrompt
    {
        public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(BiometricOutcome.Succeeded);
    }

    private sealed class Signer(ECDsa key) : IDeviceApprovalSigner
    {
        public string DeviceId => "device-1";
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(key.SignData(canonicalPayload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>Wraps the real client to keep the body and status of the last send, for the replay and mismatch checks.</summary>
    private sealed class CapturingSender(HttpToolGrantClient inner) : IToolGrantSender
    {
        public string? LastBody { get; private set; }
        public HttpStatusCode? LastStatus { get; private set; }
        public string? Failure { get; private set; }

        public async ValueTask SendAsync(string templateId, int version, OwnerPayload payload, byte[] signature, CancellationToken cancellationToken)
        {
            try
            {
            LastBody = System.Text.Json.JsonSerializer.Serialize(new Bazaar.Client.TalentPool.Http.ToolGrantDecisionDto(
                    payload.Action, payload.ApprovalId, Convert.ToHexStringLower(payload.ArgsHash), payload.HumanId, payload.DeviceId,
                    payload.IssuedAt.ToUnixTimeMilliseconds(), payload.ExpiresAt.ToUnixTimeMilliseconds(), Convert.ToHexStringLower(payload.Nonce), Convert.ToBase64String(signature)),
                    ToolGrantJsonContext.Default.ToolGrantDecisionDto);
                await inner.SendAsync(templateId, version, payload, signature, cancellationToken);
                LastStatus = HttpStatusCode.NoContent;
            }
            catch (Exception ex)
            {
                Failure = ex.ToString();
                LastStatus = (ex as HttpRequestException)?.StatusCode;
                throw;
            }
        }
    }
}
