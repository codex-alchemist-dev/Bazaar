using Bazaar.Admin.Composition;
using Bazaar.Client.Tui.Dashboard;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.OpsPorts;
using Bazaar.Server.WebAdmin;
using Bazaar.Server.WebAdmin.Security;

namespace Bazaar.Client.Tui.Host.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class RecordingAudit : IAdminAudit
{
    public List<(string Kind, string Actor)> Entries { get; } = [];

    public Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        Entries.Add((kind, actor));
        return Task.CompletedTask;
    }
}

internal sealed class FakeGate(AdminIdentity? identity) : IOwnerLocalGate
{
    public Task<AdminIdentity?> EnterAsync(CancellationToken ct = default) => Task.FromResult(identity);
}

internal sealed class AllowAll : IOpsAuthorizer
{
    public Task<bool> MayUseOpsAsync(AdminIdentity identity, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class FakeStatus : IAdminStatusSource
{
    public Task<AdminStatusView> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new AdminStatusView("Test Studio", "0.1.1", null, DateTimeOffset.UnixEpoch, 3600, []));
}

/// <summary>The wiring a test hands the command line: what Open* returns, and whether it was opened and disposed.</summary>
internal sealed class FakeWiring : ITuiHostWiring
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    public OpsWiring? Ops { get; set; }

    public bool OwnerExists { get; set; }

    public int OpsOpened { get; private set; }

    public int InstallOpened { get; private set; }

    public bool InstallDisposed { get; private set; }

    public Task<OpsWiring?> OpenOpsAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct)
    {
        OpsOpened++;
        return Task.FromResult(Ops);
    }

    public Task<InstallWiring> OpenInstallAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct)
    {
        InstallOpened++;
        return Task.FromResult(new InstallWiring(PostgresApplyGate.Closed, new Disposed(() => InstallDisposed = true)));
    }

    private sealed class Disposed(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Ops pieces over fakes: the audit and the crash journal are returned so the test can read them.</summary>
    public static (OpsWiring Wiring, RecordingAudit Audit, FileOpsCrashJournal Crashes, InMemoryAdminSessionStore Sessions) OpsOver(
        AdminIdentity? owner, string crashFile, bool ownerExists, IAdminStatusSource? status = null, IOpsEventSource? events = null)
    {
        var audit = new RecordingAudit();
        var sessions = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var crashes = new FileOpsCrashJournal(crashFile);
        var signIn = new OpsSignIn(new FakeGate(owner), null, sessions, new AllowAll(), audit, new FixedClock(Now));
        var ports = new OpsPorts(Status: status, Events: events, Crashes: crashes);
        return (new OpsWiring(ports, signIn, crashes, _ => Task.FromResult(ownerExists)), audit, crashes, sessions);
    }

    public static readonly AdminIdentity Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), PrincipalKind.Human, Guid.Empty);
}

/// <summary>A key terminal whose key reads can throw, to make the dashboard crash.</summary>
internal sealed class ExplodingTerminal : ITerminal
{
    private readonly Bazaar.Client.Tui.Tests.ScriptedTerminal _inner = new(supportsKeys: true);

    public ExplodingTerminal()
    {
        _inner.Keys(new TerminalKey(TerminalKeyKind.Enter));
    }

    public bool SupportsKeys => true;

    public int Width => 80;

    public void Write(string text) => _inner.Write(text);

    public void WriteLine(string text = "") => _inner.WriteLine(text);

    public string? ReadLine() => _inner.ReadLine();

    public int Reads { get; private set; }

    public TerminalKey ReadKey()
    {
        // The first key answers the sign-in pick; the next is the dashboard's, and it throws.
        if (Reads++ == 0)
        {
            return _inner.ReadKey();
        }

        throw new InvalidOperationException("secret detail that must not be recorded");
    }

    public void EraseLinesAbove(int count) => _inner.EraseLinesAbove(count);
}
