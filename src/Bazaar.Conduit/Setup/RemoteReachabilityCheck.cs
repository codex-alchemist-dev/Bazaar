using System.Net;
using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Conduit.Setup;

/// <summary>
/// The wizard's remote access check (doc 45 step 15b, doc 39). It answers the <c>remote.reachable</c> placeholder, so Setup does not
/// reference Conduit. Off is green, because off is the default. A named Cloudflare tunnel is tried for up to 90 s with
/// <see cref="TunnelReadiness"/>, since a new host name answers only after about a minute (spike C2). The result is advice: an
/// unreachable tunnel is amber and never red, so it never blocks the apply. A quick tunnel or an unset host name is not evaluated,
/// because the address is not known from the plan.
/// </summary>
public sealed class RemoteReachabilityCheck : ISetupVerificationCheck
{
    /// <summary>The check id, matching the placeholder in the setup verification list.</summary>
    public const string CheckId = "remote.reachable";

    private const string _enabledKey = "conduit.cloudflared.enabled";
    private const string _modeKey = "conduit.cloudflared.mode";
    private const string _hostnameKey = "conduit.cloudflared.hostname";

    private readonly Func<Uri, CancellationToken, Task<HttpStatusCode?>> _fetch;
    private readonly TimeProvider _time;
    private readonly TunnelReadinessOptions _options;

    /// <summary>Creates the check. <paramref name="fetch"/> returns the HTTP status of a GET, or null when nothing answered.</summary>
    public RemoteReachabilityCheck(Func<Uri, CancellationToken, Task<HttpStatusCode?>> fetch, TimeProvider time, TunnelReadinessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(time);
        _fetch = fetch;
        _time = time;
        _options = options ?? TunnelReadinessOptions.Default;
    }

    /// <inheritdoc />
    public string Id => CheckId;

    /// <inheritdoc />
    public string Title => "Remote access reachable";

    /// <inheritdoc />
    public async Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Plan.Settings;
        if (!settings.TryGetValue(_enabledKey, out var enabled) || enabled.ValueKind != JsonValueKind.True)
        {
            return SetupCheckResult.Green("Remote access is off, which is the default. No inbound internet connection.");
        }

        var mode = Text(settings, _modeKey) ?? "named";
        if (mode == "quick")
        {
            return SetupCheckResult.NotEvaluated("a quick tunnel's address is known only once it runs");
        }

        var host = Text(settings, _hostnameKey);
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return SetupCheckResult.Amber(
                "The Cloudflare tunnel is on but no valid host name is set.",
                "Set conduit.cloudflared.hostname to the tunnel's public host name.");
        }

        var uri = new Uri($"https://{host}/");
        var result = await TunnelReadiness.WaitAsync(async ct => IsReachable(await _fetch(uri, ct).ConfigureAwait(false)), _options, _time, cancellationToken)
            .ConfigureAwait(false);
        return result.Reachable
            ? SetupCheckResult.Green($"The Cloudflare tunnel answered after {result.Attempts} {(result.Attempts == 1 ? "try" : "tries")}.")
            : SetupCheckResult.Amber(
                $"The Cloudflare tunnel did not answer in {_options.Window.TotalSeconds:F0} s.",
                "Check that cloudflared is running and the tunnel token is stored, then run verification again. A new host name can take a minute to start answering.");
    }

    /// <summary>Any answer below 500 means the tunnel is up; Cloudflare answers 530 (and other 5xx) when no tunnel is connected.</summary>
    private static bool IsReachable(HttpStatusCode? status) => status is { } code && (int)code < 500;

    private static string? Text(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
