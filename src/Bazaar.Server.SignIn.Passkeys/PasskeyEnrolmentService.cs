using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Bazaar.Server.SignIn.Passkeys;

/// <summary>
/// Passkey enrolment for a person who is already signed in (doc 32 section 4, owner question 3 default: no approval step). The person
/// is whoever the access token names; nothing in the body is trusted. The creation options are held server side, one per person and
/// single use for 5 minutes, and the attestation is checked by the <c>Fido2</c> library against them. Attestation is not requested
/// (<c>none</c>): the passkey proves the person to this page only, not the make of the device. A person may hold at most
/// <see cref="MaxPasskeysPerPerson"/> passkeys. A person's first passkey on an instance with no signed-in owner is a different
/// bootstrap (a console enrolment code, doc 32 section 5b) and is OPEN pending owner question 4; it is not built here.
/// </summary>
public sealed class PasskeyEnrolmentService : IPasskeyEnrolment
{
    /// <summary>The most passkeys one person may hold.</summary>
    public const int MaxPasskeysPerPerson = 10;

    private const int _maxAttestationChars = 32 * 1024;
    private const int _maxPending = 1024;
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private readonly IPasskeyCredentialStore _store;
    private readonly IEnrolmentCaller _caller;
    private readonly Fido2Configuration _config;
    private readonly Fido2 _fido;
    private readonly TimeProvider _time;
    private readonly IAuditLog? _audit;
    private readonly ConcurrentDictionary<Guid, (string Options, DateTimeOffset Expiry)> _pending = new();

    /// <summary>Creates the service.</summary>
    public PasskeyEnrolmentService(IPasskeyCredentialStore store, IEnrolmentCaller caller, PasskeyOptions options, TimeProvider time, IAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _caller = caller;
        _time = time;
        _audit = audit;
        _config = new Fido2Configuration
        {
            RPID = options.RpId,
            RPName = options.DisplayName,
            Origins = new HashSet<string>(options.Origins, StringComparer.Ordinal),
        };
        _fido = new Fido2(_config);
    }

    /// <inheritdoc />
    public async Task<string?> BeginAsync(string bearerToken, CancellationToken ct = default)
    {
        var person = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
        if (person is null)
        {
            return null;
        }

        var existing = await _store.CredentialIdsAsync(person.HumanId, ct).ConfigureAwait(false);
        if (existing.Count >= MaxPasskeysPerPerson)
        {
            return null;
        }

        var user = new Fido2User { Id = person.HumanId.ToByteArray(), Name = person.DisplayName, DisplayName = person.DisplayName };
        var options = _fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var json = options.ToJson();
        var now = _time.GetUtcNow();
        Sweep(now);
        if (_pending.Count >= _maxPending && !_pending.ContainsKey(person.HumanId))
        {
            return null;
        }

        _pending[person.HumanId] = (json, now + _lifetime);
        return json;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string bearerToken, string attestationJson, CancellationToken ct = default)
    {
        var person = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
        if (person is null || string.IsNullOrEmpty(attestationJson) || attestationJson.Length > _maxAttestationChars)
        {
            return false;
        }

        // Single use: take the options out before checking, so a failed attempt cannot be retried against the same challenge.
        if (!_pending.TryRemove(person.HumanId, out var held) || held.Expiry <= _time.GetUtcNow())
        {
            return false;
        }

        try
        {
            var options = CredentialCreateOptions.FromJson(held.Options);
            var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson);
            if (options is null || response is null)
            {
                return false;
            }

            var result = await _fido.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (p, token) =>
                        !(await _store.CredentialIdsAsync(person.HumanId, token).ConfigureAwait(false)).Any(id => id.AsSpan().SequenceEqual(p.CredentialId)),
                },
                ct).ConfigureAwait(false);

            var credential = new PasskeyCredential(Guid.CreateVersion7(), person.HumanId, result.Id, result.PublicKey, result.SignCount);
            if (!await _store.AddAsync(credential, ct).ConfigureAwait(false))
            {
                return false;
            }

            await AuditAsync(person.HumanId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<EnrolmentPerson?> ResolveAsync(string bearerToken, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(bearerToken) ? null : await _caller.ResolveAsync(bearerToken, ct).ConfigureAwait(false);

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, value) in _pending)
        {
            if (value.Expiry <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }
    }

    private async Task AuditAsync(Guid humanId, CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            await _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), humanId.ToString("D"), "signin.passkey.enrolled", "{}"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The passkey is stored; a refused audit entry is not a reason to hide it from its owner.
        }
    }
}
