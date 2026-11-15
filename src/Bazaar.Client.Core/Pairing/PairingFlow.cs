namespace Bazaar.Client.Core.Pairing;

/// <summary>
/// The device side of pairing (doc 32, section 3): starts a device authorization with the code the approver offered, polls
/// until the approver answers, and keeps only the refresh credential. The access token is never stored; it lives in the
/// <see cref="PairingGrant"/> the caller receives.
/// </summary>
public sealed class PairingFlow(IRefreshCredentialStore store, Func<Uri, PairingClient> clientFor)
{
    private readonly IRefreshCredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<Uri, PairingClient> _clientFor = clientFor ?? throw new ArgumentNullException(nameof(clientFor));

    /// <summary>
    /// Pairs this device with the server at <paramref name="baseAddress"/>. The <paramref name="userCode"/> is normalised
    /// (spaces and dashes removed, upper case) before it is sent. On success the refresh credential is saved and the grant is
    /// returned. A <see cref="PairingException"/> from the server propagates unchanged and nothing is saved.
    /// </summary>
    public async Task<PairingGrant> PairAsync(Uri baseAddress, string userCode, string label, string deviceClass, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The server address must be an absolute http or https URI.", nameof(baseAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        ArgumentNullException.ThrowIfNull(userCode);

        var normalised = Normalise(userCode);
        if (normalised.Length == 0)
        {
            throw new ArgumentException("The pairing code is empty.", nameof(userCode));
        }

        var client = _clientFor(baseAddress);
        var authorization = await client.StartAsync(normalised, label, deviceClass, cancellationToken).ConfigureAwait(false);
        var grant = await client.PollAsync(authorization, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(grant.RefreshToken, cancellationToken).ConfigureAwait(false);
        return grant;
    }

    /// <summary>
    /// Cold start: exchanges the stored refresh credential for a new grant (doc 32, section 5). Returns <c>null</c> when the device
    /// is not paired. On success the rotated refresh credential replaces the stored one. When the server refuses the credential
    /// (<c>invalid_grant</c>) the stored credential is cleared and <c>null</c> is returned, so the device must pair again. Transport
    /// failures and other errors propagate and leave the stored credential in place.
    /// </summary>
    public async Task<PairingGrant?> RestoreAsync(Uri baseAddress, string deviceClass, string clientBuild, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);

        var stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        try
        {
            var grant = await _clientFor(baseAddress).RefreshAsync(stored, deviceClass, clientBuild, cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(grant.RefreshToken, cancellationToken).ConfigureAwait(false);
            return grant;
        }
        catch (PairingException exception) when (exception.Code == PairingClient.InvalidGrantCode)
        {
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>True when a refresh credential is stored, which means the device has paired before.</summary>
    public async Task<bool> IsPairedAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Removes spaces, tabs and dashes, then upper-cases what is left, so "ab-cd 12" becomes "ABCD12".</summary>
    private static string Normalise(string userCode) =>
        new string(userCode.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
}
