using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.MemberRoster;

public static class MemberRosterRegistration
{
    /// <summary>Registers <see cref="IAdminMemberRoster"/> over the shared <see cref="NpgsqlDataSource"/>.</summary>
    public static IServiceCollection AddBazaarMemberRoster(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMemberRowSource>(sp => new PostgresMemberRowSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAdminMemberRoster>(sp => new AdminMemberRoster(sp.GetRequiredService<IMemberRowSource>()));
        return services;
    }
}
