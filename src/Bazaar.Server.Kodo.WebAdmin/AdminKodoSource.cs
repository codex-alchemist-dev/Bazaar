using Bazaar.Contracts.Kodo;
using Bazaar.Core;
using Bazaar.Server.Kodo;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Kodo.WebAdmin;

/// <summary>
/// Maps the held Kodo snapshot and the open incidents to the admin panel's read view (doc 51 section 5, doc 46 section 15.7).
/// The view carries ids, state words, cause codes, titles and runbook links only: no journal head, no controls, no evidence.
/// </summary>
public sealed class AdminKodoSource(IKodoSnapshotProvider snapshots, IKodoIncidentSource incidents, TimeProvider? time = null, TimeSpan? maxAge = null) : IAdminKodoSource
{
    /// <summary>A snapshot older than this is shown as stale, with no score and no component states (doc 46 section 8.3, never show stale green).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _maxAge = maxAge ?? DefaultMaxAge;

    public const string StateCheckName = "kodo.state";
    internal const int MaxIncidents = 50;
    internal const int MaxTitleLength = 200;

    public async Task<AdminKodoView> GetAsync(CancellationToken ct = default)
    {
        KodoStatusSnapshotDto? snapshot = snapshots.Current;
        IReadOnlyList<KodoIncidentSummary> open = await incidents.ListOpenAsync(ct).ConfigureAwait(false);

        List<AdminKodoCheck> checks = [];
        bool stale = snapshot is not null && _time.GetUtcNow() - snapshot.Status.AsOf > _maxAge;
        if (stale)
        {
            checks.Add(new(StateCheckName, "stale", $"last update {snapshot!.Status.AsOf.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"));
        }
        else if (snapshot is not null)
        {
            KodoStatusDto status = snapshot.Status;
            checks.Add(new(StateCheckName, status.State, status.UnderlyingState is null ? null : $"underlying: {status.UnderlyingState}"));
            foreach (KodoComponentDto component in snapshot.Components)
            {
                checks.Add(new(component.Id, component.State, component.Cause));
            }
        }

        List<AdminKodoIncident> list = open
            .OrderByDescending(i => i.OpenedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .Take(MaxIncidents)
            .Select(i => new AdminKodoIncident(i.Id, Title(i.Title), i.Severity, i.OpenedAt, SafeLink(i.RunbookLink)))
            .ToList();

        return new(stale ? null : snapshot?.Status.Score, checks, list);
    }

    private static string Title(string title)
    {
        string t = title.Trim();
        return t.Length <= MaxTitleLength ? t : t[..MaxTitleLength];
    }

    /// <summary>Only an http(s) URL or a site-relative path is passed on; anything else (script schemes, file paths) becomes null.</summary>
    internal static string? SafeLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        string l = link.Trim();
        if (l.StartsWith('/') && !l.StartsWith("//", StringComparison.Ordinal) && !l.Contains('\\'))
        {
            return l;
        }

        return Uri.TryCreate(l, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" ? l : null;
    }
}

public static class KodoWebAdminRegistration
{
    /// <summary>Registers <see cref="IAdminKodoSource"/> over the Kodo snapshot provider and incident source.</summary>
    public static IServiceCollection AddBazaarKodoWebAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAdminKodoSource>(sp =>
            new AdminKodoSource(sp.GetRequiredService<IKodoSnapshotProvider>(), sp.GetRequiredService<IKodoIncidentSource>(), sp.GetService<TimeProvider>()));
        return services;
    }
}

/// <summary>Admin panel Kodo port (doc 51 section 5). Kept apart from the Kodo module so that module stays free of WebAdmin.</summary>
public sealed class KodoWebAdminComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "kodo-web-admin",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["kodo"],
        Provides: ["feature:kodo-web-admin"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarKodoWebAdmin();
}
