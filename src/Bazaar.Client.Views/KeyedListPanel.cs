using Avalonia.Controls;

namespace Bazaar.Client.Views;

/// <summary>
/// A vertical list of item controls keyed by one field. Each item is built with the single template on first sight of its key.
/// On an update, an item whose key is unchanged keeps its control if it is the same record; a new record under a known key is
/// rebuilt, and a key that is gone is removed. Duplicate or missing keys throw before anything changes.
/// </summary>
public sealed class KeyedListPanel : StackPanel
{
    private readonly string _keyField;
    private readonly Func<IReadOnlyDictionary<string, object?>, Control> _template;
    private readonly Dictionary<string, (IReadOnlyDictionary<string, object?> Record, Control Control)> _byKey = new(StringComparer.Ordinal);

    public KeyedListPanel(string keyField, Func<IReadOnlyDictionary<string, object?>, Control> template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyField);
        ArgumentNullException.ThrowIfNull(template);

        _keyField = keyField;
        _template = template;
    }

    /// <summary>How many times the item template has run. A test seam; it counts builds, not renders.</summary>
    public int TemplateBuilds { get; private set; }

    /// <summary>Replaces the items. Order follows <paramref name="items"/>.</summary>
    public void Update(IEnumerable<IReadOnlyDictionary<string, object?>> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var records = items.ToList();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, object?> record in records)
        {
            string key = KeyOf(record);
            if (!keys.Add(key))
            {
                throw new ViewDeclarationException($"duplicate list key '{key}'.");
            }
        }

        var ordered = new List<Control>(records.Count);
        foreach (IReadOnlyDictionary<string, object?> record in records)
        {
            string key = KeyOf(record);
            if (!_byKey.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Record, record))
            {
                entry = (record, _template(record));
                TemplateBuilds++;
                _byKey[key] = entry;
            }

            ordered.Add(entry.Control);
        }

        foreach (string gone in _byKey.Keys.Where(key => !keys.Contains(key)).ToList())
        {
            _byKey.Remove(gone);
        }

        Children.Clear();
        foreach (Control control in ordered)
        {
            Children.Add(control);
        }
    }

    private string KeyOf(IReadOnlyDictionary<string, object?> record)
    {
        if (!record.TryGetValue(_keyField, out object? value) || value is not string key || key.Length == 0)
        {
            throw new ViewDeclarationException($"list item has no text key field '{_keyField}'.");
        }

        return key;
    }
}
