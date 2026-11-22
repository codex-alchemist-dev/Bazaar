using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Enrolment;
using Bazaar.DeviceApproval;

namespace Bazaar.Client.DeviceEnrolment.Tests;

public sealed class DeviceEnrolmentClientTests
{
    private static readonly Guid _human = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _device = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Session : IEnrolmentSession
    {
        public Guid HumanId => _human;
        public Guid DeviceId => _device;
        public string DeviceClass => "windows";
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("tok");
    }

    private sealed class Keys(bool throws = false) : IApprovalKeyProvisioner
    {
        public byte[] Spki { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo();
        public byte[]? Nonce { get; private set; }

        public ValueTask<ProvisionedApprovalKey> ProvisionAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken)
        {
            if (throws)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.NoTpm, "no tpm");
            }

            Nonce = nonce.ToArray();
            return ValueTask.FromResult(new ProvisionedApprovalKey(Spki, [[1, 2]], keyName));
        }
    }

    private sealed class Credentials(bool cancel = false) : IPlatformCredentialCreator
    {
        public byte[]? Challenge { get; private set; }

        public ValueTask<PlatformCredential?> CreateAsync(byte[] challenge, Guid humanId, Guid deviceId, CancellationToken cancellationToken)
        {
            Challenge = challenge;
            return ValueTask.FromResult<PlatformCredential?>(cancel ? null : new PlatformCredential([9], [8], [7]));
        }
    }

    private sealed class Starter(EnrolmentStarter? value) : IEnrolmentStarterSource
    {
        public ValueTask<EnrolmentStarter?> GetAsync(Guid newDeviceId, byte[] approvalPublicKeySpki, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }

    private sealed class Handler(HttpStatusCode complete, string completeBody = "{}") : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? Bearer { get; private set; }
        public EnrolmentCompleteDto? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bearer = request.Headers.Authorization?.Parameter;
            if (request.RequestUri.AbsolutePath == EnrolmentRoutes.Challenge)
            {
                var dto = new EnrolmentChallengeDto(Convert.ToBase64String(new byte[32]), DateTimeOffset.UnixEpoch);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(dto, EnrolmentContractsJsonContext.Default.EnrolmentChallengeDto));
            }

            Body = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), EnrolmentContractsJsonContext.Default.EnrolmentCompleteDto);
            return Json(complete, completeBody);
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static DeviceEnrolmentClient Client(Handler handler, Keys keys, Credentials credentials, EnrolmentStarter? starter) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new Session(), keys, credentials, new Starter(starter), "bazaar-approval");

    [Fact]
    public async Task A_complete_run_posts_the_key_the_credential_and_the_code_with_the_bearer_token()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"enrolled":true,"reason":"enrolled","credentialAttested":true}""");
        var keys = new Keys();
        var credentials = new Credentials();

        var result = await Client(handler, keys, credentials, EnrolmentStarter.FromConsoleCode("ABCDEFGHJK")).EnrolAsync(Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.CredentialAttested);
        Assert.Equal([EnrolmentRoutes.Challenge, EnrolmentRoutes.Complete], handler.Paths);
        Assert.Equal("tok", handler.Bearer);
        Assert.Equal(_device, handler.Body!.DeviceId);
        Assert.Equal("windows", handler.Body.DeviceClass);
        Assert.Equal("ABCDEFGHJK", handler.Body.ConsoleCode);
        Assert.Null(handler.Body.Approval);
        Assert.Equal(Convert.ToBase64String(keys.Spki), handler.Body.ApprovalPublicKey);
        Assert.Single(handler.Body.ApprovalKeyAttestation);
    }

    [Fact]
    public async Task The_credential_challenge_is_the_shared_formula_over_the_nonce_and_the_new_key()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"enrolled":true,"reason":"enrolled","credentialAttested":false}""");
        var keys = new Keys();
        var credentials = new Credentials();

        await Client(handler, keys, credentials, EnrolmentStarter.FromConsoleCode("ABCDEFGHJK")).EnrolAsync(Ct);

        Assert.Equal(new byte[32], keys.Nonce);
        Assert.Equal(EnrolmentChallengeFormula.Compute(new byte[32], _human, _device, keys.Spki), credentials.Challenge);
    }

    [Fact]
    public async Task No_hardware_key_a_cancelled_credential_or_a_missing_starter_send_nothing()
    {
        var noKey = new Handler(HttpStatusCode.OK);
        Assert.Equal(EnrolmentClientOutcome.NoHardwareKey, (await Client(noKey, new Keys(throws: true), new Credentials(), EnrolmentStarter.FromConsoleCode("X")).EnrolAsync(Ct)).Outcome);
        Assert.Equal([EnrolmentRoutes.Challenge], noKey.Paths);

        var noCredential = new Handler(HttpStatusCode.OK);
        Assert.Equal(EnrolmentClientOutcome.Cancelled, (await Client(noCredential, new Keys(), new Credentials(cancel: true), EnrolmentStarter.FromConsoleCode("X")).EnrolAsync(Ct)).Outcome);
        Assert.Equal([EnrolmentRoutes.Challenge], noCredential.Paths);

        var noStarter = new Handler(HttpStatusCode.OK);
        Assert.Equal(EnrolmentClientOutcome.Cancelled, (await Client(noStarter, new Keys(), new Credentials(), null).EnrolAsync(Ct)).Outcome);
        Assert.Equal([EnrolmentRoutes.Challenge], noStarter.Paths);

        var both = new Handler(HttpStatusCode.OK);
        var ambiguous = new EnrolmentStarter("X", new EnrolmentApprovalDto("device.enrol", Guid.Empty, "", _human, "d", default, default, "", "", new EnrolmentBiometricDto(1, "", "", "", "")));
        Assert.Equal(EnrolmentClientOutcome.Cancelled, (await Client(both, new Keys(), new Credentials(), ambiguous).EnrolAsync(Ct)).Outcome);
        Assert.Equal([EnrolmentRoutes.Challenge], both.Paths);
    }

    [Fact]
    public async Task A_server_refusal_surfaces_its_reason_and_a_server_error_is_unavailable()
    {
        var refused = new Handler(HttpStatusCode.Forbidden, """{"enrolled":false,"reason":"approval_key_not_hardware","credentialAttested":false}""");
        var result = await Client(refused, new Keys(), new Credentials(), EnrolmentStarter.FromConsoleCode("X")).EnrolAsync(Ct);
        Assert.Equal(EnrolmentClientOutcome.Refused, result.Outcome);
        Assert.Equal("approval_key_not_hardware", result.Reason);

        var down = new Handler(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(EnrolmentClientOutcome.Unavailable, (await Client(down, new Keys(), new Credentials(), EnrolmentStarter.FromConsoleCode("X")).EnrolAsync(Ct)).Outcome);

        var garbage = new Handler(HttpStatusCode.OK, "not json");
        Assert.Equal(EnrolmentClientOutcome.Unavailable, (await Client(garbage, new Keys(), new Credentials(), EnrolmentStarter.FromConsoleCode("X")).EnrolAsync(Ct)).Outcome);
    }
}
