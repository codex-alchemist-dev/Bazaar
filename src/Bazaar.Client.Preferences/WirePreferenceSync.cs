using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Preferences;

/// <summary>
/// Sends a preference change as <c>prefs.set</c> (doc 02). A <c>bad_request</c> or <c>not_found</c> answer is permanent and
/// becomes <see cref="PreferenceRejectedException"/>; any other failure (socket down, rate limit, server fault) is thrown as is
/// and the change stays pending.
/// </summary>
public sealed class WirePreferenceSync : IPreferenceSync
{
    private readonly IWireRequester _wire;

    public WirePreferenceSync(IWireRequester wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        _wire = wire;
    }

    public async Task PushAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await _wire.RequestAsync<PrefsSet, PrefsSetResult>(WireTypes.PrefsSet, new PrefsSet(key, value), cancellationToken).ConfigureAwait(false);
        }
        catch (WireRequestException ex) when (ex.Error.Code is WireErrorCodes.BadRequest or WireErrorCodes.NotFound)
        {
            throw new PreferenceRejectedException($"The server refused '{key}': {ex.Error.Message}", ex);
        }
    }
}
