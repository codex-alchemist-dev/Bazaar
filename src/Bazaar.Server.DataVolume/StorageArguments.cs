namespace Bazaar.Server.DataVolume;

/// <summary>The command line of <c>storage init</c> and <c>storage migrate</c>. The two take the same volume options; migrate adds <c>--from</c>.</summary>
internal sealed record StorageArguments(
    string? Provider, string? Container, string ContainerKind, long? MaxGb, string? ServiceAccount, string? From, bool DryRun, string? Error)
{
    public static StorageArguments Parse(ReadOnlySpan<string> args, bool migrate)
    {
        string? provider = null, container = null, serviceAccount = null, from = null;
        var containerKind = "file";
        long? maxGb = null;
        var dryRun = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i].ToLowerInvariant();
            if (!seen.Add(option))
                return Fail($"{args[i]} was given twice.");

            if (option == "--dry-run")
            {
                dryRun = true;
                continue;
            }

            if (option is not ("--provider" or "--container" or "--container-kind" or "--max-gb" or "--service-account") && !(migrate && option == "--from"))
                return Fail($"unknown option '{args[i]}'.");

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return Fail($"{args[i]} needs a value.");

            var value = args[++i];
            switch (option)
            {
                case "--provider":
                    provider = value.Trim().ToLowerInvariant();
                    break;
                case "--container":
                    container = value;
                    break;
                case "--container-kind":
                    containerKind = value.Trim().ToLowerInvariant();
                    if (containerKind is not ("file" or "device"))
                        return Fail("--container-kind must be file or device.");
                    break;
                case "--max-gb":
                    if (!long.TryParse(value, out var gb) || gb is < 1 or > 1_000_000)
                        return Fail("--max-gb must be a whole number of at least 1.");
                    maxGb = gb;
                    break;
                case "--from":
                    from = value;
                    break;
                default:
                    serviceAccount = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(provider))
            return Fail("--provider is required.");

        if (migrate && string.IsNullOrWhiteSpace(from))
            return Fail("--from is required: the old plain data root.");

        return new StorageArguments(provider, container, containerKind, maxGb, serviceAccount, from, dryRun, null);
    }

    private static StorageArguments Fail(string error) => new(null, null, "file", null, null, null, false, error);
}

/// <summary>What <c>storage init</c> and <c>storage migrate</c> are going to create, decided before anything is touched.</summary>
internal sealed record InitPlan(
    string ProviderKind,
    IDataVolumeProvider Provider,
    IDataVolumeOperator? Operator,
    string InstallId,
    bool InstallIdGenerated,
    string ContainerKind,
    string ContainerPath,
    long MaxSizeBytes,
    string ServiceAccount)
{
    public bool Plain => Operator is null;
}

/// <summary>What exists after a volume creation attempt, for the failure report.</summary>
internal sealed class VolumeCreationState
{
    public bool ContainerMayExist { get; set; }

    public string? VolumeId { get; set; }
}
