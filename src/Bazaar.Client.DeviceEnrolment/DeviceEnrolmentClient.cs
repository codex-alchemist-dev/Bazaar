using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bazaar.Contracts.Enrolment;
using Bazaar.DeviceApproval;

namespace Bazaar.Client.DeviceEnrolment;

/// <summary>
/// The device side of doc 32 section 5b: ask for a nonce, create the hardware approval key and the platform credential (whose
/// challenge ties both to the nonce), get the starter proof and post the request. Nothing is sent unless every local step
/// produced its output, so a device without a hardware key or a credential never posts a weaker request. The caller builds the
/// <see cref="HttpClient"/> from the server endpoint, so the key-hash pin and the loopback-only plain-http rule already apply.
/// </summary>
public sealed class DeviceEnrolmentClient(
    HttpClient http,
    IEnrolmentSession session,
    IApprovalKeyProvisioner keys,
    IPlatformCredentialCreator credentials,
    IEnrolmentStarterSource starter,
    string approvalKeyName) : IDeviceEnrolmentClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IEnrolmentSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly IApprovalKeyProvisioner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly IPlatformCredentialCreator _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly IEnrolmentStarterSource _starter = starter ?? throw new ArgumentNullException(nameof(starter));
    private readonly string _keyName = !string.IsNullOrWhiteSpace(approvalKeyName) ? approvalKeyName : throw new ArgumentException("A key name is required.", nameof(approvalKeyName));

    /// <inheritdoc />
    public async Task<EnrolmentClientResult> EnrolAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            using var challengeReply = await PostAsync(EnrolmentRoutes.Challenge, token, content: null, cancellationToken).ConfigureAwait(false);
            if (!challengeReply.IsSuccessStatusCode)
            {
                return Failure(challengeReply);
            }

            var challenge = await challengeReply.Content.ReadFromJsonAsync(EnrolmentContractsJsonContext.Default.EnrolmentChallengeDto, cancellationToken).ConfigureAwait(false);
            if (challenge is null || !TryDecode(challenge.Nonce, out var nonce))
            {
                return Unavailable();
            }

            ProvisionedApprovalKey key;
            try
            {
                key = await _keys.ProvisionAsync(_keyName, nonce, cancellationToken).ConfigureAwait(false);
            }
            catch (ApprovalRefusedException)
            {
                return new EnrolmentClientResult(EnrolmentClientOutcome.NoHardwareKey, "no_hardware_key");
            }

            var webAuthnChallenge = EnrolmentChallengeFormula.Compute(nonce, _session.HumanId, _session.DeviceId, key.PublicKeySpki);
            var credential = await _credentials.CreateAsync(webAuthnChallenge, _session.HumanId, _session.DeviceId, cancellationToken).ConfigureAwait(false);
            if (credential is null)
            {
                return new EnrolmentClientResult(EnrolmentClientOutcome.Cancelled, "credential_cancelled");
            }

            var proof = await _starter.GetAsync(_session.DeviceId, key.PublicKeySpki, cancellationToken).ConfigureAwait(false);
            if (proof is null || (proof.ConsoleCode is null) == (proof.Approval is null))
            {
                return new EnrolmentClientResult(EnrolmentClientOutcome.Cancelled, "starter_cancelled");
            }

            var body = new EnrolmentCompleteDto(
                _session.DeviceId,
                _session.DeviceClass,
                challenge.Nonce,
                Convert.ToBase64String(key.PublicKeySpki),
                [.. key.Attestation.Select(Convert.ToBase64String)],
                Convert.ToBase64String(credential.CredentialId),
                Convert.ToBase64String(credential.AttestationObject),
                Convert.ToBase64String(credential.ClientDataJson),
                proof.ConsoleCode,
                proof.Approval);
            return await CompleteAsync(token, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    private async Task<EnrolmentClientResult> CompleteAsync(string token, EnrolmentCompleteDto body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, EnrolmentContractsJsonContext.Default.EnrolmentCompleteDto);
        using var reply = await PostAsync(EnrolmentRoutes.Complete, token, content, ct).ConfigureAwait(false);
        if ((int)reply.StatusCode >= 500)
        {
            return Unavailable();
        }

        var parsed = await ReadReplyAsync(reply, ct).ConfigureAwait(false);
        if (reply.IsSuccessStatusCode)
        {
            return parsed is { Enrolled: true }
                ? new EnrolmentClientResult(EnrolmentClientOutcome.Enrolled, parsed.Reason, parsed.CredentialAttested)
                : Unavailable();
        }

        return new EnrolmentClientResult(EnrolmentClientOutcome.Refused, parsed?.Reason ?? "refused");
    }

    private async Task<HttpResponseMessage> PostAsync(string route, string token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<EnrolmentReplyDto?> ReadReplyAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        try
        {
            return await reply.Content.ReadFromJsonAsync(EnrolmentContractsJsonContext.Default.EnrolmentReplyDto, ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static EnrolmentClientResult Failure(HttpResponseMessage reply) =>
        (int)reply.StatusCode >= 500
            ? Unavailable()
            : new EnrolmentClientResult(EnrolmentClientOutcome.Refused, reply.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized" : "forbidden");

    private static EnrolmentClientResult Unavailable() => new(EnrolmentClientOutcome.Unavailable, "unavailable");

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = new byte[value.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(value, bytes, out var written))
        {
            return false;
        }

        bytes = bytes.AsSpan(0, written).ToArray();
        return true;
    }
}
