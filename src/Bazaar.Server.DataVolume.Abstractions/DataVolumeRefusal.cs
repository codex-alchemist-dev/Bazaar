namespace Bazaar.Server.DataVolume;

/// <summary>Why the host refuses to start on its data folder (doc 63 section 5). There is no override for any of them.</summary>
public enum DataVolumeRefusal
{
    /// <summary>The boot root, the descriptor or the KEK is missing. The host never creates them at start; setup does.</summary>
    NotInitialised,

    /// <summary>The data root does not exist or is not the root of a mounted volume.</summary>
    NotMounted,

    /// <summary>The data root is not on an encrypted volume with protection on, or its cipher is below the floor.</summary>
    NotEncrypted,

    /// <summary>The volume, descriptor or host config belongs to another install, or the descriptor was changed.</summary>
    IdentityMismatch,

    /// <summary>The marker inside the volume is missing, edited or from another volume.</summary>
    MarkerInvalid,

    /// <summary>A PostgreSQL directory (data, WAL, tablespace) resolves outside the verified data root.</summary>
    PostgresFilesOutsideVolume,

    /// <summary>Setup only: a descriptor, a container or data already exists where <c>storage init</c> would create one. Nothing is overwritten.</summary>
    AlreadyInitialised,
}

/// <summary>Thrown when a data volume check fails. The host logs one line and exits with <see cref="ExitCode"/>.</summary>
public sealed class DataVolumeRefusedException : Exception
{
    /// <summary>The process exit code for every data volume refusal (<c>EX_CONFIG</c>).</summary>
    public const int ExitCode = 78;

    public DataVolumeRefusedException(DataVolumeRefusal reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public DataVolumeRefusal Reason { get; }

    /// <summary>The stable reason code written to the log and the audit entry.</summary>
    public string Code => Reason switch
    {
        DataVolumeRefusal.NotInitialised => "data_volume_not_initialised",
        DataVolumeRefusal.NotMounted => "data_volume_not_mounted",
        DataVolumeRefusal.NotEncrypted => "data_volume_not_encrypted",
        DataVolumeRefusal.IdentityMismatch => "data_volume_identity_mismatch",
        DataVolumeRefusal.MarkerInvalid => "data_volume_marker_invalid",
        DataVolumeRefusal.PostgresFilesOutsideVolume => "postgres_files_outside_volume",
        DataVolumeRefusal.AlreadyInitialised => "data_volume_already_initialised",
        _ => "data_volume_refused",
    };
}
