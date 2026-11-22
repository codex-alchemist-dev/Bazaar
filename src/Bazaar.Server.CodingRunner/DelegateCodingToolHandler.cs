using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner;

/// <summary>
/// A handler built from a function. Host adapters use it to bind git, search, review and subagent services to tool names
/// without a class per tool.
/// </summary>
public sealed class DelegateCodingToolHandler : ICodingToolHandler
{
    private readonly Func<CodingToolCall, CancellationToken, Task<CodingToolOutput>> _run;

    /// <summary>Creates the handler.</summary>
    public DelegateCodingToolHandler(
        string name,
        ToolCapabilityClass capabilityClass,
        bool writesWorktree,
        Func<CodingToolCall, CancellationToken, Task<CodingToolOutput>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        Name = name;
        CapabilityClass = capabilityClass;
        WritesWorktree = writesWorktree;
        _run = run;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass { get; }

    /// <inheritdoc />
    public bool WritesWorktree { get; }

    /// <inheritdoc />
    public Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken) => _run(toolCall, cancellationToken);
}
