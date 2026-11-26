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

/// <summary>
/// The caller of one tool call, as the dispatcher knows it. Carried to a tool that needs to know who is calling (for
/// example a tool that acts for the calling agent), so the calling identity is never taken from the model's arguments.
/// <see cref="ChainDepth"/> is how many trigger fires deep the calling turn is: 0 for a turn no fire woke, and the woken
/// turn's fire depth otherwise. A tool that creates work from this turn adds one to it, so a loop cannot reset its depth.
/// </summary>
public sealed record ToolCallContext(Guid AgentId, Guid SessionId, Guid TurnId, int ChainDepth = 0);

/// <summary>
/// A tool that also needs the caller (<see cref="ToolCallContext"/>). Added as a companion interface, not a change to
/// <see cref="ITool"/>, so existing tools keep their shape. <see cref="ToolDispatcher"/> calls the context overload when a
/// registered tool implements this interface, and the plain <see cref="ITool.ExecuteAsync"/> is never called for it.
/// Because it derives from <see cref="ITool"/> (the registry holds <see cref="ITool"/>), the plain overload is a default
/// implementation that throws, so a caller that bypasses the dispatcher fails loudly instead of running without a caller.
/// </summary>
public interface IContextualTool : ITool
{
    /// <summary>Runs the tool for the given caller. The same result rules as <see cref="ITool.ExecuteAsync"/> apply.</summary>
    Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken);

    /// <summary>Refuses a call that bypasses the dispatcher, because such a call has no caller to act for.</summary>
    Task<string> ITool.ExecuteAsync(JsonElement args, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A contextual tool must be run through the dispatcher, which supplies the caller.");
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
