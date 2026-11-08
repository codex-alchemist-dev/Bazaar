using System.ComponentModel;
using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Core.Panes;

/// <summary>One row of an engine pane: a top-level model field and its value as display text.</summary>
public sealed record EnginePaneField(string Name, string Value);

/// <summary>
/// Holds an engine pane snapshot (docs/design/20-engine-integration-api.md, Panes). A snapshot is replaced only
/// by a newer revision, so a reconnecting client never shows an older view over a newer one. Data only: no Avalonia types.
/// </summary>
public sealed class EnginePaneViewModel : INotifyPropertyChanged
{
    /// <summary>Longer values are cut to this many characters so a large model cannot stall the UI.</summary>
    public const int MaxValueLength = 1000;

    private PaneDescriptor _descriptor;
    private IReadOnlyList<EnginePaneField> _fields;

    public EnginePaneViewModel(PaneDescriptor descriptor)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _fields = ReadFields(descriptor.Model);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Namespaced pane kind, for example <c>godot.scene-tree</c>. Fixed for the life of the view model.</summary>
    public string Kind => _descriptor.Kind;

    public string Title => _descriptor.Title;

    public EngineCapability Capability => _descriptor.Capability;

    /// <summary>Revision of the snapshot currently shown.</summary>
    public long Revision => _descriptor.Revision;

    /// <summary>Top-level model fields in the order the server sent them. A non-object model shows one "value" row.</summary>
    public IReadOnlyList<EnginePaneField> Fields => _fields;

    /// <summary>
    /// Replaces the snapshot when the descriptor's revision is newer than the one shown. Equal or older revisions are ignored.
    /// Returns true when the snapshot was replaced. Throws <see cref="ArgumentException"/> when the kind differs.
    /// </summary>
    public bool Apply(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!string.Equals(descriptor.Kind, Kind, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected pane kind '{Kind}' but got '{descriptor.Kind}'.", nameof(descriptor));
        }

        if (descriptor.Revision <= Revision)
        {
            return false;
        }

        _descriptor = descriptor;
        _fields = ReadFields(descriptor.Model);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Capability)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fields)));
        return true;
    }

    private static EnginePaneField[] ReadFields(JsonElement model)
    {
        switch (model.ValueKind)
        {
            case JsonValueKind.Undefined:
                return [];
            case JsonValueKind.Object:
                return model.EnumerateObject()
                    .Select(property => new EnginePaneField(property.Name, DisplayText(property.Value)))
                    .ToArray();
            default:
                return [new EnginePaneField("value", DisplayText(model))];
        }
    }

    private static string DisplayText(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "...";
    }
}
