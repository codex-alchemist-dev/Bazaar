using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.OpsPorts;

/// <summary>
/// Where the resident TUI's file-backed ports read and write (doc 58 section 3). Every value is an operator setting under
/// <c>Bazaar:Ops</c> with a safe default: the files sit in one data directory and hold no secret. Producers (the server's log sink,
/// Kodo and the slot rollback) append to the same files through <see cref="OpsLogFileWriter"/> and <see cref="OpsEventFileWriter"/>.
/// </summary>
public sealed record OpsPortsOptions
{
    public const string DataDirectoryKey = "Bazaar:Ops:DataDirectory";
    public const string LogFileKey = "Bazaar:Ops:LogFile";
    public const string EventFileKey = "Bazaar:Ops:EventFile";
    public const string CrashJournalFileKey = "Bazaar:Ops:CrashJournalFile";
    public const string OwnerPrincipalKey = "Bazaar:Ops:OwnerPrincipalId";
    public const string MaxReadBytesKey = "Bazaar:Ops:MaxReadBytes";

    /// <summary>The most a read takes from the end of a file. Bounds memory when a log file is large.</summary>
    public const int DefaultMaxReadBytes = 4 * 1024 * 1024;

    private const int _minReadBytes = 4 * 1024;
    private const int _maxReadBytesCeiling = 64 * 1024 * 1024;

    public required string DataDirectory { get; init; }

    public required string LogFile { get; init; }

    public required string EventFile { get; init; }

    public required string CrashJournalFile { get; init; }

    /// <summary>The owner's principal id when more than one human holds the owner role. Null means the gate admits only a sole owner.</summary>
    public Guid? OwnerPrincipalId { get; init; }

    public int MaxReadBytes { get; init; } = DefaultMaxReadBytes;

    /// <summary>The default data directory: a <c>Bazaar/ops</c> folder under the machine-wide application data folder.</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bazaar", "ops");

    public static OpsPortsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = NonBlank(configuration[DataDirectoryKey]) ?? DefaultDataDirectory();
        Guid? owner = Guid.TryParse(configuration[OwnerPrincipalKey], out var parsed) ? parsed : null;
        var maxRead = int.TryParse(configuration[MaxReadBytesKey], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? Math.Clamp(bytes, _minReadBytes, _maxReadBytesCeiling)
            : DefaultMaxReadBytes;
        return new OpsPortsOptions
        {
            DataDirectory = directory,
            LogFile = NonBlank(configuration[LogFileKey]) ?? Path.Combine(directory, "server-log.jsonl"),
            EventFile = NonBlank(configuration[EventFileKey]) ?? Path.Combine(directory, "health-events.jsonl"),
            CrashJournalFile = NonBlank(configuration[CrashJournalFileKey]) ?? Path.Combine(directory, "tui-crashes.jsonl"),
            OwnerPrincipalId = owner,
            MaxReadBytes = maxRead,
        };
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
