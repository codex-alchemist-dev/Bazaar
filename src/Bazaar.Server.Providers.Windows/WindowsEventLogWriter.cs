using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Providers.Windows;

/// <summary>
/// Writes warnings and lifecycle events to the Windows Event Log (doc 03). The installer creates the source;
/// the runtime never creates it. Logging must not take the host down, so writes report failure as <c>false</c> instead of throwing.
/// </summary>
public interface IEventLogWriter
{
    /// <summary>Reports whether this writer can write right now. Does not change state.</summary>
    ProviderStatus Probe();

    /// <summary>Writes one entry. Returns false when nothing was written: unusable, a level below Information, or a failed write.</summary>
    bool Write(LogLevel level, string message);
}

/// <summary>The Event Log implementation of <see cref="IEventLogWriter"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWriter : IEventLogWriter
{
    /// <summary>The event source the installer registers.</summary>
    public const string DefaultSource = "Bazaar.Server";

    /// <summary>The kind name reported in probes and the startup log.</summary>
    public const string KindName = "windows_event_log";

    private readonly string _source;
    private readonly Func<string, bool> _sourceExists;

    /// <param name="source">The event source name. Must be non-blank.</param>
    /// <param name="sourceExists">Registration check. Defaults to <see cref="EventLog.SourceExists(string)"/>; tests inject a fake.</param>
    public WindowsEventLogWriter(string source = DefaultSource, Func<string, bool>? sourceExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _source = source;
        _sourceExists = sourceExists ?? EventLog.SourceExists;
    }

    /// <summary>Usable only on Windows, and only when the installer has registered the source.</summary>
    public ProviderStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProviderStatus(KindName, false, [], "requires Windows");
        }

        try
        {
            return _sourceExists(_source)
                ? new ProviderStatus(KindName, true, [], null)
                : new ProviderStatus(KindName, false, [], $"event source '{_source}' is not registered; the installer creates it");
        }
        catch (SecurityException)
        {
            // A non-elevated user cannot read the Security log, and SourceExists scans it. Report "cannot check" and keep running.
            return new ProviderStatus(KindName, false, [], $"event source '{_source}' could not be checked without elevation");
        }
    }

    /// <inheritdoc />
    public bool Write(LogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var entryType = level switch
        {
            LogLevel.Critical or LogLevel.Error => EventLogEntryType.Error,
            LogLevel.Warning => EventLogEntryType.Warning,
            LogLevel.Information => EventLogEntryType.Information,
            _ => (EventLogEntryType?)null,
        };

        if (entryType is null || !Probe().Usable)
        {
            return false;
        }

        try
        {
            EventLog.WriteEntry(_source, message, entryType.Value);
            return true;
        }
        catch (Exception)
        {
            // Any failure, including an oversized message or a lost source, means the entry is not written. The host keeps running.
            return false;
        }
    }
}
