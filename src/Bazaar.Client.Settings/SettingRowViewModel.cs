using System.Globalization;
using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Client.Settings;

/// <summary>
/// One setting in the pane. The row never decides what is allowed: it asks the registry definition (<see cref="SettingDefinition.Evaluate"/>)
/// before it calls the server, so the registry's refusal text, floors and approval rules are the only rules (doc 03).
/// </summary>
public sealed class SettingRowViewModel
{
    private readonly ISettingsClient _client;
    private readonly SettingActor _actor;

    internal SettingRowViewModel(SettingsEntry entry, SettingActor actor, ISettingsClient client)
    {
        Definition = entry.Definition;
        _actor = actor;
        _client = client;
        StoredJson = entry.ValueJson;
        EditorKind = KindOf(Definition.ValueType);
        Choices = Definition.ValueType.IsEnum
            ? [.. Enum.GetNames(Definition.ValueType).Select(n => JsonNamingPolicy.SnakeCaseLower.ConvertName(n))]
            : [];
        ReadOnlyReason = ReasonReadOnly();
        _draft = CurrentText;
    }

    private string _draft;

    /// <summary>Raised after the row's state changed (draft, error, status or stored value).</summary>
    public event Action? Changed;

    public SettingDefinition Definition { get; }

    public string Name => Definition.Name;

    /// <summary>The area: the key's first dotted segment (<c>host</c> for <c>host.status.bind</c>).</summary>
    public string Area => Definition.Name.Split('.')[0];

    public string Description => Definition.Description;

    public SettingEditorKind EditorKind { get; }

    /// <summary>Choice names in wire form, for <see cref="SettingEditorKind.Choice"/>; empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The stored JSON, or null when unset.</summary>
    public string? StoredJson { get; private set; }

    public bool IsDefault => StoredJson is null;

    public string DefaultText => Display(Definition.DefaultValueJson);

    public string CurrentText => Display(StoredJson ?? Definition.DefaultValueJson);

    public string ScopeText => Definition.Scope.ToString();

    public string WhoMayChangeText => Definition.WhoMayChange.ToString();

    /// <summary>Why the signed-in role cannot change this key; null when it can.</summary>
    public string? ReadOnlyReason { get; }

    public bool CanEdit => ReadOnlyReason is null;

    /// <summary>True when a change may need approval; for a value-dependent key the exact answer comes from <see cref="Draft"/>.</summary>
    public bool NeedsApproval => Definition.ApprovalTier != ApprovalTier.None;

    public string? ApprovalText => NeedsApproval
        ? $"A change can need {Definition.ApprovalTier} approval (owner); it is held until approved."
        : null;

    /// <summary>The proposed value as text (the editor's content). Setting it re-validates.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            _draft = value ?? string.Empty;
            Validate();
            Changed?.Invoke();
        }
    }

    /// <summary>The registry's refusal for the current draft, or the parse error; null when the draft is valid.</summary>
    public string? Error { get; private set; }

    /// <summary>Set when the draft is valid but the registry says the change would be held for approval.</summary>
    public string? PendingNote { get; private set; }

    /// <summary>The outcome of the last save, in the registry's or server's words.</summary>
    public string? Status { get; private set; }

    public bool IsDirty => !string.Equals(Draft, CurrentText, StringComparison.Ordinal);

    public bool CanSave => CanEdit && IsDirty && Error is null;

    /// <summary>Checks the draft against the registry definition. Returns the evaluation, or null when the row is read-only or the draft is not a value of the type.</summary>
    public SettingEvaluation? Validate()
    {
        Error = null;
        PendingNote = null;
        if (!CanEdit)
        {
            return null;
        }

        if (!TryToJson(out var json, out var parseError))
        {
            Error = parseError;
            return null;
        }

        var evaluation = Definition.Evaluate(_actor, json);
        switch (evaluation.Outcome)
        {
            case SettingWriteOutcome.Rejected:
                Error = evaluation.Reason;
                break;
            case SettingWriteOutcome.PendingApproval:
                PendingNote = evaluation.Reason;
                break;
        }

        return evaluation;
    }

    /// <summary>Sends the draft to the server if the registry accepts it. A refused draft never reaches the wire.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Status = null;
        var evaluation = Validate();
        if (evaluation is null || evaluation.Outcome == SettingWriteOutcome.Rejected)
        {
            Changed?.Invoke();
            return;
        }

        SettingsChangeResult result;
        try
        {
            result = await _client.SetAsync(Name, evaluation.ValueJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new SettingsChangeResult(SettingWriteOutcome.Rejected, ex.Message);
        }

        switch (result.Outcome)
        {
            case SettingWriteOutcome.Accepted:
                StoredJson = evaluation.ValueJson;
                Error = null;
                PendingNote = null;
                Status = "Saved.";
                _draft = CurrentText;
                break;
            case SettingWriteOutcome.PendingApproval:
                Status = result.Reason ?? "The change is held until it is approved. It is not applied yet.";
                break;
            default:
                Error = result.Reason ?? "The server refused the change.";
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>Puts the draft back to the current value.</summary>
    public void Revert()
    {
        Status = null;
        Draft = CurrentText;
    }

    private string? ReasonReadOnly()
    {
        if (EditorKind == SettingEditorKind.ReadOnlyValue)
        {
            return "This value is a list and is not edited here.";
        }

        return _actor.Has(Definition.WhoMayChange)
            ? null
            : $"Read-only: only {Definition.WhoMayChange} may change this; you are signed in as {(_actor.Roles == SettingRole.None ? "no role" : _actor.Roles.ToString())}.";
    }

    private bool TryToJson(out JsonElement json, out string? error)
    {
        error = null;
        json = default;
        var text = Draft.Trim();
        switch (EditorKind)
        {
            case SettingEditorKind.Toggle:
                if (!string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Choose on or off.";
                    return false;
                }

                json = JsonSerializer.SerializeToElement(string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));
                return true;
            case SettingEditorKind.Number:
                if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || (Definition.ValueType == typeof(int) && (number != decimal.Truncate(number) || number is < int.MinValue or > int.MaxValue)))
                {
                    error = Definition.ValueType == typeof(int) ? "Enter a whole number." : "Enter a number.";
                    return false;
                }

                json = JsonElement.Parse(number.ToString(CultureInfo.InvariantCulture));
                return true;
            case SettingEditorKind.Choice:
                if (!Choices.Contains(text, StringComparer.Ordinal))
                {
                    error = $"Choose one of: {string.Join(", ", Choices)}.";
                    return false;
                }

                json = JsonSerializer.SerializeToElement(text);
                return true;
            case SettingEditorKind.Text:
                json = JsonSerializer.SerializeToElement(Draft);
                return true;
            default:
                error = ReadOnlyReason;
                return false;
        }
    }

    private static SettingEditorKind KindOf(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool))
        {
            return SettingEditorKind.Toggle;
        }

        if (t.IsEnum)
        {
            return SettingEditorKind.Choice;
        }

        if (t == typeof(int) || t == typeof(long) || t == typeof(decimal) || t == typeof(double))
        {
            return SettingEditorKind.Number;
        }

        return t == typeof(string) ? SettingEditorKind.Text : SettingEditorKind.ReadOnlyValue;
    }

    private static string Display(string json)
    {
        try
        {
            var root = JsonElement.Parse(json);
            return root.ValueKind switch
            {
                JsonValueKind.String => root.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join(", ", root.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                _ => root.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
