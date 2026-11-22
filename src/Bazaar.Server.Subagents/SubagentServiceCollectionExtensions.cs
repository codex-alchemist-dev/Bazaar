using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Subagents;

/// <summary>DI registration for the subagent policy layer.</summary>
public static class SubagentServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISubagentSpawner"/>. The host must register <see cref="IRuntimeSubagentPort"/> (an adapter over the runtime's
    /// subagent runner), <see cref="ISubagentWorktreeAllocator"/> and <see cref="ISubagentTrace"/>; none has a default, so a missing
    /// one fails at resolve time rather than running unguarded. Options are validated here.
    /// </summary>
    public static IServiceCollection AddBazaarSubagents(this IServiceCollection services, Action<SubagentOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new SubagentOptionsBuilder();
        configure?.Invoke(builder);
        var options = builder.Build();
        options.EnsureValid();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISubagentSpawner, SubagentSpawner>();
        return services;
    }
}

/// <summary>Mutable builder for <see cref="SubagentOptions"/> (records use init-only setters).</summary>
public sealed class SubagentOptionsBuilder
{
    /// <summary>See <see cref="SubagentOptions.RuntimeMaxDepth"/>.</summary>
    public int RuntimeMaxDepth { get; set; } = 2;

    /// <summary>See <see cref="SubagentOptions.MaxDepth"/>.</summary>
    public int MaxDepth { get; set; } = 1;

    /// <summary>See <see cref="SubagentOptions.DisjointWriters"/>.</summary>
    public bool DisjointWriters { get; set; }

    internal SubagentOptions Build() => new()
    {
        RuntimeMaxDepth = RuntimeMaxDepth,
        MaxDepth = MaxDepth,
        DisjointWriters = DisjointWriters,
    };
}
