namespace Bazaar.Components.ManagedPostgres;

/// <summary>A managed PostgreSQL operation failed or was refused. Messages never contain the superuser password.</summary>
public sealed class PostgresProcessException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public PostgresProcessException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the underlying cause.</summary>
    public PostgresProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
