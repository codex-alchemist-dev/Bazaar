using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// Every secret reference present in the plan is well formed and of an allowed family (doc 45 section 1.6). This is syntax only:
/// nothing is resolved, so the check never touches a secret store (resolution is its own placeholder check). The bootstrap value
/// <c>custody.kekRef</c> must use the family its provider names. The others use <c>secret://bazaar/</c>. A settings key with a
/// secret-looking name also uses <c>secret://bazaar/</c>. A failing message names the key only, never the text it holds.
/// </summary>
public sealed class SecretReferenceSyntaxCheck : ISetupVerificationCheck
{
    private static readonly string[] _secretNameFragments = ["secret", "credential", "password", "token"];

    private const string _settingsPrefix = "settings.";

    /// <inheritdoc />
    public string Id => "secrets.syntax";

    /// <inheritdoc />
    public string Title => "Secret references well formed";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = context.Plan;
        var problems = new List<string>();
        var checkedCount = 0;

        checkedCount++;
        if (!IsBootstrapFor(plan.Custody.KekRef, plan.Custody.KekProvider))
        {
            problems.Add("custody.kekRef");
        }

        if (plan.Custody.MergeCredential.AppIdRef is not null)
        {
            checkedCount++;
            if (!SecretReferences.IsValid(plan.Custody.MergeCredential.AppIdRef, SecretReferences.SecretStoreFamilies))
            {
                problems.Add("custody.mergeCredential.appIdRef");
            }
        }
        else if (plan.Custody.MergeCredential.Kind == "owner_app")
        {
            checkedCount++;
            problems.Add("custody.mergeCredential.appIdRef");
        }

        for (var i = 0; i < plan.Models.Sources.Count; i++)
        {
            var keyRef = plan.Models.Sources[i].KeyRef;
            if (keyRef is null)
            {
                continue;
            }

            checkedCount++;
            if (!SecretReferences.IsValid(keyRef, SecretReferences.SecretStoreFamilies))
            {
                problems.Add($"models.sources[{i}].keyRef");
            }
        }

        foreach (var (key, value) in plan.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsSecretNamedSetting(key))
            {
                continue;
            }

            checkedCount++;
            if (value.ValueKind != JsonValueKind.String || !SecretReferences.IsValid(value.GetString()!, SecretReferences.SecretStoreFamilies))
            {
                problems.Add(_settingsPrefix + key);
            }
        }

        // Only the key paths are listed. The values are never read into the message, so they cannot leak through it.
        SetupCheckResult result = problems.Count == 0
            ? SetupCheckResult.Green($"All {checkedCount} secret reference(s) present are well formed and of an allowed family. None is resolved here.")
            : SetupCheckResult.Red(
                $"These secret references are missing or not well formed: {string.Join(", ", problems)}. Their values are not shown.",
                "Replace each listed value with a reference of the family doc 45 section 1.6 allows. A literal value is never accepted.");
        return Task.FromResult(result);
    }

    /// <summary>True when a settings key looks secret-typed. Matched case-insensitively, the same fragments the CLI view uses.</summary>
    public static bool IsSecretNamedSetting(string key) =>
        _secretNameFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the bootstrap reference is well formed and uses the family its provider names (doc 45 section 1.6).</summary>
    private static bool IsBootstrapFor(string kekRef, string kekProvider)
    {
        if (!SecretReferences.IsValid(kekRef, SecretReferences.BootstrapFamilies))
        {
            return false;
        }

        var expected = kekProvider switch
        {
            "env" => SecretReferenceFamily.Env,
            "file" => SecretReferenceFamily.File,
            "dpapi" => SecretReferenceFamily.Dpapi,
            "systemd-creds" => SecretReferenceFamily.SystemdCreds,
            _ => SecretReferenceFamily.None,
        };
        return SecretReferences.Classify(kekRef) == expected;
    }
}
