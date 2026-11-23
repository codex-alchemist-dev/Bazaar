namespace Bazaar.Server.Providers;

/// <summary>The four per-OS roles from doc 03. Any number of providers may be registered for a role; the consuming module decides what to do with several (doc 03, Provider modules).</summary>
public enum ProviderRole
{
    /// <summary>Runs the host as a service: Windows Service or systemd.</summary>
    ServiceHost,

    /// <summary>Agent VM backend: Hyper-V or QEMU/KVM (doc 16 owns the contract).</summary>
    Vm,

    /// <summary>Cache and presence only: Garnet or Valkey. Never GC fan-out (C07).</summary>
    Cache,

    /// <summary>Full-text search: Postgres FTS, or pg_search on Linux.</summary>
    Search,
}
