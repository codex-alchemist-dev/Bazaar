using System.Text.Json;
using Bazaar.Rubedo;

namespace Bazaar.Rubedo.Views;

/// <summary>
/// Reads the JSON wire form of a view document (doc 41c, sections 2 and 4 to 6) into a <see cref="ViewDocument"/>.
/// Shape problems (wrong JSON types, unknown action kinds, unknown data types) throw <see cref="FormatException"/>.
/// Semantic problems (unknown widgets, bad expressions, missing labels) are left to <see cref="ViewValidator"/>, so the
/// validator can report them with stable codes. Pure: no I/O.
/// </summary>
public static class ViewDocumentReader
{
    /// <summary>Parses a JSON view document.</summary>
    public static ViewDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return ReadRoot(document.RootElement);
    }

    private static ViewDocument ReadRoot(JsonElement root)
    {
        RequireObject(root, "document");
        int dslVersion = RequireInt(root, "dslVersion", "document");
        string name = RequireString(root, "name", "document");

        var data = new Dictionary<string, ViewDataSource>(StringComparer.Ordinal);
        if (root.TryGetProperty("data", out JsonElement dataElement))
        {
            RequireObject(dataElement, "data");
            foreach (JsonProperty property in dataElement.EnumerateObject())
            {
                data[property.Name] = ReadDataSource(property.Value, $"data.{property.Name}");
            }
        }

        var actions = new Dictionary<string, ViewAction>(StringComparer.Ordinal);
        if (root.TryGetProperty("actions", out JsonElement actionsElement))
        {
            RequireObject(actionsElement, "actions");
            foreach (JsonProperty property in actionsElement.EnumerateObject())
            {
                actions[property.Name] = ReadAction(property.Value, $"actions.{property.Name}");
            }
        }

        if (!root.TryGetProperty("root", out JsonElement rootElement))
        {
            throw new FormatException("document: missing 'root'.");
        }

        return new ViewDocument(dslVersion, name, data, ReadNode(rootElement, "root"), actions);
    }

    private static ViewDataSource ReadDataSource(JsonElement element, string path)
    {
        RequireObject(element, path);
        string capability = RequireString(element, "capability", path);
        string type = RequireString(element, "type", path);
        if (type == "list")
        {
            if (!element.TryGetProperty("fields", out JsonElement fieldsElement))
            {
                throw new FormatException($"{path}: a list source needs 'fields'.");
            }

            RequireObject(fieldsElement, $"{path}.fields");
            var fields = new Dictionary<string, RubedoType>(StringComparer.Ordinal);
            foreach (JsonProperty field in fieldsElement.EnumerateObject())
            {
                fields[field.Name] = ReadScalarType(field.Value, $"{path}.fields.{field.Name}");
            }

            return new ViewDataSource(capability, null, fields);
        }

        return new ViewDataSource(capability, ReadScalarType(element.GetProperty("type"), $"{path}.type"));
    }

    private static RubedoType ReadScalarType(JsonElement element, string path)
    {
        string type = element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new FormatException($"{path}: must be a string.");
        return type switch
        {
            "bool" => RubedoType.Bool,
            "number" => RubedoType.Number,
            "text" => RubedoType.Text,
            _ => throw new FormatException($"{path}: unknown data type '{type}'."),
        };
    }

    private static ViewAction ReadAction(JsonElement element, string path)
    {
        RequireObject(element, path);
        string kind = RequireString(element, "kind", path);
        ViewActionKind actionKind = kind switch
        {
            "navigate" => ViewActionKind.Navigate,
            "invoke" => ViewActionKind.Invoke,
            "openPane" => ViewActionKind.OpenPane,
            "setSetting" => ViewActionKind.SetSetting,
            _ => throw new FormatException($"{path}: unknown action kind '{kind}'."),
        };

        string? target = null;
        if (element.TryGetProperty("target", out JsonElement targetElement))
        {
            target = targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString()
                : throw new FormatException($"{path}.target: must be a string.");
        }

        return new ViewAction(actionKind, target);
    }

    private static ViewNode ReadNode(JsonElement element, string path)
    {
        RequireObject(element, path);
        string widget = RequireString(element, "widget", path);

        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.TryGetProperty("props", out JsonElement propsElement))
        {
            RequireObject(propsElement, $"{path}.props");
            foreach (JsonProperty property in propsElement.EnumerateObject())
            {
                props[property.Name] = ReadProp(property.Value, $"{path}.props.{property.Name}");
            }
        }

        var children = new List<ViewNode>();
        if (element.TryGetProperty("children", out JsonElement childrenElement))
        {
            if (childrenElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{path}.children: must be an array.");
            }

            int index = 0;
            foreach (JsonElement child in childrenElement.EnumerateArray())
            {
                children.Add(ReadNode(child, $"{path}/{index}"));
                index++;
            }
        }

        return new ViewNode(widget, props, children);
    }

    /// <summary>A prop is a scalar, an expression <c>{ "expr": "..." }</c> or a token reference <c>{ "token": "..." }</c> (doc 41c, sections 2 and 5.1).</summary>
    private static object? ReadProp(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                return element.GetDouble();
            case JsonValueKind.Object when element.TryGetProperty("expr", out JsonElement expr) && element.EnumerateObject().Count() == 1:
                return expr.ValueKind == JsonValueKind.String
                    ? new ViewExpr(expr.GetString()!)
                    : throw new FormatException($"{path}.expr: must be a string.");
            case JsonValueKind.Object when element.TryGetProperty("token", out JsonElement token) && element.EnumerateObject().Count() == 1:
                return token.ValueKind == JsonValueKind.String
                    ? new ViewTokenRef(token.GetString()!)
                    : throw new FormatException($"{path}.token: must be a string.");
            default:
                throw new FormatException($"{path}: a prop must be a scalar, an {{ \"expr\": ... }} or a {{ \"token\": ... }} object.");
        }
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{path}: must be an object.");
        }
    }

    private static string RequireString(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"{path}.{property}: must be a string.");
        }

        return value.GetString()!;
    }

    private static int RequireInt(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || !value.TryGetInt32(out int number))
        {
            throw new FormatException($"{path}.{property}: must be an integer.");
        }

        return number;
    }
}
