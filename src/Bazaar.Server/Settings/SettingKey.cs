using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Bazaar.Server.Settings;

/// <summary>
/// A typed setting key. <typeparamref name="T"/> is bool, int, string, an enum (wire form: snake_case name) or a read-only
/// list of those. <paramref name="validate"/> returns the reason a value is invalid, or null; floors live there, so a
/// value that loosens a floor is rejected however it arrives. <paramref name="requiresApprovalFor"/> decides, per value,
/// whether <paramref name="approvalTier"/> applies (for example, a non-loopback bind).
/// </summary>
public sealed class SettingKey<T> : SettingDefinition
{
    private readonly Func<T, string?>? _validate;
    private readonly Func<T, bool>? _requiresApprovalFor;

    public SettingKey(
        string name,
        T defaultValue,
        SettingScope scope,
        SettingRole whoMayChange,
        string description,
        ApprovalTier approvalTier = ApprovalTier.None,
        Func<T, string?>? validate = null,
        Func<T, bool>? requiresApprovalFor = null)
        : base(name, scope, whoMayChange, approvalTier, description)
    {
        Default = defaultValue;
        _validate = validate;
        _requiresApprovalFor = requiresApprovalFor;

        // A default that fails its own validator is a bug in the registry, so fail at startup, not at first read.
        var error = validate?.Invoke(defaultValue);
        if (error is not null)
            throw new ArgumentException($"Default of '{name}' is invalid: {error}", nameof(defaultValue));
    }

    public T Default { get; }

    public override Type ValueType => typeof(T);

    public override string DefaultValueJson => Serialize(Default);

    /// <summary>Reads a stored value. Throws <see cref="InvalidSettingValueException"/> if it is not valid for this key.</summary>
    public T Read(JsonElement stored)
    {
        if (!TryParse(stored, out var value, out var error))
            throw new InvalidSettingValueException(Name, error);
        var floor = _validate?.Invoke(value);
        if (floor is not null)
            throw new InvalidSettingValueException(Name, floor);
        return value;
    }

    public override string? CheckValue(JsonElement value)
    {
        if (!TryParse(value, out var parsed, out var error))
            return error;
        return _validate?.Invoke(parsed);
    }

    public override SettingEvaluation Evaluate(SettingActor actor, JsonElement proposed)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var raw = proposed.GetRawText();

        if (!actor.Has(WhoMayChange))
            return new SettingEvaluation(SettingWriteOutcome.Rejected, ApprovalTier.None, $"'{Name}' may be changed only by {WhoMayChange}.", raw);

        if (!TryParse(proposed, out var value, out var parseError))
            return new SettingEvaluation(SettingWriteOutcome.Rejected, ApprovalTier.None, parseError, raw);

        var invalid = _validate?.Invoke(value);
        if (invalid is not null)
            return new SettingEvaluation(SettingWriteOutcome.Rejected, ApprovalTier.None, invalid, raw);

        var canonical = Serialize(value);
        var needsApproval = ApprovalTier != ApprovalTier.None && (_requiresApprovalFor is null || _requiresApprovalFor(value));
        if (needsApproval)
            return new SettingEvaluation(SettingWriteOutcome.PendingApproval, ApprovalTier, $"'{Name}' needs {ApprovalTier} approval; the change is held.", canonical);

        return new SettingEvaluation(SettingWriteOutcome.Accepted, ApprovalTier.None, null, canonical);
    }

    private bool TryParse(JsonElement json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (json.ValueKind == JsonValueKind.Null)
        {
            error = $"'{Name}' cannot be null.";
            return false;
        }

        try
        {
            value = json.Deserialize<T>(SettingJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            error = $"'{Name}' expects a {Describe()} value; got {json.ValueKind}.";
            return false;
        }

        if (value is null)
        {
            error = $"'{Name}' expects a {Describe()} value.";
            return false;
        }

        error = null;
        return true;
    }

    private static string Serialize(T value) => JsonSerializer.Serialize(value, SettingJson.Options);

    private static string Describe() => typeof(T) switch
    {
        var t when t == typeof(bool) => "true or false",
        var t when t == typeof(int) => "whole number",
        var t when t == typeof(string) => "text",
        var t when t.IsEnum => "one of the listed names",
        _ => "list",
    };
}
