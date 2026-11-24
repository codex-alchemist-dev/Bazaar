using System.Text;
using System.Text.Json;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// Raised when the model's arguments do not match a handler's schema. The message names the schema field and the reason,
/// never the value the model sent.
/// </summary>
internal sealed class CodingToolArgumentException(string field, string reason) : Exception($"{field} {reason}")
{
    /// <summary>The schema field that failed.</summary>
    public string Field { get; } = field;

    /// <summary>What was wrong with it.</summary>
    public string Reason { get; } = reason;

    /// <summary>The structured error the model receives.</summary>
    public CodingToolOutput ToOutput() =>
        CodingToolResults.Failure(CodingToolResults.MalformedCode, $"Argument '{Field}' {Reason}.", CodingToolResults.MalformedExitCode);
}

/// <summary>
/// Reads one call's arguments against a closed schema. Unknown fields, wrong JSON types, empty required strings and out-of-range
/// numbers are all rejected with <see cref="CodingToolArgumentException"/>; nothing is coerced.
/// </summary>
internal sealed class CodingToolArgs
{
    private static readonly JsonElement _emptyObject = JsonElement.Parse("{}");

    private readonly JsonElement _root;

    /// <summary>Reads <paramref name="args"/>; <paramref name="allowed"/> is the complete list of field names.</summary>
    public CodingToolArgs(JsonElement args, params string[] allowed)
    {
        _root = args.ValueKind == JsonValueKind.Undefined ? _emptyObject : args;
        if (_root.ValueKind != JsonValueKind.Object)
        {
            throw new CodingToolArgumentException("arguments", "must be a JSON object");
        }

        foreach (var property in _root.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                // The model's key is not echoed back: only the fact that an unknown field was sent.
                throw new CodingToolArgumentException("arguments", "contains a field that is not in this tool's schema");
            }
        }
    }

    /// <summary>A non-blank string of at most <paramref name="maxLength"/> characters.</summary>
    public string RequiredString(string name, int maxLength)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new CodingToolArgumentException(name, "is required");
        }

        return ReadString(name, value, maxLength, allowEmpty: false);
    }

    /// <summary>A string when present; null when absent or JSON null.</summary>
    public string? OptionalString(string name, int maxLength, bool allowEmpty = false)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(name, value, maxLength, allowEmpty);
    }

    /// <summary>A whole number in [<paramref name="min"/>, <paramref name="max"/>], required.</summary>
    public int RequiredInt(string name, int min, int max)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new CodingToolArgumentException(name, "is required");
        }

        return ReadInt(name, value, min, max);
    }

    /// <summary>A whole number in [<paramref name="min"/>, <paramref name="max"/>] when present, else <paramref name="fallback"/>.</summary>
    public int OptionalInt(string name, int min, int max, int fallback)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return ReadInt(name, value, min, max);
    }

    /// <summary>A boolean when present, else <paramref name="fallback"/>.</summary>
    public bool OptionalBool(string name, bool fallback)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new CodingToolArgumentException(name, "must be a boolean");
        }

        return value.GetBoolean();
    }

    /// <summary>An array of non-blank strings when present; null when absent or JSON null.</summary>
    public IReadOnlyList<string>? OptionalStringArray(string name, int maxItems, int maxLength)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CodingToolArgumentException(name, "must be an array of strings");
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (items.Count == maxItems)
            {
                throw new CodingToolArgumentException(name, $"must have at most {maxItems} items");
            }

            items.Add(ReadString(name, item, maxLength, allowEmpty: false));
        }

        return items;
    }

    private static string ReadString(string name, JsonElement value, int maxLength, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CodingToolArgumentException(name, "must be a string");
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new CodingToolArgumentException(name, "must not be blank");
        }

        if (text.Length > maxLength)
        {
            throw new CodingToolArgumentException(name, $"must be at most {maxLength} characters");
        }

        return text;
    }

    private static int ReadInt(string name, JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new CodingToolArgumentException(name, "must be a whole number");
        }

        if (number < min || number > max)
        {
            throw new CodingToolArgumentException(name, $"must be from {min} to {max}");
        }

        return number;
    }
}

/// <summary>Result builders shared by the handlers. Every output is JSON with no host paths and no secrets.</summary>
internal static class CodingToolResults
{
    /// <summary>Error code for arguments that do not match the schema.</summary>
    public const string MalformedCode = "malformed_args";

    /// <summary>Exit code for a usage error (EX_USAGE): the call itself is wrong.</summary>
    public const int MalformedExitCode = 64;

    /// <summary>Exit code for a tool that ran and could not do its job.</summary>
    public const int FailedExitCode = 1;

    /// <summary>Builds a structured error with a fixed message. Callers never pass model text or host paths as the message.</summary>
    public static CodingToolOutput Failure(string code, string message, int exitCode = FailedExitCode) =>
        new(new ExecOutcome(exitCode), Json(w =>
        {
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
        }));

    /// <summary>Writes a JSON object with <paramref name="write"/> and returns its text.</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Turns a PascalCase enum or error name into snake_case, for the codes the model sees.</summary>
    public static string SnakeCase(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
