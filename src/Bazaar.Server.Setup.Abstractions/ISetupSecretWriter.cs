namespace Bazaar.Server.Setup;

/// <summary>
/// Stores one secret value under a <c>secret://bazaar/...</c> reference (doc 45 section 1.6; the store is ISecretStore, doc 14).
/// The value is handed over once and never returned. Implementations must not put the value in an exception message, a log line
/// or an audit row; callers treat any exception as a failed write and print no detail from it.
/// </summary>
public interface ISetupSecretWriter
{
    /// <summary>True when a real secret store is registered. A false value means <see cref="WriteAsync"/> must not be called.</summary>
    bool StoreAvailable { get; }

    /// <summary>Stores <paramref name="value"/> under <paramref name="reference"/>. Only called when <see cref="StoreAvailable"/> is true.</summary>
    Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default);
}
