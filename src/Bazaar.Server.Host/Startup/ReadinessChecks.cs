using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Startup;

public sealed record ReadinessResult(string Name, bool Ok, string? Detail = null);

/// <summary>One condition behind <c>/health/ready</c> (doc 03). Checks must be quick and never include secret values in <see cref="ReadinessResult.Detail"/>.</summary>
public interface IReadinessCheck
{
    string Name { get; }

    Task<ReadinessResult> CheckAsync(CancellationToken ct);
}

public sealed class DatabaseReadinessCheck(DatabaseConnection database) : IReadinessCheck
{
    public string Name => "postgres";

    public async Task<ReadinessResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            var dataSource = await database.GetAsync(ct);
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return new ReadinessResult(Name, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReadinessResult(Name, false, ex.GetType().Name);
        }
    }
}

public sealed class InstanceLockReadinessCheck(IInstanceLock instanceLock) : IReadinessCheck
{
    public string Name => "instance_lock";

    public Task<ReadinessResult> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResult(Name, instanceLock.IsHeld, instanceLock.IsHeld ? null : "not held"));
}

/// <summary>Ready only when every required provider role has a usable provider. The role list is empty until the settings registry (ST1) supplies <c>host.readiness.required_roles</c>.</summary>
public sealed class ProviderReadinessCheck(ProviderCapabilityReport report, IOptions<BazaarReadinessOptions> options) : IReadinessCheck
{
    public string Name => "providers";

    public Task<ReadinessResult> CheckAsync(CancellationToken ct)
    {
        var missing = report.MissingRequiredRoles(options.Value.RequiredRoles);
        return Task.FromResult(missing.Count == 0
            ? new ReadinessResult(Name, true)
            : new ReadinessResult(Name, false, "unusable: " + string.Join(", ", missing.Select(r => r.SettingName()))));
    }
}

public sealed class BazaarReadinessOptions
{
    public const string SectionName = "Bazaar:Readiness";

    public List<ProviderRole> RequiredRoles { get; set; } = [];
}
