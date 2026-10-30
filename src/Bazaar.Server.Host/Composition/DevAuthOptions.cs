namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Local development authenticator settings (<c>Bazaar:DevAuth</c>). When <see cref="Enabled"/> is true, one bearer token grants the owner
/// principal. Off by default; the token is a secret reference (<c>env:NAME</c> or <c>file:PATH</c>), never a literal value.
/// </summary>
public sealed class DevAuthOptions
{
    public const string SectionName = "Bazaar:DevAuth";

    public bool Enabled { get; set; }

    /// <summary>Secret reference for the bearer token, such as <c>env:BAZAAR_DEV_TOKEN</c>.</summary>
    public string? TokenSecret { get; set; }

    /// <summary>Owner principal id. A GUID is used as is; any other text maps to a stable GUID.</summary>
    public string PrincipalId { get; set; } = "owner";
}

public sealed class DevAuthOptionsValidator : IValidateOptions<DevAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, DevAuthOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.TokenSecret))
        {
            failures.Add($"{DevAuthOptions.SectionName}:TokenSecret is required when {DevAuthOptions.SectionName}:Enabled is true (use env:NAME or file:PATH).");
        }

        if (string.IsNullOrWhiteSpace(options.PrincipalId))
        {
            failures.Add($"{DevAuthOptions.SectionName}:PrincipalId must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
