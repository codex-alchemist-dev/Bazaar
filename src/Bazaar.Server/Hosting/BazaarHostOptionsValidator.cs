using Microsoft.Extensions.Options;

namespace Bazaar.Server.Hosting;

/// <summary>Checks host options at start. Each failure names the configuration key so the startup error is actionable.</summary>
public sealed class BazaarHostOptionsValidator : IValidateOptions<BazaarHostOptions>
{
    public ValidateOptionsResult Validate(string? name, BazaarHostOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DataRoot))
        {
            failures.Add($"{BazaarHostOptions.SectionName}:DataRoot is required.");
        }
        else if (!Path.IsPathFullyQualified(options.DataRoot))
        {
            failures.Add($"{BazaarHostOptions.SectionName}:DataRoot must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseConnectionReference))
        {
            failures.Add($"{BazaarHostOptions.SectionName}:DatabaseConnectionReference is required; use a secret reference such as env:NAME.");
        }

        if (options.KekReference is not null && string.IsNullOrWhiteSpace(options.KekReference))
        {
            failures.Add($"{BazaarHostOptions.SectionName}:KekReference must be omitted or non-empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
