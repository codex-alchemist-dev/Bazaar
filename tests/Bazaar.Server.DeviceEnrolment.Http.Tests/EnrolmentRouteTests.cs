using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Enrolment;
using Bazaar.Server.Biometric;
using Bazaar.Server.DeviceEnrolment.Tests;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.DeviceEnrolment.Http.Tests;

/// <summary>The enrolment routes (doc 32 section 5b) over the real ceremony, with in-memory store and fake attestors.</summary>
public sealed class EnrolmentRouteTests
{
    private static readonly Guid _human = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _device = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");
    private static readonly Guid _device2 = Guid.Parse("0197a000-0000-7000-8000-0000000000d3");
    private static readonly Guid _approver = Guid.Parse("0197a000-0000-7000-8000-0000000000d2");
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeAuth : IConnectionAuthenticator
    {
        public Dictionary<string, AuthResult> Tokens { get; } = [];

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential is not null && Tokens.TryGetValue(credential.Value, out var r) ? r : AuthResult.Fail("no"));
    }

    private sealed class Rig : IAsyncDisposable
    {
        public MemoryStore Store { get; } = new();
        public MemoryAudit Audit { get; } = new();
        public FakeAuth Auth { get; } = new();
        public FakeTimeProvider Time { get; } = new(_now);
        public FakeOwnerSignature Owner { get; }
        public FakeBiometric Bio { get; }
        public DeviceEnrolment Enrolment { get; }
        public HttpClient Client { get; private set; } = null!;
        private WebApplication? _app;
        private readonly bool _register;

        public Rig(
            ApprovalKeyAttestationResult? key = null,
            OwnerSignatureVerdict owner = OwnerSignatureVerdict.Valid,
            BiometricVerdict bio = BiometricVerdict.Valid,
            bool register = true)
        {
            _register = register;
            Store.Devices.Add((_device, _human, DeviceClass.Windows));
            Store.Devices.Add((_device2, _human, DeviceClass.Windows));
            Auth.Tokens["owner"] = AuthResult.Success(new PrincipalRef(_human, "owner"), _device.ToString("D"));
            Auth.Tokens["owner2"] = AuthResult.Success(new PrincipalRef(_human, "owner"), _device2.ToString("D"));
            Auth.Tokens["coworker"] = AuthResult.Success(new PrincipalRef(_human, "human"), _device.ToString("D"));
            Auth.Tokens["nodevice"] = AuthResult.Success(new PrincipalRef(_human, "owner"));
            Owner = new FakeOwnerSignature(owner);
            Bio = new FakeBiometric(bio);
            Enrolment = new DeviceEnrolment(
                Store,
                new FakeKeyAttestor(key ?? new ApprovalKeyAttestationResult(true, true, true)),
                new FakeCredentialAttestor(new CredentialRegistration(true, true, Keys.P256(), new HashSet<BiometricFactor> { BiometricFactor.FaceDepth }, true, 0)),
                Owner,
                Bio,
                Audit);
        }

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddRouting();
            builder.Services.AddOptions<WireOptions>();
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.Services.AddSingleton<IConnectionAuthenticator>(Auth);
            builder.Services.AddSingleton<IEnrolmentAudit>(Audit);
            if (_register)
            {
                builder.Services.AddSingleton<IDeviceEnrolment>(Enrolment);
            }

            _app = builder.Build();
            _app.MapBazaarDeviceEnrolmentRoutes();
            await _app.StartAsync(Ct);
            Client = _app.GetTestClient();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }

        public async Task<HttpResponseMessage> PostAsync(string path, string? token, object? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (body is not null)
            {
                request.Content = body is string raw
                    ? new StringContent(raw, Encoding.UTF8, "application/json")
                    : JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            return await Client.SendAsync(request, Ct);
        }

        public async Task<string> NonceAsync(string token)
        {
            var reply = await PostAsync(EnrolmentRoutes.Challenge, token);
            reply.EnsureSuccessStatusCode();
            var dto = await reply.Content.ReadFromJsonAsync(EnrolmentContractsJsonContext.Default.EnrolmentChallengeDto, Ct);
            return dto!.Nonce;
        }

        public async Task<string> CodeAsync() => await Enrolment.IssueConsoleCodeAsync(_human, _now, Ct);
    }

    private static EnrolmentCompleteDto Complete(string nonce, Guid device, string? code, EnrolmentApprovalDto? approval = null, byte[]? key = null) =>
        new(device, "windows", nonce, Convert.ToBase64String(key ?? Keys.P256()), [Convert.ToBase64String([1, 2])],
            Convert.ToBase64String([9, 9]), Convert.ToBase64String([1]), Convert.ToBase64String([2]), code, approval);

    private static async Task<EnrolmentReplyDto?> ReplyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync(EnrolmentContractsJsonContext.Default.EnrolmentReplyDto, Ct);

    [Fact]
    public async Task First_device_enrols_and_ids_come_from_the_token()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner");

        var response = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, await rig.CodeAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await ReplyAsync(response);
        Assert.True(reply!.Enrolled);
        Assert.True(reply.CredentialAttested);
        var commit = Assert.Single(rig.Store.Commits);
        Assert.Equal(_human, commit.HumanId);
        Assert.Equal(_device, commit.DeviceId);
        Assert.NotNull(commit.ConsoleCodeHash);
    }

    [Fact]
    public async Task Callers_without_a_valid_owner_token_and_device_are_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.PostAsync(EnrolmentRoutes.Challenge, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.PostAsync(EnrolmentRoutes.Challenge, "bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.PostAsync(EnrolmentRoutes.Challenge, "coworker")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.PostAsync(EnrolmentRoutes.Challenge, "nodevice")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.PostAsync(EnrolmentRoutes.Complete, null, Complete("AA==", _device, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.PostAsync(EnrolmentRoutes.Complete, "coworker", Complete("AA==", _device, "x"))).StatusCode);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task Routes_answer_503_when_the_ceremony_is_not_registered()
    {
        await using var rig = new Rig(register: false);
        await rig.StartAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync(EnrolmentRoutes.Challenge, "owner")).StatusCode);
    }

    [Fact]
    public async Task A_replayed_nonce_is_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner");
        var code = await rig.CodeAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, code))).StatusCode);

        var replay = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, await rig.CodeAsync()));

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal("challenge_invalid", (await ReplyAsync(replay))!.Reason);
        Assert.Single(rig.Store.Commits);
    }

    [Fact]
    public async Task A_nonce_issued_to_another_device_is_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner2");

        var response = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, await rig.CodeAsync()));

        Assert.Equal("challenge_invalid", (await ReplyAsync(response))!.Reason);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task A_body_that_names_another_device_than_the_signed_in_one_is_refused_and_audited()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner");

        var response = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device2, await rig.CodeAsync()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("device_mismatch", (await ReplyAsync(response))!.Reason);
        Assert.Equal([EnrolmentVerdict.DeviceMismatch], rig.Audit.Refusals);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task An_attestation_that_does_not_verify_is_refused()
    {
        await using var rig = new Rig(key: new ApprovalKeyAttestationResult(false, false, false));
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner");

        var response = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, await rig.CodeAsync()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("approval_key_not_hardware", (await ReplyAsync(response))!.Reason);
        Assert.Empty(rig.Store.Commits);
    }

    [Fact]
    public async Task A_second_enrolment_without_owner_approval_is_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(await rig.NonceAsync("owner"), _device, await rig.CodeAsync()))).StatusCode);

        // A new device with no code, and one with a made-up code: the starter is refused.
        var noCode = await rig.PostAsync(EnrolmentRoutes.Complete, "owner2", Complete(await rig.NonceAsync("owner2"), _device2, "ABCDEFGHJK"));
        Assert.Equal("starter_refused", (await ReplyAsync(noCode))!.Reason);

        // An approval whose owner signature does not verify is refused too.
        await using var bad = new Rig(owner: OwnerSignatureVerdict.BadSignature);
        await bad.StartAsync();
        var refused = await bad.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(await bad.NonceAsync("owner"), _device, null, Approval()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("starter_refused", (await ReplyAsync(refused))!.Reason);
        Assert.Single(rig.Store.Commits);
        Assert.Empty(bad.Store.Commits);
    }

    [Fact]
    public async Task A_console_code_cannot_be_used_twice()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var code = await rig.CodeAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(await rig.NonceAsync("owner"), _device, code))).StatusCode);

        var reuse = await rig.PostAsync(EnrolmentRoutes.Complete, "owner2", Complete(await rig.NonceAsync("owner2"), _device2, code));

        Assert.Equal(HttpStatusCode.Forbidden, reuse.StatusCode);
        Assert.Equal("starter_refused", (await ReplyAsync(reuse))!.Reason);
        Assert.Single(rig.Store.Commits);
    }

    [Fact]
    public async Task The_same_device_cannot_enrol_twice()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(await rig.NonceAsync("owner"), _device, await rig.CodeAsync()))).StatusCode);

        var again = await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(await rig.NonceAsync("owner"), _device, await rig.CodeAsync()));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_enrolled", (await ReplyAsync(again))!.Reason);
    }

    [Fact]
    public async Task A_further_device_enrols_with_an_owner_signed_approval_and_needs_a_passing_biometric_check()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var key = Keys.P256();

        var ok = await rig.PostAsync(EnrolmentRoutes.Complete, "owner2", Complete(await rig.NonceAsync("owner2"), _device2, null, Approval(_device2, key), key));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var commit = Assert.Single(rig.Store.Commits);
        Assert.Null(commit.ConsoleCodeHash);
        Assert.True(rig.Bio.LastHighRisk);

        await using var noBio = new Rig(bio: BiometricVerdict.NotUserVerified);
        await noBio.StartAsync();
        var key2 = Keys.P256();
        var refused = await noBio.PostAsync(EnrolmentRoutes.Complete, "owner2", Complete(await noBio.NonceAsync("owner2"), _device2, null, Approval(_device2, key2), key2));
        Assert.Equal("starter_refused", (await ReplyAsync(refused))!.Reason);
        Assert.Empty(noBio.Store.Commits);
    }

    [Fact]
    public async Task Malformed_bodies_are_refused_with_400()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var nonce = await rig.NonceAsync("owner");
        var code = await rig.CodeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete("***", _device, code))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, code, Approval()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", Complete(nonce, _device, code) with { DeviceClass = "toaster" })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await rig.PostAsync(EnrolmentRoutes.Complete, "owner", new string('x', 200_000))).StatusCode);
        Assert.Empty(rig.Store.Commits);
    }

    private sealed class Session : Bazaar.Client.DeviceEnrolment.IEnrolmentSession
    {
        public Guid HumanId => _human;
        public Guid DeviceId => _device;
        public string DeviceClass => "windows";
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("owner");
    }

    private sealed class Provisioner : Bazaar.DeviceApproval.IApprovalKeyProvisioner
    {
        public ValueTask<Bazaar.DeviceApproval.ProvisionedApprovalKey> ProvisionAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Bazaar.DeviceApproval.ProvisionedApprovalKey(Keys.P256(), [[1]], keyName));
    }

    private sealed class Credential : Bazaar.Client.DeviceEnrolment.IPlatformCredentialCreator
    {
        public ValueTask<Bazaar.Client.DeviceEnrolment.PlatformCredential?> CreateAsync(byte[] challenge, Guid humanId, Guid deviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Bazaar.Client.DeviceEnrolment.PlatformCredential?>(new Bazaar.Client.DeviceEnrolment.PlatformCredential([9], [8], [7]));
    }

    private sealed class CodeStarter(string code) : Bazaar.Client.DeviceEnrolment.IEnrolmentStarterSource
    {
        public ValueTask<Bazaar.Client.DeviceEnrolment.EnrolmentStarter?> GetAsync(Guid newDeviceId, byte[] approvalPublicKeySpki, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Bazaar.Client.DeviceEnrolment.EnrolmentStarter?>(Bazaar.Client.DeviceEnrolment.EnrolmentStarter.FromConsoleCode(code));
    }

    [Fact]
    public async Task The_client_module_enrols_against_the_routes_and_a_reused_code_is_then_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var code = await rig.CodeAsync();
        var client = new Bazaar.Client.DeviceEnrolment.DeviceEnrolmentClient(rig.Client, new Session(), new Provisioner(), new Credential(), new CodeStarter(code), "bazaar-approval");

        var first = await client.EnrolAsync(Ct);
        var again = await client.EnrolAsync(Ct);

        Assert.True(first.Succeeded);
        Assert.Equal(Bazaar.Client.DeviceEnrolment.EnrolmentClientOutcome.Refused, again.Outcome);
        Assert.Single(rig.Store.Commits);
    }

    private static EnrolmentApprovalDto Approval(Guid? newDevice = null, byte[]? key = null)
    {
        var args = EnrolmentArgs.Hash(newDevice ?? _device, key ?? Keys.P256());
        return new EnrolmentApprovalDto(
            EnrolmentArgs.Action, Guid.NewGuid(), Convert.ToBase64String(args), _human, _approver.ToString("D"),
            _now, _now.AddMinutes(2), Convert.ToBase64String(new byte[16]), Convert.ToBase64String([7]),
            new EnrolmentBiometricDto(1, Convert.ToBase64String([1]), Convert.ToBase64String([2]), Convert.ToBase64String([3]), Convert.ToBase64String([4])));
    }
}
