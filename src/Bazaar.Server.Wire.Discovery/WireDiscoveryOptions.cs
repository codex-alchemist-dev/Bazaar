namespace Bazaar.Server.Wire.Discovery;

/// <summary>Operator settings for the discovery route (doc 90 sections 6 and 8, key <c>wire.discovery.enabled</c>).</summary>
public sealed class WireDiscoveryOptions
{
    /// <summary>When false both routes answer 404, as if the module were absent. Read on every request. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Route prefix of the discovery document. Schema artifacts live under <c>{BasePath}/schema/{name}</c>.</summary>
    public string BasePath { get; set; } = "/.well-known/bazaar/wire";

    /// <summary>Requests allowed per client address and window across both routes. Default 300, enough for a client to mirror every artifact (about 180 files) in one minute.</summary>
    public int PermitLimit { get; set; } = 300;

    /// <summary>Length of the fixed window. Default one minute.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Most client addresses tracked at once; further addresses share one bucket, which bounds memory. Default 10 000.</summary>
    public int MaxTrackedClients { get; set; } = 10_000;

    /// <summary><c>Cache-Control</c> max-age in seconds. Default 300.</summary>
    public int CacheMaxAgeSeconds { get; set; } = 300;

    /// <summary>How often the live operator settings are re-read from the settings store. Default 5 seconds, so an operator change applies within that time.</summary>
    public TimeSpan SettingsRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal bool IsValid(out string error)
    {
        if (PermitLimit < 1)
        {
            error = "PermitLimit must be at least 1.";
        }
        else if (Window <= TimeSpan.Zero)
        {
            error = "Window must be positive.";
        }
        else if (MaxTrackedClients < 1)
        {
            error = "MaxTrackedClients must be at least 1.";
        }
        else if (CacheMaxAgeSeconds < 0)
        {
            error = "CacheMaxAgeSeconds must not be negative.";
        }
        else if (SettingsRefreshInterval <= TimeSpan.Zero)
        {
            error = "SettingsRefreshInterval must be positive.";
        }
        else if (string.IsNullOrEmpty(BasePath) || BasePath[0] != '/' || BasePath.Length > 1 && BasePath[^1] == '/')
        {
            error = "BasePath must start with '/' and not end with one.";
        }
        else
        {
            error = string.Empty;
            return true;
        }

        return false;
    }
}
