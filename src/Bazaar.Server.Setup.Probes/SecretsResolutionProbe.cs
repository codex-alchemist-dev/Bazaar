namespace Bazaar.Server.Setup.Probes;

/// <summary>
/// <c>secrets.resolution</c>: the bootstrap reference <c>custody.kekRef</c> points at something that exists (an <c>env:</c> variable that
/// is set, a <c>file:</c> that exists and is not empty). Existence only: the value is never read into a result, and no reference text is
/// repeated. A <c>dpapi:</c> or <c>systemd-creds:</c> reference needs its OS provider, and the references in the secret store (<c>secret://</c>)
/// have no existence check, so either one leaves the check not evaluated unless the bootstrap reference already fails.
/// </summary>
public sealed class SecretsResolutionProbe(Func<string, bool>? environmentVariableSet = null, Func<string, bool>? fileHasContent = null) : ISetupInfrastructureProbe
{
    private readonly Func<string, bool> _envSet = environmentVariableSet ?? (name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private readonly Func<string, bool> _fileHasContent = fileHasContent ?? (path => File.Exists(path) && new FileInfo(path).Length > 0);

    /// <inheritdoc />
    public string CheckId => "secrets.resolution";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = context.Plan;
        var kekRef = plan.Custody.KekRef;
        var family = SecretReferences.Classify(kekRef);
        var storeRefs = plan.Models.Sources.Count(source => source.KeyRef is not null)
            + (plan.Custody.MergeCredential.AppIdRef is null ? 0 : 1);

        var bootstrapFound = family switch
        {
            SecretReferenceFamily.Env => _envSet(kekRef[SecretReferences.PrefixOf(family).Length..]),
            SecretReferenceFamily.File => _fileHasContent(kekRef[SecretReferences.PrefixOf(family).Length..]),
            _ => (bool?)null,
        };

        if (bootstrapFound == false)
        {
            return Task.FromResult(new SetupProbeReading(
                SetupProbeOutcome.Failed, "The key-encryption key reference does not resolve to anything.", "Set the variable, or create the file, that custody.kekRef names."));
        }

        if (bootstrapFound is null)
        {
            return Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Unavailable, "This kind of key-encryption key reference needs its OS provider to resolve"));
        }

        return Task.FromResult(storeRefs > 0
            ? new SetupProbeReading(SetupProbeOutcome.Unavailable, $"The key-encryption key reference resolves, but {storeRefs} secret-store reference(s) have no existence check yet")
            : new SetupProbeReading(SetupProbeOutcome.Healthy, "The key-encryption key reference resolves."));
    }
}
