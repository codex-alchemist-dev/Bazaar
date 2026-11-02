namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>Why a Rojo operation failed.</summary>
public enum RojoErrorKind
{
    /// <summary>The configured binary could not be started (missing file or not executable).</summary>
    BinaryNotFound,

    /// <summary>The process ran past <see cref="RojoOptions.Timeout"/> and was killed.</summary>
    Timeout,

    /// <summary>rojo exited with a non-zero code.</summary>
    NonZeroExit,

    /// <summary><c>rojo --version</c> printed nothing usable.</summary>
    EmptyVersionOutput,

    /// <summary>The reported version does not match <see cref="RojoOptions.RequiredVersion"/>.</summary>
    VersionMismatch,

    /// <summary>The project file or root directory does not exist.</summary>
    ProjectNotFound,

    /// <summary>The output's directory does not exist.</summary>
    OutputDirectoryNotFound,

    /// <summary>A path resolves outside the root, or passes through a symbolic link or junction.</summary>
    PathOutsideRoot,
}

/// <summary>A Rojo operation failed. <see cref="StandardError"/> holds captured stderr when rojo ran.</summary>
public sealed class RojoException(RojoErrorKind kind, string message, int? exitCode = null, string? standardError = null)
    : Exception(message)
{
    public RojoErrorKind Kind { get; } = kind;

    public int? ExitCode { get; } = exitCode;

    public string? StandardError { get; } = standardError;
}
