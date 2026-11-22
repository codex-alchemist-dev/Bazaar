namespace Bazaar.Client.CompanySetup;

/// <summary>
/// What the create-company screen collects (doc 45 section 1.4, the "Asks: Yes" rows it covers). Everything else in the setup plan
/// keeps the server's shipped default. The server writes no secret value here: the key-encryption-key is a reference.
/// </summary>
/// <param name="CompanyName">Shown to people. 1 to 100 characters.</param>
/// <param name="CompanySlug">Lower-case identifier; cannot change later.</param>
/// <param name="OwnerName">The owner's display name.</param>
/// <param name="Locale">Default locale, for example <c>en</c>.</param>
/// <param name="Timezone">Default IANA time zone.</param>
/// <param name="KekProvider">Key-encryption-key provider: <c>dpapi</c>, <c>systemd-creds</c>, <c>file</c> or <c>env</c>.</param>
/// <param name="KekReference">The bootstrap reference for that provider, for example <c>dpapi:C:\ProgramData\Bazaar\keys\kek.bin</c>.</param>
/// <param name="AcknowledgeNoBackupTarget">The recorded acknowledgement that no backup target is set yet (doc 45 open question 5).</param>
public sealed record CompanySetupRequest(
    string CompanyName,
    string CompanySlug,
    string OwnerName,
    string Locale,
    string Timezone,
    string KekProvider,
    string KekReference,
    bool AcknowledgeNoBackupTarget);

/// <summary>How the caller proves it may provision (doc 45 section 1.9): the one-time bootstrap code before the owner exists, a bearer token after.</summary>
/// <param name="BootstrapCode">The code the server console printed. Sent in a header, never logged.</param>
/// <param name="BearerToken">An owner's access token.</param>
public sealed record SetupAccess(string? BootstrapCode, string? BearerToken)
{
    /// <summary>Hides both secrets from <c>ToString</c>.</summary>
    public override string ToString() => "SetupAccess { secrets hidden }";
}

/// <summary>One finding from the server: a severity, the plan key it is about, and the rule. Never a value.</summary>
public sealed record SetupIssue(string Severity, string Path, string Message);

/// <summary>How a call to the provisioning API ended.</summary>
public enum SetupCallOutcome
{
    /// <summary>Valid, or applied.</summary>
    Ok,

    /// <summary>The plan has errors. See the issues.</summary>
    Invalid,

    /// <summary>Held for owner approval (a floor or a Tier 2 change). Nothing changed.</summary>
    Held,

    /// <summary>A step failed and rolled back.</summary>
    ApplyFailed,

    /// <summary>The server refused the caller: wrong or missing bootstrap code or token.</summary>
    AccessRefused,

    /// <summary>The server is off, too busy (429), already applying (409), or answered something unexpected.</summary>
    Unavailable,
}

/// <summary>The answer to a validate call.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="PlanHash">The plan hash to apply. Null unless <see cref="Outcome"/> is <see cref="SetupCallOutcome.Ok"/> or <see cref="SetupCallOutcome.Held"/>.</param>
/// <param name="Issues">Every finding, in the server's order.</param>
/// <param name="Message">One plain sentence for the person.</param>
public sealed record SetupValidation(SetupCallOutcome Outcome, string? PlanHash, IReadOnlyList<SetupIssue> Issues, string Message);

/// <summary>The answer to an apply call.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="AppliedSteps">The steps that committed.</param>
/// <param name="Issues">Every finding, in the server's order.</param>
/// <param name="Replayed">True when the server returned a stored result and ran nothing (the same plan was applied before).</param>
/// <param name="Message">One plain sentence for the person.</param>
public sealed record SetupApplication(SetupCallOutcome Outcome, IReadOnlyList<string> AppliedSteps, IReadOnlyList<SetupIssue> Issues, bool Replayed, string Message);

/// <summary>The client side of the provisioning API: <c>POST /setup/plans</c> (dry run) and <c>POST /setup/plans/{hash}/apply</c>.</summary>
public interface ICompanySetupService
{
    /// <summary>Checks the plan this request makes and returns its hash. Writes nothing on the server.</summary>
    Task<SetupValidation> ValidateAsync(CompanySetupRequest request, SetupAccess access, CancellationToken cancellationToken = default);

    /// <summary>Applies the plan this request makes. <paramref name="planHash"/> must be the hash <see cref="ValidateAsync"/> returned.</summary>
    Task<SetupApplication> ApplyAsync(CompanySetupRequest request, string planHash, SetupAccess access, CancellationToken cancellationToken = default);
}

/// <summary>Where a platform's key-storage default comes from, so the screen never hardcodes a path (doc 03: OS-specific things sit behind a provider).</summary>
public interface IKeyCustodyDefaults
{
    /// <summary>The provider the server host would pick on this machine, or null when the person must choose.</summary>
    string? DefaultProvider { get; }

    /// <summary>The reference that goes with <see cref="DefaultProvider"/>, or empty when the person must give one.</summary>
    string DefaultReference { get; }
}
