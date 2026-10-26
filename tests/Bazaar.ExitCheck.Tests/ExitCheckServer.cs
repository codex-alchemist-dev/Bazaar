using Bazaar.Client.Core.Wire;
using Bazaar.Testing.Postgres;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// The running server as the exit check sees it: a real host on real PostgreSQL with one GC that a human and an
/// agent belong to. PRH replaces the body of <see cref="ExitCheckServers.StartAsync"/> with the in-process Kestrel harness.
/// </summary>
public interface IExitCheckServer : IAsyncDisposable
{
    /// <summary>The GC both principals belong to.</summary>
    Guid GcId { get; }

    /// <summary>The human member.</summary>
    Guid HumanId { get; }

    /// <summary>The agent member.</summary>
    Guid AgentId { get; }

    /// <summary>A credential the fake authenticator accepts for a principal.</summary>
    string CredentialFor(Guid principal);

    /// <summary>Opens WebSocket connections to the host's <c>/ws</c>.</summary>
    IWireTransportFactory Transport { get; }
}

/// <summary>Starts the server under test.</summary>
public static class ExitCheckServers
{
    /// <summary>Starts the host against <paramref name="db"/>, or throws <see cref="PendingPrerequisitesException"/>.</summary>
    public static ValueTask<IExitCheckServer> StartAsync(PgFixture db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (ExitCheckLedger.Pending is { Count: > 0 } pending)
        {
            throw new PendingPrerequisitesException(pending);
        }

        // Written by whoever merges the last prerequisite: build the host with PRH, migrate db, seed the GC and members.
        throw new PendingPrerequisitesException([new Prerequisite("E2E", "wire ExitCheckServers.StartAsync to the PRH harness once GLUE lands", false)]);
    }
}
