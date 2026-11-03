using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>One tool the server can run for an agent. Arguments are the parsed JSON object the model sent.</summary>
public interface ITool
{
    /// <summary>Runs the tool and returns its result as JSON text. The result must not carry secrets or host paths.</summary>
    Task<string> ExecuteAsync(JsonElement args, CancellationToken cancellationToken);
}

/// <summary>Finds a tool by its exact name. A name that is not registered returns false.</summary>
public interface IToolRegistry
{
    bool TryGet(string name, [NotNullWhen(true)] out ITool? tool);
}

/// <summary>The fixed set of runnable tools, built once at startup. Names match exactly and ordinally.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly ImmutableDictionary<string, ITool> _tools;

    /// <summary>Builds a registry. Throws if a name is blank, has surrounding whitespace or repeats.</summary>
    /// <exception cref="ArgumentException">A name breaks a registration rule.</exception>
    public ToolRegistry(IEnumerable<(string Name, ITool Tool)> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var builder = ImmutableDictionary.CreateBuilder<string, ITool>(StringComparer.Ordinal);
        foreach (var (name, tool) in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
            {
                throw new ArgumentException("Tool names must be non-blank and must not have surrounding whitespace.", nameof(tools));
            }

            if (!builder.TryAdd(name, tool))
            {
                throw new ArgumentException($"Tool '{name}' is registered twice.", nameof(tools));
            }
        }

        _tools = builder.ToImmutable();
    }

    /// <inheritdoc />
    public bool TryGet(string name, [NotNullWhen(true)] out ITool? tool)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _tools.TryGetValue(name, out tool);
    }
}
