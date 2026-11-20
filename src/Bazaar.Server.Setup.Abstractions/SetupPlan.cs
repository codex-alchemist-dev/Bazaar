using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// The setup plan (doc 45 section 1.2): one normalized document with every decision filled in, defaults included. It holds
/// references only, never a secret value. Every entry path (file, flags, environment, API, wizard) produces this shape, so
/// its canonical form and hash are the same whichever door was used.
/// </summary>
public sealed class SetupPlan
{
    public int SchemaVersion { get; init; } = SetupConstants.SchemaVersion;

    public SetupInstallSection Install { get; init; } = new();

    public SetupDatabaseSection Database { get; init; } = new();

    public SetupCustodySection Custody { get; init; } = new();

    public SetupOwnerSection Owner { get; init; } = new();

    public SetupPrivacySection Privacy { get; init; } = new();

    public SetupBackupsSection Backups { get; init; } = new();

    public SetupModelsSection Models { get; init; } = new();

    public SetupEmailSection Email { get; init; } = new();

    public SetupScamMailSection ScamMailProtocol { get; init; } = new();

    /// <summary>The company source. Required; there is no default, so an operator always states what the company starts from.</summary>
    public SetupCompanySection Company { get; init; } = new();

    /// <summary>Module names. Normalized to a sorted, distinct list, so the order in the file does not change the hash.</summary>
    public List<string> Modules { get; init; } = [];

    public SetupNetworkSection Network { get; init; } = new();

    /// <summary>Setting values keyed by registry name (doc 03). Each value is the canonical JSON the registry accepts.</summary>
    public Dictionary<string, JsonElement> Settings { get; init; } = [];
}

public sealed class SetupInstallSection
{
    /// <summary>Required. Shown to people; no default.</summary>
    public string CompanyName { get; init; } = "";

    /// <summary>Required. Lower-case slug used in paths and package names.</summary>
    public string CompanySlug { get; init; } = "";

    public string Locale { get; init; } = "en";

    /// <summary>An IANA time zone name. Checked for shape only; the platform's time-zone database is not read here.</summary>
    public string Timezone { get; init; } = "UTC";

    /// <summary>Release channel (docs/VERSIONING.md): alpha, beta, stable, ltsc or latest-commit.</summary>
    public string ReleaseChannel { get; init; } = "beta";

    public SetupUpdatePolicySection UpdatePolicy { get; init; } = new();
}

public sealed class SetupUpdatePolicySection
{
    /// <summary>automatic or manual. Shipped default: automatic for security (doc 45, owner gap decision 2026-11-13).</summary>
    public string Security { get; init; } = "automatic";

    /// <summary>automatic or manual. Shipped default: manual for everything else.</summary>
    public string Other { get; init; } = "manual";
}

public sealed class SetupDatabaseSection
{
    public int PostgresMajor { get; init; } = 18;

    /// <summary>Data root. Null means the host provider's platform default (for example %ProgramData%\Bazaar on Windows).</summary>
    public string? DataRoot { get; init; }

    /// <summary>GC-log hash partitions, fixed before the first GC data (doc 04, setting gc.partition_count).</summary>
    public int GcPartitionCount { get; init; } = 16;
}

public sealed class SetupCustodySection
{
    /// <summary>The key-encryption key provider: env, file, dpapi or systemd-creds. Required.</summary>
    public string KekProvider { get; init; } = "";

    /// <summary>A bootstrap reference (env:, file:, dpapi: or systemd-creds:) that must match <see cref="KekProvider"/>. Required.</summary>
    public string KekRef { get; init; } = "";

    /// <summary>
    /// The identity KEK provider (doc 14 <c>identity.kek_provider</c>): file, dpapi, openbao or tpm. Optional; absent means the server
    /// default (dpapi on Windows, file elsewhere). Separate from <see cref="KekProvider"/>, which is how the bootstrap secret is held.
    /// Left out of the canonical form when absent, so a plan without it keeps its hash.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityKek { get; init; }

    /// <summary>Where the audit anchor is published, as an absolute https URL. Null means no external anchor.</summary>
    public string? AuditAnchorTarget { get; init; }

    public SetupMergeCredentialSection MergeCredential { get; init; } = new();
}

public sealed class SetupMergeCredentialSection
{
    /// <summary>owner_app (a dedicated owner GitHub App) or owner_linked (the owner's linked GitHub credential), doc 14 and 21.</summary>
    public string Kind { get; init; } = "owner_linked";

    /// <summary>A secret:// reference. Required when Kind is owner_app, and must be absent otherwise.</summary>
    public string? AppIdRef { get; init; }
}

public sealed class SetupOwnerSection
{
    /// <summary>Required. Shown to people; no default.</summary>
    public string DisplayName { get; init; } = "";

    public bool RequireSecondFactor { get; init; } = true;

    public SetupRecoverySection Recovery { get; init; } = new();
}

public sealed class SetupRecoverySection
{
    public bool PrintedCodes { get; init; }

    /// <summary>Usernames of backup owners. Order is kept; it is a list, not a set.</summary>
    public List<string> BackupOwners { get; init; } = [];
}

public sealed class SetupPrivacySection
{
    /// <summary>Only "private" is accepted (doc 45 decided inputs: instances are private by default).</summary>
    public string Profile { get; init; } = "private";
}

public sealed class SetupBackupsSection
{
    /// <summary>A backup target such as file:/mnt/backup/bazaar. Null needs <see cref="AcknowledgeNoTarget"/>.</summary>
    public string? Target { get; init; }

    public int DrillIntervalDays { get; init; } = 30;

    public bool AcknowledgeNoTarget { get; init; }
}

public sealed class SetupModelsSection
{
    public List<SetupModelSource> Sources { get; init; } = [];

    public string DefaultPreset { get; init; } = "free-lane";
}

public sealed class SetupModelSource
{
    public string Id { get; init; } = "";

    /// <summary>A secret:// reference, or null when the source needs no key.</summary>
    public string? KeyRef { get; init; }
}

public sealed class SetupEmailSection
{
    public bool Enabled { get; init; }
}

public sealed class SetupScamMailSection
{
    /// <summary>Optional Nigerian Prince Protocol step (doc 45 section 1.11). Off by default.</summary>
    public bool Enabled { get; init; }
}

public sealed class SetupCompanySection
{
    /// <summary>blank, template:&lt;id&gt;@&lt;semver&gt; or import:&lt;id&gt;. Required.</summary>
    public string Source { get; init; } = "";
}

public sealed class SetupNetworkSection
{
    /// <summary>Maps to the registry key host.status.bind. Anything but loopback is held for approval (Tier 2).</summary>
    public string StatusBind { get; init; } = "loopback";

    /// <summary>Maps to the registry key host.status.port. Must be 1 to 65535 in a plan.</summary>
    public int StatusPort { get; init; } = 8443;
}
