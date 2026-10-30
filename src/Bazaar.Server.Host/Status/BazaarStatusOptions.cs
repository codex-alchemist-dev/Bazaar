using System.Net;

namespace Bazaar.Server.Host.Status;

/// <summary>
/// Where the status listener binds (doc 03, <c>host.status.bind</c> and <c>host.status.port</c>). <c>loopback</c> is the default; an IP literal such as
/// <c>0.0.0.0</c> is set explicitly through configuration (containers do this). Wider binds still lack the Tier 2 owner approval flow.
/// </summary>
public sealed class BazaarStatusOptions
{
    public const string SectionName = "Bazaar:Host:Status";

    public const string LoopbackBind = "loopback";

    public string Bind { get; set; } = LoopbackBind;

    public int Port { get; set; } = 7390;

    public IPAddress Address => ParseBind(Bind) ?? throw new InvalidOperationException(
        $"{SectionName}:Bind must be 'loopback' or an IP address, not '{Bind}'.");

    /// <summary>Returns the listener address for a bind value, or null when the value is neither <c>loopback</c> nor an IP literal.</summary>
    public static IPAddress? ParseBind(string bind)
    {
        if (bind.Equals(LoopbackBind, StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(bind, out var address) ? address : null;
    }
}

public sealed class BazaarStatusOptionsValidator : IValidateOptions<BazaarStatusOptions>
{
    public ValidateOptionsResult Validate(string? name, BazaarStatusOptions options)
    {
        var failures = new List<string>();
        if (BazaarStatusOptions.ParseBind(options.Bind) is null)
        {
            failures.Add($"{BazaarStatusOptions.SectionName}:Bind must be 'loopback' or an IP address such as 0.0.0.0.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{BazaarStatusOptions.SectionName}:Port must be 1-65535.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
