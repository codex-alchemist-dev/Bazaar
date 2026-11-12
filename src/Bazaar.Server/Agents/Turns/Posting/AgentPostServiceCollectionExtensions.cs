using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Mentions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// Registration for the agent post sink. The host must also register <c>IGcLogWriter</c> and <c>IGcBodyCodec</c>
/// (<c>AddBazaarGcWire</c>), an <c>ILoopGuard</c> and the shared <c>NpgsqlDataSource</c>.
/// </summary>
public static class AgentPostServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GcAgentPostSink"/> as the <see cref="IAgentPostSink"/>, with <see cref="SqlGcMentionDirectory"/> as
    /// its <see cref="IGcMentionDirectory"/> and <see cref="InboxRowWriter"/> as its <see cref="IInboxRowWriter"/>, so agent
    /// posts carry validated mentions and write mention inbox rows. Also registers <see cref="PrincipalHandleRepository"/>.
    /// Every registration is skipped when the host already set one.
    /// </summary>
    public static IServiceCollection AddGcAgentPostSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PrincipalHandleRepository>();
        services.TryAddSingleton<IGcMentionDirectory, SqlGcMentionDirectory>();
        services.TryAddSingleton<IInboxRowWriter, InboxRowWriter>();
        services.TryAddSingleton<IAgentPostSink, GcAgentPostSink>();
        return services;
    }
}
