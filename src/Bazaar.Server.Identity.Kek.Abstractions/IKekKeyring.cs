namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// An operating-system secret service that seals the <c>file</c> KEK provider's key file (doc 61 Q2, DECIDED): the Linux Secret
/// Service (libsecret) or Windows DPAPI. Each is a provider behind this interface; nothing outside a provider calls an OS-only API.
/// The keyring's own key is random and independent of the KEK, so the KEK is never sealed by a key derived from itself.
/// </summary>
public interface IKekKeyring
{
    /// <summary>Stable kind written into the sealed key file, for example <c>libsecret</c> or <c>dpapi</c>. A file sealed by one kind never opens under another.</summary>
    string Kind { get; }

    /// <summary>Reports whether the keyring can be used now and whether it already holds an entry for <paramref name="entryName"/>. Never throws for an unreachable keyring.</summary>
    ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken);

    /// <summary>Seals <paramref name="key"/> for storage in the key file, bound to <paramref name="associatedData"/>. Creates the keyring entry when needed.</summary>
    /// <exception cref="KekKeyringException">The keyring refused or could not answer.</exception>
    ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken);

    /// <summary>Opens bytes made by <see cref="SealAsync"/>. Never creates an entry.</summary>
    /// <exception cref="KekKeyringException">The entry is missing, the keyring cannot answer, or the bytes do not authenticate.</exception>
    ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="IKekKeyring.ProbeAsync"/>. <see cref="Reason"/> names the failure and never holds key material.</summary>
public sealed record KekKeyringProbe(bool Available, bool HasEntry, string? Reason)
{
    public static KekKeyringProbe Unavailable(string reason) => new(false, false, reason);
}

/// <summary>Thrown when the keyring cannot give a definite answer, or a sealed key file cannot be opened. Never carries key material.</summary>
public sealed class KekKeyringException : Exception
{
    public KekKeyringException(string message)
        : base(message)
    {
    }

    public KekKeyringException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Operator setting <c>Bazaar:Identity:KekKeyring</c> (doc 61 Q2). <see cref="Auto"/> (default, the DECIDED behaviour) seals the key
/// file with the keyring where one is usable and otherwise keeps the protected file with an audited warning. <see cref="Required"/>
/// tightens it: no keyring fails the start. There is no value that turns the keyring off where it works.
/// </summary>
public enum KekKeyringMode
{
    Auto,
    Required,
}

/// <summary>Receives KEK custody events (doc 48 section 5), for example <c>kek.keyring_fallback</c>. A failed record fails the open.</summary>
public interface IKekCustodyAuditSink
{
    ValueTask RecordAsync(KekCustodyAuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <summary>A KEK custody event. <see cref="Detail"/> never contains key material.</summary>
public sealed record KekCustodyAuditEvent(DateTimeOffset At, string Kind, string KeyringKind, string Detail);
