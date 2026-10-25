using System.Globalization;

namespace Bazaar.Server.Settings;

/// <summary>
/// The <c>host.*</c> settings from doc 03 ("### Settings"), all install scope and operator-changed. Defaults follow the table,
/// with two exceptions noted on the keys. Floors are in the validators, so they hold for every write path:
/// the service account is never Administrator; the status listener stays on loopback unless the change is approved (held
/// as Tier 2); Postgres stays in the readiness roles.
/// </summary>
public static class HostSettings
{
    public static readonly SettingKey<ServiceHostProvider> ProviderServiceHost = new(
        "host.provider.service_host", ServiceHostProvider.Auto, SettingScope.Install, SettingRole.Operator,
        "Service host: Windows Service or systemd. Auto picks the one for this OS.");

    public static readonly SettingKey<VmProvider> ProviderVm = new(
        "host.provider.vm", VmProvider.Auto, SettingScope.Install, SettingRole.Operator,
        "VM backend: Hyper-V, QEMU/KVM, or none.");

    public static readonly SettingKey<CacheProvider> ProviderCache = new(
        "host.provider.cache", CacheProvider.Auto, SettingScope.Install, SettingRole.Operator,
        "Cache and presence: Garnet or Valkey. Auto uses Valkey until Garnet passes spike S2 (doc 03).");

    public static readonly SettingKey<SearchProvider> ProviderSearch = new(
        "host.provider.search", SearchProvider.Auto, SettingScope.Install, SettingRole.Operator,
        "Search: Postgres full-text or pg_search.");

    public static readonly SettingKey<IReadOnlyList<string>> ReadinessRequiredRoles = new(
        "host.readiness.required_roles", ["postgres", "search", "service_host"], SettingScope.Install, SettingRole.Operator,
        "Readiness roles that must be usable before /health/ready passes. Postgres is always required (floor). " +
        "The default adds postgres to the table's list, because doc 03's floor makes it mandatory.",
        validate: CheckRequiredRoles);

    public static readonly SettingKey<int> ShutdownTimeoutSeconds = new(
        "host.shutdown_timeout_seconds", 20, SettingScope.Install, SettingRole.Operator,
        "Seconds a stop waits for the checkpoint before the host exits.",
        validate: Between(5, 120));

    public static readonly SettingKey<IReadOnlyList<int>> ServiceRestartDelaysMinutes = new(
        "host.service.restart_delays_minutes", [1, 1, 5], SettingScope.Install, SettingRole.Operator,
        "Service restart delays in minutes, one per failure.",
        validate: CheckRestartDelays);

    public static readonly SettingKey<int> ServiceFailureResetHours = new(
        "host.service.failure_reset_hours", 24, SettingScope.Install, SettingRole.Operator,
        "Hours without a failure before the restart count resets.",
        validate: AtLeast(1));

    public static readonly SettingKey<string> MaintenanceWindow = new(
        "host.maintenance_window", "04:00", SettingScope.Install, SettingRole.Operator,
        "Local time HH:mm for Windows Update reboots. The host drains first.",
        validate: CheckLocalTime);

    public static readonly SettingKey<int> DbStartupGiveUpMinutes = new(
        "host.db.startup_give_up_minutes", 0, SettingScope.Install, SettingRole.Operator,
        "Minutes the host keeps retrying Postgres at start. 0 means never exit.",
        validate: AtLeast(0));

    public static readonly SettingKey<StatusBind> StatusBindAddress = new(
        "host.status.bind", StatusBind.Loopback, SettingScope.Install, SettingRole.Operator,
        "Where the status listener binds. Anything but loopback needs owner approval (Tier 2).",
        approvalTier: ApprovalTier.Tier2,
        requiresApprovalFor: bind => bind != StatusBind.Loopback);

    /// <summary>
    /// Default 0 is a placeholder: the setup wizard chooses the port. 0 is valid only as that unset placeholder; the status
    /// listener must not start while it is 0.
    /// </summary>
    public static readonly SettingKey<int> StatusPort = new(
        "host.status.port", 0, SettingScope.Install, SettingRole.Operator,
        "Status listener port. 0 until the setup wizard chooses one.",
        validate: value => value == 0 ? null : Between(1, 65535)(value));

    public static readonly SettingKey<string> ServiceAccount = new(
        "host.service_account", "bazaar-service", SettingScope.Install, SettingRole.Operator,
        "Service account the host runs as. Never Administrator (floor). Any change needs owner approval (Tier 2).",
        approvalTier: ApprovalTier.Tier2,
        validate: CheckServiceAccount);

    public static readonly SettingKey<int> LogRetentionDays = new(
        "host.log.retention_days", 14, SettingScope.Install, SettingRole.Operator,
        "Days of log files kept.",
        validate: AtLeast(1));

    public static readonly SettingKey<int> LogMaxTotalMb = new(
        "host.log.max_total_mb", 1024, SettingScope.Install, SettingRole.Operator,
        "Cap on total log size in megabytes.",
        validate: AtLeast(1));

    public static readonly SettingKey<int> HealthDiskUsedWarnPercent = new(
        "host.health.disk_used_warn_percent", 80, SettingScope.Install, SettingRole.Operator,
        "Disk-used percentage that degrades health. It never stops the host.",
        validate: Between(1, 100));

    public static readonly SettingKey<int> HealthMemoryGrowthWarnPercent = new(
        "host.health.memory_growth_warn_percent", 10, SettingScope.Install, SettingRole.Operator,
        "Memory growth percentage that degrades health. It never stops the host.",
        validate: Between(1, 1000));

    /// <summary>Every host key, in doc 03 table order.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        ProviderServiceHost,
        ProviderVm,
        ProviderCache,
        ProviderSearch,
        ReadinessRequiredRoles,
        ShutdownTimeoutSeconds,
        ServiceRestartDelaysMinutes,
        ServiceFailureResetHours,
        MaintenanceWindow,
        DbStartupGiveUpMinutes,
        StatusBindAddress,
        StatusPort,
        ServiceAccount,
        LogRetentionDays,
        LogMaxTotalMb,
        HealthDiskUsedWarnPercent,
        HealthMemoryGrowthWarnPercent,
    ];

    /// <summary>True for "Administrator", "DOMAIN\Administrator" and "Administrator@host", in any case.</summary>
    public static bool IsAdministrator(string account)
    {
        var name = account.Trim();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
            name = name[(slash + 1)..];
        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
            name = name[..at];
        return string.Equals(name, "Administrator", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CheckServiceAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return "the service account name is empty.";
        return IsAdministrator(account) ? "the service account must never be Administrator (doc 03 floor)." : null;
    }

    private static string? CheckRequiredRoles(IReadOnlyList<string> roles)
    {
        foreach (var role in roles)
        {
            if (!HostReadinessRoles.All.Contains(role, StringComparer.Ordinal))
                return $"unknown readiness role '{role}'.";
        }

        if (roles.Distinct(StringComparer.Ordinal).Count() != roles.Count)
            return "a readiness role is listed twice.";
        if (!roles.Contains(HostReadinessRoles.Postgres, StringComparer.Ordinal))
            return "postgres must stay a readiness role (doc 03 floor).";
        return null;
    }

    private static string? CheckRestartDelays(IReadOnlyList<int> delays)
    {
        if (delays.Count is < 1 or > 10)
            return "give between 1 and 10 restart delays.";
        return delays.Any(d => d is < 0 or > 1440) ? "each restart delay must be from 0 to 1440 minutes." : null;
    }

    private static string? CheckLocalTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : "must be a local time as HH:mm, for example 04:00.";

    private static Func<int, string?> Between(int min, int max) =>
        value => value >= min && value <= max ? null : $"must be from {min} to {max}; got {value}.";

    private static Func<int, string?> AtLeast(int min) =>
        value => value >= min ? null : $"must be at least {min}; got {value}.";
}
