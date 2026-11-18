using System.Text.Json.Nodes;
using Bazaar.Contracts.Modules;
using Bazaar.Server.Install.Abstractions;
using Bazaar.Server.Install.Trust;

namespace Bazaar.Server.Install;

/// <summary>The outcome of <see cref="ModuleInstallService.InstallAsync"/>. Every refusal writes nothing.</summary>
public abstract record ModuleInstallResult
{
    private ModuleInstallResult()
    {
    }

    /// <summary>Both checks passed and the install was audited.</summary>
    public sealed record Installed(string ModuleId, string Version) : ModuleInstallResult;

    /// <summary>The package fails its signature check (doc 48 section 1). Nothing is recorded.</summary>
    public sealed record RefusedSignature(ModuleSignatureStatus Status) : ModuleInstallResult;

    /// <summary>The signature verifies, but this host cannot run the module (doc 50 section 5). Nothing is recorded.</summary>
    public sealed record RefusedHost(IReadOnlyList<HostCompatibilityIssue> Issues) : ModuleInstallResult;

    /// <summary>The package declares permissions the owner has not approved. Carries the list. Nothing is recorded.</summary>
    public sealed record RefusedUnapproved(IReadOnlyList<string> Unapproved) : ModuleInstallResult;
}

/// <summary>
/// Installs a module package (doc 48 section 1, doc 50 section 5). Runs <see cref="ModuleInstallCheck"/> first (signature, then
/// host), then <see cref="PermissionApproval"/> against the owner's approved set. Only when both pass does it write the
/// <c>module.installed</c> audit entry, which is the install record. Any refusal writes nothing.
/// </summary>
public sealed class ModuleInstallService(IModuleSignatureVerifier verifier, IModuleInstallStore store, TimeProvider time)
{
    /// <summary>Checks the package and installs it when it passes.</summary>
    /// <param name="package">The package to install, as downloaded.</param>
    /// <param name="host">What this host offers.</param>
    /// <param name="approved">The permission set the owner approved for this module version (or the last approved version).</param>
    /// <param name="actor">Who asked for the install, for the audit entry.</param>
    public async Task<ModuleInstallResult> InstallAsync(
        ModulePackage package,
        HostFacts host,
        IReadOnlySet<string> approved,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        switch (ModuleInstallCheck.Evaluate(package, verifier, host))
        {
            case ModuleInstallDecision.RefusedSignature signature:
                return new ModuleInstallResult.RefusedSignature(signature.Status);
            case ModuleInstallDecision.RefusedHost hostRefusal:
                return new ModuleInstallResult.RefusedHost(hostRefusal.Issues);
        }

        var permissions = PermissionApproval.Evaluate(package.DeclaredPermissions, approved);
        if (!permissions.IsApproved)
        {
            return new ModuleInstallResult.RefusedUnapproved(permissions.Unapproved);
        }

        var payload = new JsonObject
        {
            ["content_digest"] = package.ContentDigestSha256Hex,
            ["host_arch"] = new JsonArray([.. package.Host.Architectures.Order(StringComparer.Ordinal).Select(a => (JsonNode)a)]),
            ["host_os"] = new JsonArray([.. package.Host.OperatingSystems.Order(StringComparer.Ordinal).Select(o => (JsonNode)o)]),
            ["module_id"] = package.ModuleId,
            ["permissions"] = new JsonArray([.. package.DeclaredPermissions.Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
            ["signer"] = package.SignerKeyId,
            ["version"] = package.Version,
        };
        await store.RecordInstalledAsync(new InstallAuditEntry(time.GetUtcNow(), actor, ModuleAuditKinds.Installed, payload), ct).ConfigureAwait(false);

        return new ModuleInstallResult.Installed(package.ModuleId, package.Version);
    }
}
