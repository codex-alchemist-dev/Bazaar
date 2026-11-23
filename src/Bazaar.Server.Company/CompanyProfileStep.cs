using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Company;

/// <summary>
/// The setup step that records the company profile (doc 45 step 1, section 1.3a): company name, slug, locale and time zone, which is
/// the plan section <c>install.identity</c>. The release channel and update policy are other sections, applied by <c>install.settings</c>.
/// </summary>
/// <remarks>
/// Idempotent (a second run creates nothing and writes no audit row), refuses a company with other values instead of editing it, and
/// rolls back only the row this run created. One audit row, <c>setup.company.profile_created</c>, holds the plan hash, the entry path
/// and the slug; the display name is not written. If the audit write fails the step removes its own row before the failure goes on,
/// because the applier does not roll back the step that failed.
/// </remarks>
public sealed class CompanyProfileStep(ICompanyProfileStore store, IAuditLog audit, TimeProvider? time = null) : ISetupStep
{
    /// <summary>The step id written to the apply audit row.</summary>
    public const string StepId = "company.profile";

    /// <summary>Early: storage is 20, and every step that needs the company or the owner runs later.</summary>
    public const int StepOrder = 40;

    /// <summary>The audit kind of the row written when the step created the profile.</summary>
    public const string CreatedKind = "setup.company.profile_created";

    /// <summary>The audit kind of the row written when the step is rolled back.</summary>
    public const string RolledBackKind = "setup.company.profile_rolled_back";

    /// <summary>The plan sections this step applies.</summary>
    public static readonly IReadOnlyList<string> CoveredSections = ["install.identity"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // The slug this run created, by plan hash, for the rollback of the same apply. An apply holds the instance lock.
    private readonly ConcurrentDictionary<string, string> _created = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => CoveredSections;

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var request = new CompanyProfileRequest(plan.Install.CompanyName, plan.Install.CompanySlug, plan.Install.Locale, plan.Install.Timezone);
        var result = await store.EnsureAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new SetupStepResult(false, result.Refusal);
        }

        if (!result.Created)
        {
            return new SetupStepResult(true, "the company profile was already present.");
        }

        try
        {
            await audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, CreatedKind, Payload(context, request.Slug).ToJsonString()),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.RemoveAsync(request.Slug, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _created[context.PlanHash] = request.Slug;
        return new SetupStepResult(true, "the company profile was created.");
    }

    /// <inheritdoc />
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_created.TryRemove(context.PlanHash, out var slug))
        {
            return;
        }

        await store.RemoveAsync(slug, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            new AuditEntry(_time.GetUtcNow(), context.OperatorIdentity, RolledBackKind, Payload(context, slug).ToJsonString()),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Payload(SetupApplyContext context, string slug) => new()
    {
        ["planHash"] = context.PlanHash,
        ["entryPath"] = context.EntryPath.ToString().ToLowerInvariant(),
        ["companySlug"] = slug,
    };
}
