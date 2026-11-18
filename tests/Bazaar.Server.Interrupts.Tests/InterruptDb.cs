using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Interrupts;

/// <summary>
/// A per-class database with the interrupt frame schema applied: common 0015 (the table, plaintext reason) then interrupts 0001
/// (sealed reason). The scripts are linked from Bazaar.Server's Data/Sql folder, so the migrations stay where they are and this
/// project needs no reference to Bazaar.Server. Both run in order, as on a real install; 0015 has no foreign keys to other tables.
/// </summary>
public sealed class InterruptDb : PgFixture, IAsyncLifetime
{
    private static readonly string[] _scripts = ["interrupt_0015.sql", "interrupt_0001.sql"];

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        foreach (var name in _scripts)
        {
            await using var cmd = DataSource.CreateCommand(ReadScript(name));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string ReadScript(string logicalName)
    {
        using var stream = typeof(InterruptDb).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded script '{logicalName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
