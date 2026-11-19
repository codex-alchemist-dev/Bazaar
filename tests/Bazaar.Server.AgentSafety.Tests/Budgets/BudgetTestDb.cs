using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// A fresh database per test, migrated with the server's own embedded migrations, so the agent_safety migrations run exactly
/// as in production. Spends from other tests would otherwise leak into a test that counts them.
/// </summary>
internal static class BudgetTestDb
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) =>
        PgTestDatabase.CreateAsync("budget", ServerMigrations.MigrateAsync, ct);
}
