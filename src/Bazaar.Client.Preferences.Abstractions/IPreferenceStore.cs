namespace Bazaar.Client.Preferences.Abstractions;

/// <summary>Why a <see cref="IPreferenceStore.Set"/> was refused.</summary>
public enum PreferenceRefusal
{
    None,
    UnknownKey,
    InvalidValue,
}

/// <summary>The result of a set. <see cref="Reason"/> is empty when the set was accepted.</summary>
public readonly record struct PreferenceResult(PreferenceRefusal Refusal, string Reason)
{
    public bool Accepted => Refusal == PreferenceRefusal.None;

    public static PreferenceResult Ok { get; } = new(PreferenceRefusal.None, string.Empty);
}

/// <summary>
/// The durable client preference store. A set is validated against the registry definition, saved locally at once, and
/// handed to the server through <see cref="IPreferenceSync"/> (<c>prefs.set</c>, doc 02). Nothing is read or written for a
/// key the registry does not declare.
/// </summary>
public interface IPreferenceStore
{
    /// <summary>Raised with the key after an accepted change, from any source (local set or a server update).</summary>
    event Action<string>? Changed;

    /// <summary>The canonical string value of <paramref name="key"/>, or null for an undeclared key.</summary>
    string? GetValue(string key);

    bool GetBool(string key);

    long GetInt(string key);

    /// <summary>Validates, saves locally and queues the change for the server.</summary>
    PreferenceResult TrySet(string key, string value);

    /// <summary>Applies a value the server confirmed or changed (<c>ui.changed</c>). A local change still waiting to sync wins.</summary>
    PreferenceResult ApplyFromServer(string key, string value);

    /// <summary>True when <paramref name="key"/> is declared.</summary>
    bool IsDeclared(string key);
}

/// <summary>Sends one accepted change to the server. A failure keeps the change pending for the next flush.</summary>
public interface IPreferenceSync
{
    Task PushAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>Durable storage for the store's values and the keys still waiting to reach the server.</summary>
public interface IPreferenceStorage
{
    PreferenceSnapshot Load();

    void Save(PreferenceSnapshot snapshot);
}

/// <summary>What the storage holds: set values and the keys not yet pushed.</summary>
public sealed record PreferenceSnapshot(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Pending)
{
    public static PreferenceSnapshot Empty { get; } = new(new Dictionary<string, string>(), []);
}

/// <summary>
/// The server refused a change for good (an unknown key, a value the registry rejects, or a key this person may not
/// change). The store drops the local change instead of retrying it forever.
/// </summary>
public sealed class PreferenceRejectedException : Exception
{
    public PreferenceRejectedException(string message)
        : base(message)
    {
    }

    public PreferenceRejectedException()
    {
    }

    public PreferenceRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
