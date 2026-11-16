using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Fail-closed check of the wake loop's tool allowlist (<c>Bazaar:AgentTurns:Scheduler:ToolAllowlist</c>). A woken turn is
/// offered only the catalogue entries its allowlist names (<see cref="TurnToolOffer"/>), and an allowlist name that no
/// component offers is otherwise dropped without a word. An operator who lists <c>ryusei.list</c> and forgets the project
/// setting would get a loop that quietly never offers it. So the loop refuses to start, and the error names each missing tool.
/// </summary>
public static class WakeToolAllowlist
{
    /// <summary>The tool-name prefix whose tools need <c>Bazaar:Ryusei:ProjectId</c> before any component offers them.</summary>
    private const string _ryuseiPrefix = "ryusei.";

    /// <summary>Throws when an allowlist name has no model tool definition in <paramref name="catalogue"/>.</summary>
    /// <exception cref="InvalidOperationException">One or more allowlist names are offered by no component.</exception>
    public static void EnsureOffered(IReadOnlyList<string> allowlist, IEnumerable<ModelToolDefinition> catalogue)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(catalogue);

        var offered = catalogue.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var missing = allowlist
            .Where(name => !offered.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
            return;

        var hint = missing.Any(name => name.StartsWith(_ryuseiPrefix, StringComparison.Ordinal))
            ? " The ryusei.* tools are offered only when Bazaar:Ryusei:ProjectId is set."
            : string.Empty;
        throw new InvalidOperationException(
            $"{AgentSchedulerOptions.SectionName}:ToolAllowlist names tools that no component offers: {string.Join(", ", missing)}. " +
            "Enable the component that provides each tool, or remove it from the allowlist." + hint);
    }
}
