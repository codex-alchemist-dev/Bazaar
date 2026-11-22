using System.Globalization;
using Bazaar.Client.Preferences.Abstractions;

namespace Bazaar.Client.Preferences;

/// <summary>
/// The durable preference store. Thread-safe: one lock guards the values and the pending set; <see cref="Changed"/> and the
/// sync call run outside it. Loading drops stored values that no longer validate, so a removed or tightened registry entry
/// falls back to its default instead of failing the client.
/// </summary>
public sealed class PreferenceStore : IPreferenceStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreferenceDefinition> _definitions;
    private readonly Dictionary<string, string> _values;
    private readonly List<string> _pending;
    private readonly IPreferenceStorage _storage;
    private readonly IPreferenceSync? _sync;

    public PreferenceStore(IEnumerable<PreferenceDefinition> definitions, IPreferenceStorage storage, IPreferenceSync? sync = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(storage);

        _definitions = new Dictionary<string, PreferenceDefinition>(StringComparer.Ordinal);
        foreach (PreferenceDefinition definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Key, definition))
            {
                throw new ArgumentException($"Preference '{definition.Key}' is declared twice.", nameof(definitions));
            }

            if (!Validate(definition, definition.Default).Accepted)
            {
                throw new ArgumentException($"The default of preference '{definition.Key}' is not valid.", nameof(definitions));
            }
        }

        _storage = storage;
        _sync = sync;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        _pending = [];

        PreferenceSnapshot snapshot = storage.Load();
        foreach ((string key, string value) in snapshot.Values)
        {
            if (_definitions.TryGetValue(key, out PreferenceDefinition? definition) && Validate(definition, value).Accepted)
            {
                _values[key] = value;
            }
        }

        _pending.AddRange(snapshot.Pending.Where(k => _values.ContainsKey(k)).Distinct(StringComparer.Ordinal));
    }

    public event Action<string>? Changed;

    /// <summary>Keys changed locally and not yet accepted by the server.</summary>
    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    public bool IsDeclared(string key) => _definitions.ContainsKey(key);

    public string? GetValue(string key)
    {
        if (!_definitions.TryGetValue(key, out PreferenceDefinition? definition))
        {
            return null;
        }

        lock (_gate)
        {
            return _values.TryGetValue(key, out string? value) ? value : definition.Default;
        }
    }

    public bool GetBool(string key) => string.Equals(GetValue(key), "true", StringComparison.Ordinal);

    public long GetInt(string key) =>
        long.TryParse(GetValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    public PreferenceResult TrySet(string key, string value)
    {
        PreferenceResult result = Check(key, value, out string canonical);
        if (!result.Accepted)
        {
            return result;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(GetValue(key), canonical, StringComparison.Ordinal);
            _values[key] = canonical;
            if (!_pending.Contains(key, StringComparer.Ordinal))
            {
                _pending.Add(key);
            }

            Persist();
        }

        if (changed)
        {
            Changed?.Invoke(key);
        }

        return PreferenceResult.Ok;
    }

    public PreferenceResult ApplyFromServer(string key, string value)
    {
        PreferenceResult result = Check(key, value, out string canonical);
        if (!result.Accepted)
        {
            return result;
        }

        bool changed;
        lock (_gate)
        {
            if (_pending.Contains(key, StringComparer.Ordinal))
            {
                return PreferenceResult.Ok;
            }

            changed = !string.Equals(GetValue(key), canonical, StringComparison.Ordinal);
            _values[key] = canonical;
            Persist();
        }

        if (changed)
        {
            Changed?.Invoke(key);
        }

        return PreferenceResult.Ok;
    }

    /// <summary>Pushes every pending change in the order it was made. A failed push stops the flush and stays pending.</summary>
    /// <returns>The number of changes the server accepted.</returns>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        if (_sync is null)
        {
            return 0;
        }

        int sent = 0;
        while (true)
        {
            string key;
            string value;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return sent;
                }

                key = _pending[0];
                value = GetValue(key)!;
            }

            try
            {
                await _sync.PushAsync(key, value, cancellationToken).ConfigureAwait(false);
            }
            catch (PreferenceRejectedException)
            {
                // A permanent refusal: drop the local change so the key returns to its default and the queue moves on.
                lock (_gate)
                {
                    _pending.Remove(key);
                    _values.Remove(key);
                    Persist();
                }

                Changed?.Invoke(key);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent;
            }

            lock (_gate)
            {
                // A newer local set of the same key during the push stays pending.
                if (string.Equals(GetValue(key), value, StringComparison.Ordinal))
                {
                    _pending.Remove(key);
                    Persist();
                }
                else
                {
                    _pending.Remove(key);
                    _pending.Add(key);
                }
            }

            sent++;
        }
    }

    private PreferenceResult Check(string key, string value, out string canonical)
    {
        canonical = value;
        if (!_definitions.TryGetValue(key, out PreferenceDefinition? definition))
        {
            return new PreferenceResult(PreferenceRefusal.UnknownKey, $"Preference '{key}' is not declared.");
        }

        PreferenceResult result = Validate(definition, value);
        if (result.Accepted)
        {
            canonical = Canonical(definition, value);
        }

        return result;
    }

    private static string Canonical(PreferenceDefinition definition, string value) => definition.Kind switch
    {
        PreferenceKind.Bool => value.ToLowerInvariant(),
        PreferenceKind.WholeNumber => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => value,
    };

    private static PreferenceResult Validate(PreferenceDefinition definition, string value)
    {
        static PreferenceResult Bad(string reason) => new(PreferenceRefusal.InvalidValue, reason);

        if (value is null)
        {
            return Bad("A value is required.");
        }

        switch (definition.Kind)
        {
            case PreferenceKind.Bool:
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? PreferenceResult.Ok
                    : Bad("Expected true or false.");
            case PreferenceKind.WholeNumber:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                {
                    return Bad("Expected a whole number.");
                }

                return (definition.Min is { } min && number < min) || (definition.Max is { } max && number > max)
                    ? Bad($"Outside the allowed range {definition.Min}..{definition.Max}.")
                    : PreferenceResult.Ok;
            case PreferenceKind.Choice:
                return definition.Choices is { } choices && choices.Contains(value, StringComparer.Ordinal)
                    ? PreferenceResult.Ok
                    : Bad("Not one of the allowed choices.");
            default:
                return definition.Max is { } cap && value.Length > cap ? Bad($"Longer than {cap} characters.") : PreferenceResult.Ok;
        }
    }

    private void Persist() => _storage.Save(new PreferenceSnapshot(new Dictionary<string, string>(_values), [.. _pending]));
}
