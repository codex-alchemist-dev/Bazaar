using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Builds the list of tool definitions a turn may offer a model (doc 06, Tool dispatch). A component adds one
/// <see cref="ModelToolDefinition"/> next to each <see cref="ToolRegistration"/>; the host collects them here. A turn is
/// offered only the entries its allowlist names (<see cref="TurnToolOffer"/>), so a definition in the catalogue offers
/// nothing by itself.
/// </summary>
public static class ToolCatalogue
{
    /// <summary>
    /// Checks every definition and returns them ordered by name, so the offered list (and the request hash built from it)
    /// does not depend on registration order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A definition has a blank name or description, repeats a name, names a tool that is not registered, or carries a
    /// parameter schema that is not a JSON object of type <c>object</c>. Each would offer a model a tool it cannot call.
    /// </exception>
    public static IReadOnlyList<ModelToolDefinition> Build(IEnumerable<ModelToolDefinition> definitions, IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(registry);

        var byName = new SortedDictionary<string, ModelToolDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new InvalidOperationException("A tool definition needs a name.");
            }

            if (string.IsNullOrWhiteSpace(definition.Description))
            {
                throw new InvalidOperationException($"The tool definition '{definition.Name}' needs a description.");
            }

            if (!registry.TryGet(definition.Name, out _))
            {
                throw new InvalidOperationException($"The tool definition '{definition.Name}' has no registered tool.");
            }

            if (!IsObjectSchema(definition.ParametersJsonSchema))
            {
                throw new InvalidOperationException(
                    $"The tool definition '{definition.Name}' needs a JSON schema of type object for its parameters.");
            }

            if (!byName.TryAdd(definition.Name, definition))
            {
                throw new InvalidOperationException($"The tool definition '{definition.Name}' is registered more than once.");
            }
        }

        return [.. byName.Values];
    }

    private static bool IsObjectSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("object");
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
