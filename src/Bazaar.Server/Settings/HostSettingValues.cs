namespace Bazaar.Server.Settings;

/// <summary>Where the status listener binds (doc 03 <c>host.status.bind</c>). Anything but Loopback needs approval.</summary>
public enum StatusBind
{
    Loopback,
    Lan,
    Tailscale,
}

/// <summary>Readiness role names for <c>host.readiness.required_roles</c>.</summary>
public static class HostReadinessRoles
{
    /// <summary>Always required. Doc 03 floor: Postgres is never removed from readiness.</summary>
    public const string Postgres = "postgres";

    public const string ServiceHost = "service_host";
    public const string Vm = "vm";
    public const string Cache = "cache";
    public const string Search = "search";

    public static IReadOnlyList<string> All { get; } = [Postgres, ServiceHost, Vm, Cache, Search];
}
