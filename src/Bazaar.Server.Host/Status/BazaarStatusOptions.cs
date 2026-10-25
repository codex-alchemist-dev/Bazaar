using System.Net;

namespace Bazaar.Server.Host.Status;

/// <summary>Where the status listener binds (doc 03, <c>host.status.bind</c> and <c>host.status.port</c>). Loopback is the floor; other values wait for the Tier 2 approval flow.</summary>
public sealed class BazaarStatusOptions
{
    public const string SectionName = "Bazaar:Host:Status";

    public string Bind { get; set; } = "loopback";

    public int Port { get; set; } = 7390;

    public IPAddress Address => IPAddress.Loopback;
}

public sealed class BazaarStatusOptionsValidator : IValidateOptions<BazaarStatusOptions>
{
    public ValidateOptionsResult Validate(string? name, BazaarStatusOptions options)
    {
        var failures = new List<string>();
        if (!options.Bind.Equals("loopback", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{BazaarStatusOptions.SectionName}:Bind must be 'loopback'; wider binds need owner approval, which is not wired yet.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{BazaarStatusOptions.SectionName}:Port must be 1-65535.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
