namespace Bazaar.Server.CodingPermissions;

/// <summary>
/// Operator settings for the coding layer of the permission gate (doc 17, "Settings"). Options may tighten the floors,
/// never loosen them; <see cref="Validate"/> rejects anything below the floor.
/// </summary>
public sealed record CodingPermissionOptions
{
    /// <summary>The settings key of <see cref="PushAlwaysApprove"/>.</summary>
    public const string PushAlwaysApproveKey = "harness.push_always_approve";

    /// <summary>When true, every push needs an approval, even from an untainted session. Default false (doc 17 Question 1).</summary>
    public bool PushAlwaysApprove { get; init; }

    /// <summary>The repository's default branch. Pushes to it are always denied.</summary>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>Throws <see cref="ArgumentException"/> when a value loosens a floor.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultBranch))
        {
            throw new ArgumentException("DefaultBranch must name the default branch; an empty value would unprotect it.", nameof(DefaultBranch));
        }
    }
}
