using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Agents.Responder;

public static class AgentResponderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent responder: the mention and DM reply loop over the GC notify stream, its Postgres store and its options
    /// (<c>Bazaar:AgentResponder</c>). It needs the host to have registered <c>AddBazaarGcWire</c> (the notify source, the body
    /// codec and the log writer), an <c>NpgsqlDataSource</c>, and an <see cref="Gateway.IModelGateway"/>. The options are validated
    /// at start.
    /// </summary>
    public static IServiceCollection AddBazaarAgentResponder(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentResponderOptions>()
            .Bind(configuration.GetSection(AgentResponderOptions.SectionName))
            .Validate(options => { options.Validate(); return true; })
            .ValidateOnStart();
        services.TryAddSingleton<IResponderGcStore, PgResponderGcStore>();
        services.TryAddSingleton<AgentResponder>();
        services.AddHostedService<AgentResponderService>();
        return services;
    }
}
