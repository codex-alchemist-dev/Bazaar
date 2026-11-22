using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Hive.Settings;

/// <summary>
/// The grant matrix read from and written to the <c>hive.grants</c> setting (docs/design/08-memory-system.md, settings table), so a
/// grant is an audited install setting. Reads see the stored value or the empty default, which transfers nothing. A stored value
/// that no longer validates throws, so a copy fails closed instead of reading a damaged matrix. Writes go through the settings
/// store as an operator actor named by <see cref="HiveGrant.GrantedBy"/> and throw when the store does not accept them.
/// </summary>
public sealed class SettingsHiveGrantStore(ISettingStore settings) : IHiveGrantStore, IDisposable
{
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _writes.Dispose();

    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        var name = HiveKinds.Name(kind);
        var entries = await settings.GetAsync(HiveSettings.Grants, cancellationToken: ct).ConfigureAwait(false);
        return entries.Any(e => Matches(e, fromProject, toProject, name));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct)
    {
        var entries = await settings.GetAsync(HiveSettings.Grants, cancellationToken: ct).ConfigureAwait(false);
        return [.. entries.Select(e => HiveSettings.ToGrant(e, "settings"))];
    }

    /// <inheritdoc />
    public async Task AddAsync(HiveGrant grant, CancellationToken ct)
    {
        HiveGrantRules.Validate(grant);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await settings.GetAsync(HiveSettings.Grants, cancellationToken: ct).ConfigureAwait(false);
            var name = HiveKinds.Name(grant.Kind);
            if (entries.Any(e => Matches(e, grant.FromProject, grant.ToProject, name)))
            {
                return;
            }

            await WriteAsync([.. entries, new HiveGrantEntry(grant.FromProject, grant.ToProject, name)], grant.GrantedBy, ct).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await settings.GetAsync(HiveSettings.Grants, cancellationToken: ct).ConfigureAwait(false);
            var name = HiveKinds.Name(kind);
            var kept = entries.Where(e => !Matches(e, fromProject, toProject, name)).ToList();
            if (kept.Count == entries.Count)
            {
                return false;
            }

            await WriteAsync(kept, "hive", ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task WriteAsync(IReadOnlyList<HiveGrantEntry> next, string actorId, CancellationToken ct)
    {
        var result = await settings.SetAsync(
            HiveSettings.Grants, next, new SettingActor(actorId, SettingRole.Operator), cancellationToken: ct).ConfigureAwait(false);
        if (result.Outcome != SettingWriteOutcome.Accepted)
        {
            throw new InvalidOperationException($"{HiveSettings.GrantsName} was not changed: {result.Reason}");
        }
    }

    private static bool Matches(HiveGrantEntry e, string from, string to, string kind) =>
        string.Equals(e.Kind, kind, StringComparison.Ordinal)
        && string.Equals(e.FromProject, from, StringComparison.Ordinal)
        && string.Equals(e.ToProject, to, StringComparison.Ordinal);
}

/// <summary>Where the allowed Hive kinds come from. Read on every copy so an operator change applies at once.</summary>
public interface IHiveKindSource
{
    /// <summary>The kinds a copy may carry now.</summary>
    Task<IReadOnlySet<HiveKind>> AllowedKindsAsync(CancellationToken ct);
}

/// <summary>Fixed kinds, from <see cref="HiveCopyOptions"/>. Used when no settings store exists (tests, dev runs).</summary>
public sealed class OptionsHiveKindSource(HiveCopyOptions options) : IHiveKindSource
{
    /// <inheritdoc />
    public Task<IReadOnlySet<HiveKind>> AllowedKindsAsync(CancellationToken ct) => Task.FromResult(options.AllowedKinds);
}

/// <summary>The allowed kinds from the <c>hive.allowed_kinds</c> setting. A damaged stored value throws, so a copy fails closed.</summary>
public sealed class SettingsHiveKindSource(ISettingStore settings) : IHiveKindSource
{
    /// <inheritdoc />
    public async Task<IReadOnlySet<HiveKind>> AllowedKindsAsync(CancellationToken ct)
    {
        var names = await settings.GetAsync(HiveSettings.AllowedKinds, cancellationToken: ct).ConfigureAwait(false);
        var kinds = new HashSet<HiveKind>();
        foreach (var name in names)
        {
            if (!HiveKinds.TryParse(name, out var kind))
            {
                throw new InvalidOperationException($"{HiveSettings.AllowedKindsName} holds an unknown kind '{name}'.");
            }

            kinds.Add(kind);
        }

        return kinds;
    }
}
