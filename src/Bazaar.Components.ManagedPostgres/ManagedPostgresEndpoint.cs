namespace Bazaar.Components.ManagedPostgres;

/// <summary>The connection details of a running managed cluster. Set by <see cref="ManagedPostgresHostedService"/> after the cluster is ready.</summary>
public sealed record ManagedPostgresConnectionInfo(string Host, int Port, string Username, string Password, string Database);

/// <summary>Holds the managed cluster's connection details once it is running. Null in External mode or before start.</summary>
public sealed class ManagedPostgresEndpoint
{
    public ManagedPostgresConnectionInfo? Current { get; private set; }

    internal void Set(ManagedPostgresConnectionInfo connection) => Current = connection;
}
