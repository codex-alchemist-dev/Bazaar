namespace Bazaar.Server.GcMetadata;

/// <summary>Reads and writes the typed keys of <c>gc.metadata</c> and leaves every other key alone (docs/design/01-gc-data-model.md, GC metadata).</summary>
public sealed class GcMetadataCodec
{
    private readonly GcMetadataSchema _schema;

    /// <summary>Creates a codec over <paramref name="schema"/>, or <see cref="GcMetadataSchema.Default"/>.</summary>
    public GcMetadataCodec(GcMetadataSchema? schema = null)
    {
        _schema = schema ?? GcMetadataSchema.Default;
        if (_schema.AllowedStatuses.Count == 0)
            throw new ArgumentException("The status list must not be empty.", nameof(schema));
        foreach (var s in _schema.AllowedStatuses)
        {
            if (!IsSlug(s, GcMetadataSchema.MaxKindLength, allowDot: false))
                throw new ArgumentException($"Status '{s}' is not a lower-case slug.", nameof(schema));
        }
    }

    /// <summary>Reads the typed keys. An invalid typed value is reported in <see cref="GcMetadataView.Issues"/> and read as null.</summary>
    public GcMetadataView Read(IReadOnlyDictionary<string, string>? metadata)
    {
        var issues = new List<GcMetadataIssue>();
        var other = new Dictionary<string, string>(StringComparer.Ordinal);
        string? project = null, kind = null, status = null;

        foreach (var (key, value) in metadata ?? new Dictionary<string, string>())
        {
            switch (key)
            {
                case GcMetadataKeys.Project: project = Check(key, value, issues); break;
                case GcMetadataKeys.Kind: kind = Check(key, value, issues); break;
                case GcMetadataKeys.Status: status = Check(key, value, issues); break;
                default: other[key] = value; break;
            }
        }

        return new GcMetadataView(project, kind, status, other, issues);
    }

    /// <summary>Applies <paramref name="patch"/> to <paramref name="current"/>. A refused patch returns no metadata, so nothing is half-written.</summary>
    public GcMetadataApplyResult Apply(IReadOnlyDictionary<string, string>? current, GcMetadataPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var issues = new List<GcMetadataIssue>();
        var result = new Dictionary<string, string>(current ?? new Dictionary<string, string>(), StringComparer.Ordinal);

        Set(result, GcMetadataKeys.Project, patch.Project, issues);
        Set(result, GcMetadataKeys.Kind, patch.Kind, issues);
        Set(result, GcMetadataKeys.Status, patch.Status, issues);

        return issues.Count == 0 ? new GcMetadataApplyResult(result, []) : new GcMetadataApplyResult(null, issues);
    }

    private void Set(Dictionary<string, string> target, string key, string? value, List<GcMetadataIssue> issues)
    {
        if (value is null)
            return;
        if (value.Length == 0)
        {
            target.Remove(key);
            return;
        }

        if (Check(key, value, issues) is { } ok)
            target[key] = ok;
    }

    private string? Check(string key, string value, List<GcMetadataIssue> issues)
    {
        var message = key switch
        {
            GcMetadataKeys.Project when !IsSlug(value, GcMetadataSchema.MaxProjectLength, allowDot: true)
                => $"Project must be lower-case letters, digits, '.', '_' or '-', up to {GcMetadataSchema.MaxProjectLength} characters.",
            GcMetadataKeys.Kind when !IsSlug(value, GcMetadataSchema.MaxKindLength, allowDot: false)
                => $"Kind must be lower-case letters, digits, '_' or '-', up to {GcMetadataSchema.MaxKindLength} characters.",
            GcMetadataKeys.Status when !_schema.AllowedStatuses.Contains(value, StringComparer.Ordinal)
                => $"Status must be one of: {string.Join(", ", _schema.AllowedStatuses)}.",
            _ => null,
        };

        if (message is null)
            return value;

        issues.Add(new GcMetadataIssue(key, message));
        return null;
    }

    private static bool IsSlug(string value, int max, bool allowDot)
    {
        if (value.Length is 0 || value.Length > max)
            return false;
        if (!char.IsAsciiLetterLower(value[0]) && !char.IsAsciiDigit(value[0]))
            return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' || (allowDot && c == '.')))
                return false;
        }

        return true;
    }
}
