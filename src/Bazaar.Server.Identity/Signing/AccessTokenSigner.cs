using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Identity.Signing;

/// <summary>
/// Production access-token signer (identity signing key, docs/design/14). One 256-bit HMAC-SHA256 key per rotation, held wrapped in the
/// store and unwrapped into memory only. A new live key is made when the current one is older than
/// <see cref="AccessTokenSigningOptions.SigningKeyRotationDays"/> (checked at issue) or on <see cref="RotateAsync"/>. A retired key
/// verifies until its retirement plus the grace period, then its wrap is cleared. Failure to unwrap fails closed: signing throws and
/// verification returns null.
/// </summary>
public sealed class AccessTokenSigner : IAccessTokenSigner, IDisposable
{
    /// <summary>The soul_dek scope of the access-token key.</summary>
    public const string Scope = "access-token";

    private const string Version = "v1";
    private const int KeyBytes = 32;
    private const int MacBytes = 32;

    /// <summary>How long a cached live key is trusted before its row is read again, so a rotation by another host is seen.</summary>
    private static readonly TimeSpan _liveRecheck = TimeSpan.FromSeconds(30);

    private readonly ISigningKeyStore _store;
    private readonly ISigningKeyWrapper _wrapper;
    private readonly TimeProvider _time;
    private readonly AccessTokenSigningOptions _options;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<Guid, CachedKey> _keys = [];
    private readonly SemaphoreSlim _rotateGate = new(1, 1);

    /// <summary>A key in memory. <see cref="ValidUntil"/> is null for a live key; <see cref="RecheckAt"/> applies only to live keys.</summary>
    private sealed record CachedKey(byte[] Key, DateTimeOffset? ValidUntil, DateTimeOffset RecheckAt);

    public AccessTokenSigner(ISigningKeyStore store, ISigningKeyWrapper wrapper, TimeProvider time, AccessTokenSigningOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wrapper);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _wrapper = wrapper;
        _time = time;
        _options = options ?? new AccessTokenSigningOptions();
    }

    /// <inheritdoc />
    public async ValueTask<string> SignAsync(Guid deviceId, Guid humanId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var live = await EnsureLiveAsync(cancellationToken).ConfigureAwait(false);
        var key = await ResolveKeyAsync(live.DekId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The live access-token signing key could not be unwrapped.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new AccessTokenPayload(deviceId, humanId, expiresAt.ToUnixTimeSeconds()),
            AccessTokenJsonContext.Default.AccessTokenPayload);
        var kid = live.DekId.ToString("N");
        var payloadB64 = Base64Url.EncodeToString(payload);
        var mac = HMACSHA256.HashData(key, SignedBytes(kid, payloadB64));
        return string.Concat(Version, ".", kid, ".", payloadB64, ".", Base64Url.EncodeToString(mac));
    }

    /// <inheritdoc />
    public async ValueTask<AccessTokenClaims?> VerifyAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Version) return null;
        if (!Guid.TryParseExact(parts[1], "N", out var kid) || parts[1] != kid.ToString("N")) return null;

        byte[] payload;
        byte[] mac;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[2]);
            mac = Base64Url.DecodeFromChars(parts[3]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (mac.Length != MacBytes) return null;

        var key = await ResolveKeyAsync(kid, cancellationToken).ConfigureAwait(false);
        if (key is null) return null;

        var expected = HMACSHA256.HashData(key, SignedBytes(parts[1], parts[2]));
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) return null;

        AccessTokenPayload? claims;
        try
        {
            claims = JsonSerializer.Deserialize(payload, AccessTokenJsonContext.Default.AccessTokenPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (claims is null || claims.Exp <= _time.GetUtcNow().ToUnixTimeSeconds()) return null;
        return new AccessTokenClaims(claims.Sub, claims.Hum);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _rotateGate.Dispose();
    }

    /// <summary>Makes a new live key now and retires the previous one. Used on demand; issue rotates on its own when the key is due.</summary>
    public async ValueTask RotateAsync(CancellationToken cancellationToken = default)
    {
        await _rotateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RotateUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _rotateGate.Release();
        }
    }

    /// <summary>Returns the live row, rotating first when there is none or it is due.</summary>
    private async ValueTask<SigningKeyRow> EnsureLiveAsync(CancellationToken cancellationToken)
    {
        await ClearExpiredAsync(cancellationToken).ConfigureAwait(false);
        var live = await _store.GetLiveAsync(Scope, cancellationToken).ConfigureAwait(false);
        if (live is not null && !IsDue(live)) return live;

        await _rotateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            live = await _store.GetLiveAsync(Scope, cancellationToken).ConfigureAwait(false);
            if (live is not null && !IsDue(live)) return live;
            return await RotateUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _rotateGate.Release();
        }
    }

    private async ValueTask<SigningKeyRow> RotateUnlockedAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var dekId = Guid.NewGuid();
        var associated = AssociatedData(dekId);
        var wrapped = await _wrapper.WrapAsync(key, associated, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);

        // When another host rotated first, the store returns its live row and our wrap is discarded; we sign with the winner's key.
        var live = await _store.RotateAsync(new NewSigningKey(dekId, Scope, wrapped, _wrapper.KekId, now), now, cancellationToken).ConfigureAwait(false);

        // Live entries cached before this rotation still say "live"; drop them so the next verify sees the retirement.
        lock (_cacheLock)
        {
            foreach (var (id, cached) in _keys.ToArray())
            {
                if (cached.ValidUntil is null) _keys.Remove(id);
            }
        }

        return live;
    }

    private bool IsDue(SigningKeyRow live) => live.CreatedAt.Add(TimeSpan.FromDays(_options.SigningKeyRotationDays)) <= _time.GetUtcNow();

    private ValueTask ClearExpiredAsync(CancellationToken cancellationToken) =>
        _store.ClearRetiredWrapsAsync(Scope, _time.GetUtcNow() - _options.Grace, cancellationToken);

    /// <summary>
    /// The unwrapped key for a dek_id, or null when the key is unknown, out of its grace period, under another KEK, or cannot be unwrapped.
    /// </summary>
    private async ValueTask<byte[]?> ResolveKeyAsync(Guid kid, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        lock (_cacheLock)
        {
            if (_keys.TryGetValue(kid, out var cached))
            {
                if (cached.ValidUntil is { } until)
                {
                    if (now < until) return cached.Key;
                    _keys.Remove(kid);
                    return null;
                }

                if (now < cached.RecheckAt) return cached.Key;
            }
        }

        var row = (await _store.ListAsync(Scope, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.DekId == kid);
        if (row is null || row.WrappedDek is null || row.KekId != _wrapper.KekId)
        {
            lock (_cacheLock) _keys.Remove(kid);
            return null;
        }

        DateTimeOffset? validUntil = row.RetiredAt is { } retired ? retired + _options.Grace : null;
        if (validUntil is { } limit && now >= limit)
        {
            lock (_cacheLock) _keys.Remove(kid);
            return null;
        }

        byte[] key;
        try
        {
            key = await _wrapper.UnwrapAsync(row.WrappedDek, AssociatedData(kid), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: an unwrap that fails refuses the key (verify returns null, sign throws in the caller).
            return null;
        }

        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }

        lock (_cacheLock)
        {
            _keys[kid] = new CachedKey(key, validUntil, now + _liveRecheck);
        }

        return key;
    }

    private static byte[] AssociatedData(Guid dekId) => dekId.ToByteArray();

    private static byte[] SignedBytes(string kid, string payloadB64) => Encoding.ASCII.GetBytes(string.Concat(Version, ".", kid, ".", payloadB64));

    /// <summary>The claims inside a token. Wire names follow the token format above.</summary>
    internal sealed record AccessTokenPayload(
        [property: JsonPropertyName("sub")] Guid Sub,
        [property: JsonPropertyName("hum")] Guid Hum,
        [property: JsonPropertyName("exp")] long Exp);
}

/// <summary>Source-generated JSON for the token payload (System.Text.Json source generation, CLAUDE.md).</summary>
[JsonSerializable(typeof(AccessTokenSigner.AccessTokenPayload))]
internal sealed partial class AccessTokenJsonContext : JsonSerializerContext;
