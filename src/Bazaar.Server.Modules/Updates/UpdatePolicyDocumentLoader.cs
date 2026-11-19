using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Modules.Updates;

/// <summary>The kinds of problem an update policy document can have. Each one is reported, never thrown.</summary>
public enum UpdatePolicyProblemKind
{
    /// <summary>The text is not JSON, or the JSON cannot be read into the document shape.</summary>
    MalformedJson,

    /// <summary>The schema version is not one this build reads.</summary>
    UnknownSchemaVersion,

    /// <summary>A property the schema does not define. Refused, not ignored.</summary>
    UnknownProperty,

    /// <summary>A required property is absent.</summary>
    MissingProperty,

    /// <summary>A value has the wrong JSON type, for example a string where a boolean belongs.</summary>
    WrongType,

    /// <summary>A string names no known enum member, day or zone form.</summary>
    UnknownEnumValue,

    /// <summary>A value is the right type but not a valid value, for example an empty name, a bad time or an unknown time zone.</summary>
    InvalidValue,

    /// <summary>A maintenance window fails <see cref="MaintenanceWindowValidator"/>. The detail names the issue.</summary>
    InvalidWindow,

    /// <summary>Two policies have the same scope, so one of them could never win.</summary>
    DuplicateScope,

    /// <summary>An auto policy is pinned to a pre-release version type. Beta and release candidates are never automatic.</summary>
    PreReleaseAutoRefused,
}

/// <summary>One problem found in a document. <see cref="Path"/> is a JSON-path-like location such as <c>$.policies[2].action</c>.</summary>
public sealed record UpdatePolicyProblem(string Path, UpdatePolicyProblemKind Kind, string Detail);

/// <summary>
/// The result of loading a document. <see cref="Document"/> is set only when <see cref="Problems"/> is empty, so a loaded
/// result always carries the policies and a refused one always carries the reasons.
/// </summary>
public sealed record UpdatePolicyLoadResult(UpdatePolicyDocument? Document, IReadOnlyList<UpdatePolicyProblem> Problems)
{
    /// <summary>True when the document loaded and <see cref="Policies"/> can be handed to the evaluator.</summary>
    public bool IsLoaded => Document is not null;

    /// <summary>The loaded policies in document order, or an empty list when the document was refused.</summary>
    public IReadOnlyList<UpdatePolicy> Policies => Document?.Policies ?? [];
}

/// <summary>
/// Loads and writes owner update policy documents (doc 49 section 7, "Policy-as-code"). Loading never throws for bad input: a
/// bad document comes back as a list of typed problems. Reading goes in three steps: the JSON must parse, the shape must
/// match the schema (no unknown properties, required properties present, value types right), and then the values must make
/// sense (known enum names, valid times and zones, valid windows, no duplicate scopes, no automatic pre-release policy).
/// </summary>
public static class UpdatePolicyDocumentLoader
{
    private const string _rootPath = "$";

    private static readonly string[] _rootProperties = ["schemaVersion", "policies"];
    private static readonly string[] _entryProperties = ["scope", "conditions", "windows", "action"];
    private static readonly string[] _scopeProperties = ["moduleId", "category", "versionType", "channel"];
    private static readonly string[] _conditionProperties = ["isSecurity", "matrixPassed"];
    private static readonly string[] _windowProperties = ["days", "start", "end", "zone"];
    private static readonly string[] _zoneProperties = ["timeZone", "utcOffset"];

    /// <summary>Loads a document from JSON text. Returns problems, not an exception, for any bad input.</summary>
    public static UpdatePolicyLoadResult Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Failed(new UpdatePolicyProblem(_rootPath, UpdatePolicyProblemKind.MalformedJson, ex.Message));
        }

        using (parsed)
        {
            var problems = new List<UpdatePolicyProblem>();
            CheckShape(parsed.RootElement, problems);
            if (problems.Count > 0)
            {
                return Failed(problems);
            }
        }

        UpdatePolicyDocumentDto dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, UpdatePolicyDocumentJsonContext.Default.UpdatePolicyDocumentDto)!;
        }
        catch (JsonException ex)
        {
            return Failed(new UpdatePolicyProblem(ex.Path ?? _rootPath, UpdatePolicyProblemKind.MalformedJson, ex.Message));
        }

        var values = new List<UpdatePolicyProblem>();
        var document = MapDocument(dto, values);
        return values.Count == 0 ? new UpdatePolicyLoadResult(document, []) : Failed(values);
    }

    /// <summary>Writes a document as JSON. The output loads back to an equal document.</summary>
    /// <exception cref="ArgumentException">A model value cannot be written in the schema, such as a fixed offset that is not whole minutes.</exception>
    public static string Serialize(UpdatePolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var dto = new UpdatePolicyDocumentDto
        {
            SchemaVersion = UpdatePolicyDocument.SchemaVersion,
            Policies = document.Policies.Select(ToEntryDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, UpdatePolicyDocumentJsonContext.Default.UpdatePolicyDocumentDto);
    }

    private static UpdatePolicyLoadResult Failed(UpdatePolicyProblem problem) => new(null, [problem]);

    private static UpdatePolicyLoadResult Failed(List<UpdatePolicyProblem> problems) => new(null, problems);

    // Step 2: the shape. Checks unknown and missing properties and JSON value types, so the typed read cannot fail on them.

    private static void CheckShape(JsonElement root, List<UpdatePolicyProblem> problems)
    {
        if (!ExpectKind(root, _rootPath, JsonValueKind.Object, "a JSON object", problems))
        {
            return;
        }

        if (!root.TryGetProperty("schemaVersion", out var version))
        {
            problems.Add(Missing(_rootPath, "schemaVersion"));
            return;
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
        {
            problems.Add(new UpdatePolicyProblem($"{_rootPath}.schemaVersion", UpdatePolicyProblemKind.WrongType, "The schema version must be a whole number."));
            return;
        }

        // A different version may have a different shape, so no further checks run against it.
        if (schemaVersion != UpdatePolicyDocument.SchemaVersion)
        {
            problems.Add(new UpdatePolicyProblem(
                $"{_rootPath}.schemaVersion",
                UpdatePolicyProblemKind.UnknownSchemaVersion,
                $"Schema version {schemaVersion} is not supported. This build reads version {UpdatePolicyDocument.SchemaVersion}."));
            return;
        }

        CheckKnownProperties(root, _rootPath, _rootProperties, problems);

        if (!RequireProperty(root, _rootPath, "policies", problems, out var policies)
            || !ExpectKind(policies, $"{_rootPath}.policies", JsonValueKind.Array, "a JSON array", problems))
        {
            return;
        }

        var index = 0;
        foreach (var entry in policies.EnumerateArray())
        {
            CheckEntry(entry, $"{_rootPath}.policies[{index}]", problems);
            index++;
        }
    }

    private static void CheckEntry(JsonElement entry, string path, List<UpdatePolicyProblem> problems)
    {
        if (!ExpectKind(entry, path, JsonValueKind.Object, "a JSON object", problems))
        {
            return;
        }

        CheckKnownProperties(entry, path, _entryProperties, problems);
        RequireString(entry, path, "action", problems);

        if (entry.TryGetProperty("scope", out var scope) && ExpectKind(scope, $"{path}.scope", JsonValueKind.Object, "a JSON object", problems))
        {
            CheckKnownProperties(scope, $"{path}.scope", _scopeProperties, problems);
            foreach (var name in _scopeProperties)
            {
                OptionalString(scope, $"{path}.scope", name, problems);
            }
        }

        if (entry.TryGetProperty("conditions", out var conditions) && ExpectKind(conditions, $"{path}.conditions", JsonValueKind.Object, "a JSON object", problems))
        {
            CheckKnownProperties(conditions, $"{path}.conditions", _conditionProperties, problems);
            foreach (var name in _conditionProperties)
            {
                OptionalBoolean(conditions, $"{path}.conditions", name, problems);
            }
        }

        if (entry.TryGetProperty("windows", out var windows) && ExpectKind(windows, $"{path}.windows", JsonValueKind.Array, "a JSON array", problems))
        {
            var index = 0;
            foreach (var window in windows.EnumerateArray())
            {
                CheckWindow(window, $"{path}.windows[{index}]", problems);
                index++;
            }
        }
    }

    private static void CheckWindow(JsonElement window, string path, List<UpdatePolicyProblem> problems)
    {
        if (!ExpectKind(window, path, JsonValueKind.Object, "a JSON object", problems))
        {
            return;
        }

        CheckKnownProperties(window, path, _windowProperties, problems);

        if (RequireProperty(window, path, "days", problems, out var days) && ExpectKind(days, $"{path}.days", JsonValueKind.Array, "a JSON array", problems))
        {
            var index = 0;
            foreach (var day in days.EnumerateArray())
            {
                ExpectKind(day, $"{path}.days[{index}]", JsonValueKind.String, "a string", problems);
                index++;
            }
        }

        RequireString(window, path, "start", problems);
        RequireString(window, path, "end", problems);

        if (RequireProperty(window, path, "zone", problems, out var zone) && ExpectKind(zone, $"{path}.zone", JsonValueKind.Object, "a JSON object", problems))
        {
            CheckKnownProperties(zone, $"{path}.zone", _zoneProperties, problems);
            foreach (var name in _zoneProperties)
            {
                OptionalString(zone, $"{path}.zone", name, problems);
            }
        }
    }

    private static void CheckKnownProperties(JsonElement obj, string path, string[] known, List<UpdatePolicyProblem> problems)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                problems.Add(new UpdatePolicyProblem(
                    $"{path}.{property.Name}",
                    UpdatePolicyProblemKind.UnknownProperty,
                    $"Unknown property '{property.Name}'. Remove it or check the spelling."));
            }
        }
    }

    private static bool RequireProperty(JsonElement obj, string path, string name, List<UpdatePolicyProblem> problems, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        problems.Add(Missing(path, name));
        return false;
    }

    private static void RequireString(JsonElement obj, string path, string name, List<UpdatePolicyProblem> problems)
    {
        if (RequireProperty(obj, path, name, problems, out var value))
        {
            ExpectKind(value, $"{path}.{name}", JsonValueKind.String, "a string", problems);
        }
    }

    private static void OptionalString(JsonElement obj, string path, string name, List<UpdatePolicyProblem> problems)
    {
        if (obj.TryGetProperty(name, out var value))
        {
            ExpectKind(value, $"{path}.{name}", JsonValueKind.String, "a string", problems);
        }
    }

    private static void OptionalBoolean(JsonElement obj, string path, string name, List<UpdatePolicyProblem> problems)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add(new UpdatePolicyProblem($"{path}.{name}", UpdatePolicyProblemKind.WrongType, "Expected true or false."));
        }
    }

    private static bool ExpectKind(JsonElement value, string path, JsonValueKind kind, string expected, List<UpdatePolicyProblem> problems)
    {
        if (value.ValueKind == kind)
        {
            return true;
        }

        problems.Add(new UpdatePolicyProblem(path, UpdatePolicyProblemKind.WrongType, $"Expected {expected}."));
        return false;
    }

    private static UpdatePolicyProblem Missing(string path, string name) =>
        new($"{path}.{name}", UpdatePolicyProblemKind.MissingProperty, $"The required property '{name}' is missing.");

    // Step 3: the values. The shape is known to be right here, so the typed read cannot fail.

    private static UpdatePolicyDocument? MapDocument(UpdatePolicyDocumentDto dto, List<UpdatePolicyProblem> problems)
    {
        var policies = new List<UpdatePolicy>();
        var scopes = new HashSet<(string?, string?, VersionType?, string?)>();

        for (var i = 0; i < dto.Policies.Count; i++)
        {
            var path = $"{_rootPath}.policies[{i}]";
            var policy = MapPolicy(dto.Policies[i], path, problems);
            if (policy is null)
            {
                continue;
            }

            var scope = policy.Scope;
            if (!scopes.Add((scope.ModuleId, scope.Category, scope.VersionType, scope.Channel)))
            {
                problems.Add(new UpdatePolicyProblem(
                    $"{path}.scope",
                    UpdatePolicyProblemKind.DuplicateScope,
                    "An earlier policy in this document has the same scope. Keep one entry per scope."));
            }

            policies.Add(policy);
        }

        return problems.Count == 0 ? new UpdatePolicyDocument(policies) : null;
    }

    private static UpdatePolicy? MapPolicy(UpdatePolicyEntryDto dto, string path, List<UpdatePolicyProblem> problems)
    {
        var before = problems.Count;

        var scope = dto.Scope is null ? new UpdatePolicyScope() : MapScope(dto.Scope, $"{path}.scope", problems);
        var conditions = new UpdatePolicyConditions(dto.Conditions?.IsSecurity, dto.Conditions?.MatrixPassed);
        var action = ReadAction(dto.Action, $"{path}.action", problems);
        var windows = MapWindows(dto.Windows ?? [], path, problems);

        if (action == UpdatePolicyAction.Auto && scope.VersionType is VersionType.Beta or VersionType.ReleaseCandidate)
        {
            problems.Add(new UpdatePolicyProblem(
                $"{path}.action",
                UpdatePolicyProblemKind.PreReleaseAutoRefused,
                "An auto policy cannot be scoped to a beta or release candidate version type. Those are never automatic (doc 42 section 10.1)."));
        }

        if (problems.Count > before || action is null)
        {
            return null;
        }

        return new UpdatePolicy(scope, conditions, windows, action.Value);
    }

    private static UpdatePolicyScope MapScope(UpdatePolicyScopeDto dto, string path, List<UpdatePolicyProblem> problems) => new(
        NonEmpty(dto.ModuleId, $"{path}.moduleId", problems),
        NonEmpty(dto.Category, $"{path}.category", problems),
        ReadVersionType(dto.VersionType, $"{path}.versionType", problems),
        NonEmpty(dto.Channel, $"{path}.channel", problems));

    private static List<MaintenanceWindow> MapWindows(List<MaintenanceWindowDto> dtos, string path, List<UpdatePolicyProblem> problems)
    {
        var windows = new List<MaintenanceWindow>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var window = MapWindow(dtos[i], $"{path}.windows[{i}]", problems);
            if (window is not null)
            {
                windows.Add(window);
            }
        }

        // The set is only checked when every window mapped, so the validator's indexes match the document's.
        if (windows.Count == dtos.Count)
        {
            foreach (var problem in MaintenanceWindowValidator.Validate(windows).Problems)
            {
                var detail = problem.OtherWindowIndex is { } other
                    ? $"{problem.Issue}, with window {other}."
                    : $"{problem.Issue}.";
                problems.Add(new UpdatePolicyProblem($"{path}.windows[{problem.WindowIndex}]", UpdatePolicyProblemKind.InvalidWindow, detail));
            }
        }

        return windows;
    }

    private static MaintenanceWindow? MapWindow(MaintenanceWindowDto dto, string path, List<UpdatePolicyProblem> problems)
    {
        var before = problems.Count;

        var days = new List<DayOfWeek>(dto.Days.Count);
        for (var i = 0; i < dto.Days.Count; i++)
        {
            var day = ReadDay(dto.Days[i], $"{path}.days[{i}]", problems);
            if (day is { } value)
            {
                days.Add(value);
            }
        }

        var start = ReadTime(dto.Start, $"{path}.start", problems);
        var end = ReadTime(dto.End, $"{path}.end", problems);
        var zone = ReadZone(dto.Zone, $"{path}.zone", problems);

        if (problems.Count > before || start is null || end is null || zone is null)
        {
            return null;
        }

        return new MaintenanceWindow(days, start.Value, end.Value, zone);
    }

    private static string? NonEmpty(string? value, string path, List<UpdatePolicyProblem> problems)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(new UpdatePolicyProblem(path, UpdatePolicyProblemKind.InvalidValue, "The value must not be empty."));
            return null;
        }

        return value;
    }

    private static UpdatePolicyAction? ReadAction(string value, string path, List<UpdatePolicyProblem> problems)
    {
        var action = value switch
        {
            "auto" => UpdatePolicyAction.Auto,
            "manual" => UpdatePolicyAction.Manual,
            "defer" => UpdatePolicyAction.Defer,
            "pin" => UpdatePolicyAction.Pin,
            "skip" => UpdatePolicyAction.Skip,
            "requireApproval" => UpdatePolicyAction.RequireApproval,
            _ => (UpdatePolicyAction?)null,
        };

        if (action is null)
        {
            problems.Add(UnknownEnum(path, "action", value, "auto, manual, defer, pin, skip or requireApproval"));
        }

        return action;
    }

    private static VersionType? ReadVersionType(string? value, string path, List<UpdatePolicyProblem> problems)
    {
        if (value is null)
        {
            return null;
        }

        var versionType = value switch
        {
            "generalAvailability" => VersionType.GeneralAvailability,
            "releaseCandidate" => VersionType.ReleaseCandidate,
            "beta" => VersionType.Beta,
            _ => (VersionType?)null,
        };

        if (versionType is null)
        {
            problems.Add(UnknownEnum(path, "version type", value, "generalAvailability, releaseCandidate or beta"));
        }

        return versionType;
    }

    private static DayOfWeek? ReadDay(string value, string path, List<UpdatePolicyProblem> problems)
    {
        var day = value switch
        {
            "sunday" => DayOfWeek.Sunday,
            "monday" => DayOfWeek.Monday,
            "tuesday" => DayOfWeek.Tuesday,
            "wednesday" => DayOfWeek.Wednesday,
            "thursday" => DayOfWeek.Thursday,
            "friday" => DayOfWeek.Friday,
            "saturday" => DayOfWeek.Saturday,
            _ => (DayOfWeek?)null,
        };

        if (day is null)
        {
            problems.Add(UnknownEnum(path, "day", value, "lowercase day names such as monday"));
        }

        return day;
    }

    private static readonly Regex _timeRegex = new(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant);

    private static TimeOnly? ReadTime(string value, string path, List<UpdatePolicyProblem> problems)
    {
        if (_timeRegex.IsMatch(value))
        {
            return TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        }

        problems.Add(new UpdatePolicyProblem(path, UpdatePolicyProblemKind.InvalidValue, $"'{value}' is not a time in HH:mm form."));
        return null;
    }

    private static MaintenanceZone? ReadZone(MaintenanceZoneDto dto, string path, List<UpdatePolicyProblem> problems)
    {
        if ((dto.TimeZone is null) == (dto.UtcOffset is null))
        {
            problems.Add(new UpdatePolicyProblem(path, UpdatePolicyProblemKind.InvalidValue, "Set exactly one of timeZone or utcOffset."));
            return null;
        }

        if (dto.TimeZone is { } zoneId)
        {
            try
            {
                return MaintenanceZone.Named(TimeZoneInfo.FindSystemTimeZoneById(zoneId));
            }
            catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                problems.Add(new UpdatePolicyProblem(
                    $"{path}.timeZone",
                    UpdatePolicyProblemKind.InvalidValue,
                    $"'{zoneId}' is not a time zone this system knows."));
                return null;
            }
        }

        var offset = ParseOffset(dto.UtcOffset!);
        if (offset is null || !MaintenanceZone.Fixed(offset.Value).IsValid)
        {
            problems.Add(new UpdatePolicyProblem(
                $"{path}.utcOffset",
                UpdatePolicyProblemKind.InvalidValue,
                $"'{dto.UtcOffset}' is not an offset in +hh:mm or -hh:mm form within -14:00 to +14:00."));
            return null;
        }

        return MaintenanceZone.Fixed(offset.Value);
    }

    private static UpdatePolicyProblem UnknownEnum(string path, string what, string value, string allowed) =>
        new(path, UpdatePolicyProblemKind.UnknownEnumValue, $"Unknown {what} '{value}'. Use {allowed}.");

    /// <summary>Parses a strict <c>+hh:mm</c> or <c>-hh:mm</c> offset. Returns null for any other text.</summary>
    private static TimeSpan? ParseOffset(string value)
    {
        if (value.Length != 6 || value[0] is not ('+' or '-') || value[3] != ':')
        {
            return null;
        }

        if (!int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59)
        {
            return null;
        }

        var offset = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? offset.Negate() : offset;
    }

    // Writing. The model is written in the schema's own forms; a value the schema cannot hold is refused, not rounded.

    private static UpdatePolicyEntryDto ToEntryDto(UpdatePolicy policy) => new()
    {
        Scope = policy.Scope == new UpdatePolicyScope() ? null : ToScopeDto(policy.Scope),
        Conditions = policy.Conditions == new UpdatePolicyConditions() ? null : ToConditionsDto(policy.Conditions),
        Windows = policy.Windows.Count == 0 ? null : policy.Windows.Select(ToWindowDto).ToList(),
        Action = ActionName(policy.Action),
    };

    private static UpdatePolicyScopeDto ToScopeDto(UpdatePolicyScope scope) => new()
    {
        ModuleId = scope.ModuleId,
        Category = scope.Category,
        VersionType = scope.VersionType is { } versionType ? VersionTypeName(versionType) : null,
        Channel = scope.Channel,
    };

    private static UpdatePolicyConditionsDto ToConditionsDto(UpdatePolicyConditions conditions) => new()
    {
        IsSecurity = conditions.IsSecurity,
        MatrixPassed = conditions.MatrixPassed,
    };

    private static MaintenanceWindowDto ToWindowDto(MaintenanceWindow window)
    {
        if (!window.Zone.IsValid)
        {
            throw new ArgumentException("A maintenance window zone must be a known time zone or an offset within -14:00 to +14:00.", nameof(window));
        }

        return new MaintenanceWindowDto
        {
            Days = window.Days.Select(DayName).ToList(),
            Start = FormatTime(window.Start),
            End = FormatTime(window.End),
            Zone = window.Zone.NamedZone is { } named
                ? new MaintenanceZoneDto { TimeZone = named.Id }
                : new MaintenanceZoneDto { UtcOffset = FormatOffset(window.Zone.FixedOffset!.Value) },
        };
    }

    private static string FormatTime(TimeOnly time)
    {
        if (time.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("A maintenance window time must be a whole minute.", nameof(time));
        }

        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatOffset(TimeSpan offset)
    {
        if (offset.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("A fixed UTC offset must be a whole number of minutes.", nameof(offset));
        }

        var magnitude = offset.Duration();
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)magnitude.TotalHours:00}:{magnitude.Minutes:00}");
    }

    private static string ActionName(UpdatePolicyAction action) => action switch
    {
        UpdatePolicyAction.Auto => "auto",
        UpdatePolicyAction.Manual => "manual",
        UpdatePolicyAction.Defer => "defer",
        UpdatePolicyAction.Pin => "pin",
        UpdatePolicyAction.Skip => "skip",
        UpdatePolicyAction.RequireApproval => "requireApproval",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a known update policy action."),
    };

    private static string VersionTypeName(VersionType versionType) => versionType switch
    {
        VersionType.GeneralAvailability => "generalAvailability",
        VersionType.ReleaseCandidate => "releaseCandidate",
        VersionType.Beta => "beta",
        _ => throw new ArgumentOutOfRangeException(nameof(versionType), versionType, "Not a known version type."),
    };

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "sunday",
        DayOfWeek.Monday => "monday",
        DayOfWeek.Tuesday => "tuesday",
        DayOfWeek.Wednesday => "wednesday",
        DayOfWeek.Thursday => "thursday",
        DayOfWeek.Friday => "friday",
        DayOfWeek.Saturday => "saturday",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Not a known day of the week."),
    };
}
