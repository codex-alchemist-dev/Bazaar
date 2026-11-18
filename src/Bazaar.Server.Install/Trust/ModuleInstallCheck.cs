using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Install.Trust;

/// <summary>The install-time decision for one module package (doc 50 section 5). Installable only when both checks pass.</summary>
public abstract record ModuleInstallDecision
{
    private ModuleInstallDecision()
    {
    }

    /// <summary>The signature verifies and the host can run the module.</summary>
    public sealed record Allowed : ModuleInstallDecision;

    /// <summary>The package fails its signature check. The host is not checked.</summary>
    public sealed record RefusedSignature(ModuleSignatureStatus Status) : ModuleInstallDecision;

    /// <summary>The signature verifies, but this host cannot run the module. Each issue names the requirement that failed.</summary>
    public sealed record RefusedHost(IReadOnlyList<HostCompatibilityIssue> Issues) : ModuleInstallDecision;
}

/// <summary>
/// The install-time check: the signature first, then the host requirements the signature covers. Pure apart from the verifier call.
/// Installers must call this before they accept a package; the host is never checked against a declaration that did not verify.
/// </summary>
public static class ModuleInstallCheck
{
    public static ModuleInstallDecision Evaluate(ModulePackage package, IModuleSignatureVerifier verifier, HostFacts host)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(host);

        var status = verifier.Verify(package);
        if (status != ModuleSignatureStatus.Valid)
        {
            return new ModuleInstallDecision.RefusedSignature(status);
        }

        var issues = HostCompatibility.Check(package.Host, host);
        return issues.Count == 0 ? new ModuleInstallDecision.Allowed() : new ModuleInstallDecision.RefusedHost(issues);
    }
}
