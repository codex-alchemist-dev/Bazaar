using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Setup;

/// <summary>
/// The semantic rules of a setup plan (doc 45 sections 1.5 to 1.7). Runs after the typed decode. It adds issues to the list
/// and normalizes the plan in place: modules are sorted and de-duplicated, and each setting value becomes the canonical JSON
/// the registry stores. Settings go through the registry, so its types, floors and approval tiers apply here too (doc 03).
/// The messages name the key and the rule; they never repeat a value.
/// </summary>
public static partial class SetupPlanValidator
{
    private static readonly string[] _releaseChannels = ["alpha", "beta", "stable", "ltsc", "latest-commit"];
    private static readonly string[] _updateModes = ["automatic", "manual"];
    private static readonly string[] _kekProviders = ["env", "file", "dpapi", "systemd-creds"];
    private static readonly string[] _identityKekProviders = ["file", "dpapi", "openbao", "tpm"];
    private static readonly string[] _mergeKinds = ["owner_app", "owner_linked"];
    private static readonly string[] _privacyProfiles = ["private"];

    private const int _maxNameLength = 100;

    private const string _providerSecretPrefix = "secret://bazaar/providers/";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImportIdRegex();

    [GeneratedRegex("^Bazaar\\.[A-Za-z][A-Za-z0-9.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleNameRegex();

    /// <summary>The semver.org grammar, as a single pattern.</summary>
    [GeneratedRegex(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();

    /// <summary>Validates and normalizes <paramref name="plan"/>, adding every issue found to <paramref name="issues"/>.</summary>
    public static void Validate(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(issues);

        if (plan.SchemaVersion != SetupConstants.SchemaVersion)
        {
            issues.Add(Error("schemaVersion", $"is not supported; this build reads schemaVersion {SetupConstants.SchemaVersion}."));
        }

        ValidateInstall(plan.Install, issues);
        ValidateDatabase(plan.Database, issues);
        ValidateCustody(plan.Custody, issues);
        ValidateOwner(plan.Owner, issues);
        if (!_privacyProfiles.Contains(plan.Privacy.Profile, StringComparer.Ordinal))
        {
            issues.Add(Error("privacy.profile", "must be private; instances are private by default."));
        }

        ValidateBackups(plan.Backups, issues);
        ValidateBackupSettings(plan, registry, issues);
        ValidateEmail(plan, issues);
        ValidateModels(plan.Models, issues);
        ValidateCompany(plan.Company, issues);
        NormalizeModules(plan, issues);
        ValidateNetwork(plan, registry, issues);
        ValidateReleaseChannel(plan, registry, issues);
        ValidateUpdatePolicy(plan, issues);
        ValidateIdentityKek(plan, issues);
        ValidateScamMailProtocol(plan, registry, issues);
        ValidateSettings(plan, registry, issues);
    }

    private static void ValidateInstall(SetupInstallSection install, List<SetupValidationIssue> issues)
    {
        if (IsBlank(install.CompanyName) || install.CompanyName.Length > _maxNameLength)
        {
            issues.Add(Error("install.companyName", $"must be 1 to {_maxNameLength} characters."));
        }

        if (install.CompanySlug is null || !SlugRegex().IsMatch(install.CompanySlug))
        {
            issues.Add(Error("install.companySlug", "must be lower-case letters, digits and dashes, starting with a letter or digit, up to 63 characters."));
        }

        if (IsBlank(install.Locale))
        {
            issues.Add(Error("install.locale", "must not be blank."));
        }

        if (IsBlank(install.Timezone))
        {
            issues.Add(Error("install.timezone", "must not be blank."));
        }

        if (!_releaseChannels.Contains(install.ReleaseChannel, StringComparer.Ordinal))
        {
            issues.Add(Error("install.releaseChannel", "must be alpha, beta, stable, ltsc or latest-commit."));
        }

        if (!_updateModes.Contains(install.UpdatePolicy.Security, StringComparer.Ordinal))
        {
            issues.Add(Error("install.updatePolicy.security", "must be automatic or manual."));
        }

        if (!_updateModes.Contains(install.UpdatePolicy.Other, StringComparer.Ordinal))
        {
            issues.Add(Error("install.updatePolicy.other", "must be automatic or manual."));
        }
    }

    private static void ValidateDatabase(SetupDatabaseSection database, List<SetupValidationIssue> issues)
    {
        if (database.PostgresMajor is < 14 or > 99)
        {
            issues.Add(Error("database.postgresMajor", "must be a PostgreSQL major version from 14 to 99."));
        }

        if (database.DataRoot is not null && (IsBlank(database.DataRoot) || database.DataRoot.Any(char.IsControl)))
        {
            issues.Add(Error("database.dataRoot", "must be a path without control characters, or omitted for the platform default."));
        }

        if (database.GcPartitionCount is < 1 or > 1024)
        {
            issues.Add(Error("database.gcPartitionCount", "must be 1 to 1024 (doc 04 default 16)."));
        }
    }

    private static void ValidateCustody(SetupCustodySection custody, List<SetupValidationIssue> issues)
    {
        if (!_kekProviders.Contains(custody.KekProvider, StringComparer.Ordinal))
        {
            issues.Add(Error("custody.kekProvider", "must be env, file, dpapi or systemd-creds."));
        }

        // The reference is never echoed. The message says which families are allowed.
        if (!SecretReferences.IsValid(custody.KekRef, SecretReferences.BootstrapFamilies))
        {
            issues.Add(Error("custody.kekRef", "must be a well-formed bootstrap reference: env:NAME, file:PATH, dpapi:PATH or systemd-creds:NAME. A literal value is not accepted."));
        }
        else if (_kekProviders.Contains(custody.KekProvider, StringComparer.Ordinal)
                 && SecretReferences.Classify(custody.KekRef) != FamilyOf(custody.KekProvider))
        {
            issues.Add(Error("custody.kekRef", "must use the reference family named by custody.kekProvider."));
        }

        if (custody.AuditAnchorTarget is not null
            && !(Uri.TryCreate(custody.AuditAnchorTarget, UriKind.Absolute, out var anchor) && anchor.Scheme == Uri.UriSchemeHttps))
        {
            issues.Add(Error("custody.auditAnchorTarget", "must be an absolute https URL, or omitted."));
        }

        ValidateMergeCredential(custody.MergeCredential, issues);
    }

    private static void ValidateMergeCredential(SetupMergeCredentialSection merge, List<SetupValidationIssue> issues)
    {
        if (!_mergeKinds.Contains(merge.Kind, StringComparer.Ordinal))
        {
            issues.Add(Error("custody.mergeCredential.kind", "must be owner_app or owner_linked."));
            return;
        }

        if (merge.Kind == "owner_app")
        {
            if (merge.AppIdRef is null || !SecretReferences.IsValid(merge.AppIdRef, SecretReferences.SecretStoreFamilies))
            {
                issues.Add(Error("custody.mergeCredential.appIdRef", "is required for owner_app and must be a secret://bazaar/ reference."));
            }
        }
        else if (merge.AppIdRef is not null)
        {
            issues.Add(Error("custody.mergeCredential.appIdRef", "must be omitted when kind is owner_linked."));
        }
    }

    private static void ValidateOwner(SetupOwnerSection owner, List<SetupValidationIssue> issues)
    {
        if (IsBlank(owner.DisplayName) || owner.DisplayName.Length > _maxNameLength)
        {
            issues.Add(Error("owner.displayName", $"must be 1 to {_maxNameLength} characters."));
        }

        for (var i = 0; i < owner.Recovery.BackupOwners.Count; i++)
        {
            if (IsBlank(owner.Recovery.BackupOwners[i]))
            {
                issues.Add(Error($"owner.recovery.backupOwners[{i}]", "must not be blank."));
            }
        }
    }

    private static void ValidateBackups(SetupBackupsSection backups, List<SetupValidationIssue> issues)
    {
        if (backups.Target is null)
        {
            if (!backups.AcknowledgeNoTarget)
            {
                issues.Add(new SetupValidationIssue(
                    SetupConstants.MissingBackupTargetSeverity,
                    "backups.target",
                    "is required unless backups.acknowledgeNoTarget is true."));
            }
        }
        else if (IsBlank(backups.Target))
        {
            issues.Add(Error("backups.target", "must not be blank."));
        }

        if (backups.DrillIntervalDays is < 1 or > 365)
        {
            issues.Add(Error("backups.drillIntervalDays", "must be 1 to 365."));
        }
    }

    /// <summary>
    /// The backups section is also registry keys (doc 45 step 8; doc 33): a value set twice (in the section and in <c>settings</c>) is an
    /// error, and a target runs through the registry's checks (shape, no credentials, and held for owner approval when it leaves the install).
    /// </summary>
    private static void ValidateBackupSettings(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues)
    {
        RejectDuplicate(plan, SetupSettingProjection.BackupTargetKey, "backups.target", issues);
        RejectDuplicate(plan, SetupSettingProjection.BackupDrillIntervalKey, "backups.drillIntervalDays", issues);
        RejectDuplicate(plan, SetupSettingProjection.BackupNoTargetAcknowledgedKey, "backups.acknowledgeNoTarget", issues);

        if (plan.Backups.Target is null || IsBlank(plan.Backups.Target))
        {
            return;
        }

        if (!registry.TryGet(SetupSettingProjection.BackupTargetKey, out var definition) || definition is null)
        {
            issues.Add(Error("backups.target", "the backup target setting is not registered in this build."));
            return;
        }

        var value = JsonSerializer.SerializeToElement(plan.Backups.Target, SetupPlanJsonContext.Default.String);
        CheckRegistryValue(definition, "backups.target", value, issues);
    }

    /// <summary>
    /// The email section (doc 45 step 11; doc 22): off with no back end, or on with one of the four v1 back ends. The operator domain
    /// is not a plan field. A value set twice (in the section and in <c>settings</c>) is an error.
    /// </summary>
    private static void ValidateEmail(SetupPlan plan, List<SetupValidationIssue> issues)
    {
        RejectDuplicate(plan, SetupSettingProjection.MailEnabledKey, "email.enabled", issues);
        RejectDuplicate(plan, SetupSettingProjection.MailBackendKey, "email.backend", issues);

        if (SetupSettingProjection.IsEmailChoiceCoherent(plan))
        {
            return;
        }

        issues.Add(plan.Email.Enabled
            ? Error("email.backend", $"is required when email.enabled is true: one of {string.Join(", ", SetupSettingProjection.MailBackends)}.")
            : Error("email.backend", "must be empty while email.enabled is false."));
    }

    private static void RejectDuplicate(SetupPlan plan, string key, string planPath, List<SetupValidationIssue> issues)
    {
        if (plan.Settings.ContainsKey(key))
        {
            issues.Add(Error("settings." + key, $"is already set by {planPath}; set it in one place."));
        }
    }

    private static void ValidateModels(SetupModelsSection models, List<SetupValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var canonicalIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < models.Sources.Count; i++)
        {
            var source = models.Sources[i];
            var path = $"models.sources[{i}]";
            if (source.Id is null || !SlugRegex().IsMatch(source.Id))
            {
                issues.Add(Error(path + ".id", "must be a lower-case slug."));
            }
            else if (!ids.Add(source.Id))
            {
                issues.Add(Error(path + ".id", "is already used by another source; source ids are unique."));
            }

            if (source.KeyRef is not null && !SecretReferences.IsValid(source.KeyRef, SecretReferences.SecretStoreFamilies))
            {
                issues.Add(Error(path + ".keyRef", "must be a secret://bazaar/ reference, or omitted. A literal value is not accepted."));
            }
            else if (source.KeyRef is not null && !source.KeyRef.StartsWith(_providerSecretPrefix, StringComparison.Ordinal))
            {
                issues.Add(Error(path + ".keyRef", "must be under secret://bazaar/providers/ (the gateway only stores provider key references from there)."));
            }

            if (source.Id is not null && SlugRegex().IsMatch(source.Id))
            {
                ValidateModelSourceAgainstCatalogue(source, path, canonicalIds, issues);
            }
        }

        if (IsBlank(models.DefaultPreset))
        {
            issues.Add(Error("models.defaultPreset", "must not be blank."));
        }
        else if (!SlugRegex().IsMatch(models.DefaultPreset))
        {
            issues.Add(Error("models.defaultPreset", "must be a lower-case slug."));
        }
    }

    /// <summary>
    /// A source must be one the default catalogue knows (doc 05), because the plan carries no kind, adapter or base URL and setup does not
    /// invent them. A source that takes a key must carry one, and a local runtime takes none (the gateway refuses a key for auth none).
    /// </summary>
    private static void ValidateModelSourceAgainstCatalogue(SetupModelSource source, string path, HashSet<string> canonicalIds, List<SetupValidationIssue> issues)
    {
        if (!ModelSourceCatalogue.Default.TryGet(source.Id, out var entry) || entry is null)
        {
            issues.Add(Error(path + ".id", "is not in the default model source catalogue (doc 05), so its kind, adapter and base URL are unknown. Add the source in the admin panel after setup."));
            return;
        }

        if (!canonicalIds.Add(entry.Id))
        {
            issues.Add(Error(path + ".id", "names a source that another entry already names (an id and its alias are the same source)."));
        }

        if (entry.TakesKey && source.KeyRef is null)
        {
            issues.Add(Error(path + ".keyRef", "is required: this source takes an API key or bearer token, held by reference."));
        }
        else if (!entry.TakesKey && source.KeyRef is not null)
        {
            issues.Add(Error(path + ".keyRef", "must be omitted: this source is a local runtime and takes no key."));
        }
    }

    private static void ValidateCompany(SetupCompanySection company, List<SetupValidationIssue> issues)
    {
        var source = company.Source ?? string.Empty;
        if (source == "blank")
        {
            return;
        }

        if (source.StartsWith("template:", StringComparison.Ordinal))
        {
            var rest = source["template:".Length..];
            var at = rest.IndexOf('@', StringComparison.Ordinal);
            if (at > 0 && TemplateIdRegex().IsMatch(rest[..at]) && SemverRegex().IsMatch(rest[(at + 1)..]))
            {
                return;
            }

            issues.Add(Error("company.source", "template sources must be template:<id>@<semver>, for example template:starter@1.0.0."));
            return;
        }

        if (source.StartsWith("import:", StringComparison.Ordinal) && ImportIdRegex().IsMatch(source["import:".Length..]))
        {
            return;
        }

        issues.Add(Error("company.source", "must be blank, template:<id>@<semver> or import:<id>."));
    }

    /// <summary>Sorts and de-duplicates the module list (it is a set), and rejects names that are not Bazaar module names.</summary>
    private static void NormalizeModules(SetupPlan plan, List<SetupValidationIssue> issues)
    {
        for (var i = 0; i < plan.Modules.Count; i++)
        {
            if (plan.Modules[i] is null || !ModuleNameRegex().IsMatch(plan.Modules[i]))
            {
                issues.Add(Error($"modules[{i}]", "must be a Bazaar module name such as Bazaar.GC."));
            }
        }

        var normalized = plan.Modules.Where(name => name is not null).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        plan.Modules.Clear();
        plan.Modules.AddRange(normalized);
    }

    /// <summary>
    /// The network block maps to two registry keys (doc 03). The values go through the registry, so a non-loopback bind is held
    /// for approval. A value set twice (in the block and in settings) is an error, so there is one answer.
    /// </summary>
    private static void ValidateNetwork(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues)
    {
        if (plan.Network.StatusPort is < 1 or > 65535)
        {
            issues.Add(Error("network.statusPort", "must be a port from 1 to 65535."));
        }

        foreach (var (settingName, path) in new[] { ("host.status.bind", "network.statusBind"), ("host.status.port", "network.statusPort") })
        {
            if (plan.Settings.ContainsKey(settingName))
            {
                issues.Add(Error("settings." + settingName, $"is already set by {path}; set it in one place."));
            }
        }

        if (!registry.TryGet("host.status.bind", out var bind) || bind is null
            || !registry.TryGet("host.status.port", out var port) || port is null)
        {
            issues.Add(Error("network", "the host status settings are not registered in this build."));
            return;
        }

        var bindValue = JsonSerializer.SerializeToElement(plan.Network.StatusBind, SetupPlanJsonContext.Default.String);
        CheckRegistryValue(bind, "network.statusBind", bindValue, issues);
        var portValue = JsonSerializer.SerializeToElement(plan.Network.StatusPort, SetupPlanJsonContext.Default.Int32);
        CheckRegistryValue(port, "network.statusPort", portValue, issues);
    }

    /// <summary>
    /// The release channel is also the registry key <c>update.channel</c> (doc 30). A value set twice (in the plan field and in
    /// settings) is an error, so there is one answer. A build whose registry has no such key skips the registry check; the channel
    /// shape itself is checked in <c>ValidateInstall</c>.
    /// </summary>
    private static void ValidateReleaseChannel(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues)
    {
        if (plan.Settings.ContainsKey(SetupSettingProjection.UpdateChannelKey))
        {
            issues.Add(Error("settings." + SetupSettingProjection.UpdateChannelKey, "is already set by install.releaseChannel; set it in one place."));
        }

        if (!_releaseChannels.Contains(plan.Install.ReleaseChannel, StringComparer.Ordinal)
            || !registry.TryGet(SetupSettingProjection.UpdateChannelKey, out var definition) || definition is null)
        {
            return;
        }

        var value = JsonSerializer.SerializeToElement(plan.Install.ReleaseChannel.Replace('-', '_'), SetupPlanJsonContext.Default.String);
        CheckRegistryValue(definition, "install.releaseChannel", value, issues);
    }

    /// <summary>
    /// The update policy (doc 45 step 2). The security half is the registry key <c>deps.update.autoSecurityPatches</c>, so a value set
    /// twice (in the plan field and in settings) is an error. The other half has no backing setting: only <c>manual</c>, the shipped
    /// default, can be applied, so <c>automatic</c> is refused here rather than accepted and silently not stored.
    /// </summary>
    private static void ValidateUpdatePolicy(SetupPlan plan, List<SetupValidationIssue> issues)
    {
        if (plan.Settings.ContainsKey(SetupSettingProjection.AutoSecurityPatchesKey))
        {
            issues.Add(Error("settings." + SetupSettingProjection.AutoSecurityPatchesKey, "is already set by install.updatePolicy.security; set it in one place."));
        }

        if (_updateModes.Contains(plan.Install.UpdatePolicy.Other, StringComparer.Ordinal) && !SetupSettingProjection.IsOtherUpdatePolicyBacked(plan))
        {
            issues.Add(Error("install.updatePolicy.other", "automatic is not supported yet: no setting stores it, so only manual can be applied."));
        }
    }

    /// <summary>
    /// The identity KEK provider (doc 45 step 5, doc 14 <c>identity.kek_provider</c>). Optional: absent writes nothing. A value must be
    /// file, dpapi, openbao or tpm (a provider may land in a later build, the plan value is valid now). The KEK opens before the
    /// database, so this value is applied to the host configuration file, not to the settings registry (PROPOSED, doc 45 step 5).
    /// </summary>
    private static void ValidateIdentityKek(SetupPlan plan, List<SetupValidationIssue> issues)
    {
        var value = plan.Custody.IdentityKek;
        if (value is not null && !_identityKekProviders.Contains(value, StringComparer.Ordinal))
        {
            issues.Add(Error("custody.identityKek", "must be file, dpapi, openbao or tpm, or omitted for the server default."));
        }
    }

    /// <summary>
    /// The Nigerian Prince Protocol step (docs/design/45 section 1.11). It is offered only when email is on, and enabling it is an
    /// owner-tier change to <c>security.scam_mail.protocol_enabled</c>, so it is held for approval and never applied silently. Off is
    /// the default and the safe state, so it needs no check. The plan section is the one place that sets the key.
    /// </summary>
    private static void ValidateScamMailProtocol(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues)
    {
        const string settingName = "security.scam_mail.protocol_enabled";
        if (plan.Settings.ContainsKey(settingName))
        {
            issues.Add(Error("settings." + settingName, "is already set by scamMailProtocol.enabled; set it in one place."));
        }

        if (!plan.ScamMailProtocol.Enabled)
        {
            return;
        }

        if (!plan.Email.Enabled)
        {
            issues.Add(Error("scamMailProtocol.enabled", "needs email to be on; the step is offered only when email is enabled."));
        }

        if (!registry.TryGet(settingName, out var definition) || definition is null)
        {
            issues.Add(Error("scamMailProtocol", "the scam-mail setting is not registered in this build."));
            return;
        }

        using var valueDoc = JsonDocument.Parse("true");
        var value = valueDoc.RootElement.Clone();
        CheckRegistryValue(definition, "scamMailProtocol.enabled", value, issues);
    }

    private static void ValidateSettings(SetupPlan plan, ISetupSettingCatalog registry, List<SetupValidationIssue> issues) =>
        ValidateSettingValues(plan.Settings, registry, "settings.", issues);

    /// <summary>
    /// Checks a map of registry values: known key, install scope, type, floor, and the write rule. Shared by the plan and by a
    /// template's settings presets (doc 45 section 1.8), so both are held to the same registry. Values are normalized in place.
    /// </summary>
    internal static void ValidateSettingValues(
        IDictionary<string, JsonElement> settings,
        ISetupSettingCatalog registry,
        string pathPrefix,
        List<SetupValidationIssue> issues)
    {
        foreach (var name in settings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var path = pathPrefix + name;
            if (!registry.TryGet(name, out var definition) || definition is null)
            {
                issues.Add(Error(path, "is not a known setting key in this build."));
                continue;
            }

            if (!definition.IsInstallScope)
            {
                issues.Add(Error(path, "is not an install-scope setting; the setup plan sets install values only."));
                continue;
            }

            settings[name] = NormalizeValue(definition, path, settings[name], issues);
        }
    }

    /// <summary>True when <paramref name="text"/> is a template id: lower-case letters, digits and dashes (the plan's <c>template:</c> source rule).</summary>
    internal static bool IsTemplateId(string text) => TemplateIdRegex().IsMatch(text);

    /// <summary>True when <paramref name="text"/> is an exact semantic version (ranges are not semver and are refused).</summary>
    internal static bool IsSemver(string text) => SemverRegex().IsMatch(text);

    /// <summary>True when <paramref name="text"/> is a Bazaar module name such as <c>Bazaar.GC</c>.</summary>
    internal static bool IsModuleName(string text) => ModuleNameRegex().IsMatch(text);

    /// <summary>True when <paramref name="text"/> is a lower-case slug such as a role id or a preset name.</summary>
    internal static bool IsSlug(string text) => SlugRegex().IsMatch(text);

    /// <summary>Runs one registry value through the same checks as a write. The stored value becomes the canonical form.</summary>
    private static void CheckRegistryValue(ISetupSettingDefinition definition, string path, JsonElement value, List<SetupValidationIssue> issues)
    {
        NormalizeValue(definition, path, value, issues);
    }

    /// <summary>
    /// Type check and floor check (a reason is an error, exit 1), then the write rule (a value that needs approval or that
    /// the actor may not write is held, exit 2). Returns the canonical JSON in every case, so the hash is always defined.
    /// </summary>
    private static JsonElement NormalizeValue(ISetupSettingDefinition definition, string path, JsonElement value, List<SetupValidationIssue> issues)
    {
        var reason = definition.CheckValue(value);
        if (reason is not null)
        {
            issues.Add(Error(path, reason));
            return value.Clone();
        }

        var evaluation = definition.Evaluate(SetupConstants.PlanActorId, value);
        if (!evaluation.Accepted)
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Hold, path, evaluation.Reason ?? "is held for approval."));
        }

        using var document = JsonDocument.Parse(evaluation.ValueJson);
        return document.RootElement.Clone();
    }

    private static SecretReferenceFamily FamilyOf(string provider) => provider switch
    {
        "env" => SecretReferenceFamily.Env,
        "file" => SecretReferenceFamily.File,
        "dpapi" => SecretReferenceFamily.Dpapi,
        "systemd-creds" => SecretReferenceFamily.SystemdCreds,
        _ => SecretReferenceFamily.None,
    };

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    private static SetupValidationIssue Error(string path, string message) =>
        new(SetupIssueSeverity.Error, path, message);
}
