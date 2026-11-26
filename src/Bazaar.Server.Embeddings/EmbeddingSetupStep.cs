using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings;

/// <summary>What the embedding setup step will do for a plan (doc 08, "WP6 detail"; doc 45 step 9b).</summary>
public enum EmbeddingSetupAction
{
    /// <summary>The plan chooses <c>none</c>: lexical only, nothing to write.</summary>
    None,

    /// <summary>The chosen provider's profile is not active yet: create it if needed, then activate it.</summary>
    Activate,

    /// <summary>The chosen provider's profile is already the active one: nothing changes.</summary>
    AlreadyActive,

    /// <summary>The plan is not valid; <see cref="EmbeddingSetupPlan.Issues"/> says why. Nothing is written.</summary>
    Refused,
}

/// <summary>
/// The outcome of planning the embedding choice. It carries the provider id, kind, model id, dimension and the base URL without
/// credentials. It never carries a secret reference or value, so it is safe to print in a dry run.
/// </summary>
public sealed record EmbeddingSetupPlan(
    EmbeddingSetupAction Action,
    string? ProviderId,
    EmbeddingProviderKind? Kind,
    string? ModelId,
    int? Dimensions,
    string? BaseUrl,
    IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>True when the plan may be applied.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Setup step for the embedding profile (doc 08 WP6; doc 45 step 9b). The choice is the plan setting <c>memory.embedding_profile</c>
/// (<c>none</c> or a provider id); the provider itself (kind, base URL, model, dimensions, key reference) is read from the
/// <c>Bazaar:Embeddings</c> configuration, so the plan holds no new section and no secret. The dimension is fixed at setup: a
/// provider whose dimension differs from the active profile is refused. The step only writes <c>embedding_profile</c> rows; the
/// setting value itself is written by the settings step.
/// </summary>
public sealed class EmbeddingSetupStep(IEmbeddingProfileStore profiles, IOptions<EmbeddingOptions> options) : ISetupStep
{
    /// <summary>The audit step id.</summary>
    public const string StepId = "embeddings.profile";

    /// <summary>Just after Model sources (doc 45 step 9), so it runs after the schema and custody steps.</summary>
    public const int StepOrder = 95;

    private const int _maxDimensions = 4000; // pgvector's halfvec index limit.
    private const string _settingPath = "settings.memory.embedding_profile";

    // Previous active profile per plan hash, so a rollback can put it back. In memory: a crash loses it, and the apply then resumes forward.
    private readonly ConcurrentDictionary<string, Guid?> _previousActive = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <summary>
    /// Validates and plans the choice against the current profile rows. Reads only. Messages name keys and rules, never values,
    /// so a literal secret in the configuration cannot leak through an issue.
    /// </summary>
    public async Task<EmbeddingSetupPlan> PlanAsync(SetupPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var chosen = ReadChoice(plan, out var choiceIssue);
        if (choiceIssue is not null)
        {
            return Refused(null, choiceIssue);
        }

        if (EmbeddingSettings.IsNone(chosen))
        {
            return new EmbeddingSetupPlan(EmbeddingSetupAction.None, null, null, null, null, null, []);
        }

        var entry = options.Value.Providers.FirstOrDefault(p => string.Equals(p.Id, chosen, StringComparison.Ordinal));
        if (entry is null)
        {
            return Refused(chosen, Error(_settingPath, "names a provider that is not configured under Bazaar:Embeddings:Providers."));
        }

        var issues = ValidateProvider(entry);
        var modelId = ResolveModelId(entry);
        if (issues.Count > 0)
        {
            return new EmbeddingSetupPlan(EmbeddingSetupAction.Refused, entry.Id, entry.Kind, modelId, null, null, issues);
        }

        var active = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null && active.Dimensions != entry.Dimensions)
        {
            issues.Add(Error(_settingPath, "dimension is fixed at setup; the active profile has a different dimension. Changing it is a planned re-embed with a new install profile, not a re-plan."));
            return new EmbeddingSetupPlan(EmbeddingSetupAction.Refused, entry.Id, entry.Kind, modelId, entry.Dimensions, DisplayBaseUrl(entry), issues);
        }

        var already = active is not null
            && string.Equals(active.ProviderId, entry.Id, StringComparison.Ordinal)
            && string.Equals(active.ModelId, modelId, StringComparison.Ordinal);
        return new EmbeddingSetupPlan(
            already ? EmbeddingSetupAction.AlreadyActive : EmbeddingSetupAction.Activate,
            entry.Id, entry.Kind, modelId, entry.Dimensions, DisplayBaseUrl(entry), []);
    }

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var step = await PlanAsync(plan, cancellationToken).ConfigureAwait(false);
        switch (step.Action)
        {
            case EmbeddingSetupAction.Refused:
                // Key and rule only; the issue text never holds a value.
                return new SetupStepResult(false, string.Join(' ', step.Issues.Select(i => $"{i.Path} {i.Message}")));
            case EmbeddingSetupAction.None:
            case EmbeddingSetupAction.AlreadyActive:
                return SetupStepResult.Ok;
        }

        var existing = await profiles.FindAsync(step.ProviderId!, step.ModelId!, step.Dimensions!.Value, cancellationToken).ConfigureAwait(false);
        var profile = existing ?? await profiles.CreateAsync(step.ProviderId!, step.ModelId!, step.Dimensions!.Value, cancellationToken).ConfigureAwait(false);
        if (!profile.Active)
        {
            var previous = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            _previousActive[context.PlanHash] = previous?.ProfileId;
            await profiles.ActivateAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        }

        return SetupStepResult.Ok;
    }

    /// <summary>Re-activates the profile that was active before this apply, when there was one. With none before, the new row stays; the setting step decides whether it is used.</summary>
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_previousActive.TryRemove(context.PlanHash, out var previous) && previous is { } id)
        {
            await profiles.ActivateAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ReadChoice(SetupPlan plan, out SetupValidationIssue? issue)
    {
        issue = null;
        if (!plan.Settings.TryGetValue(EmbeddingSettings.EmbeddingProfileKey.Name, out var element))
        {
            return EmbeddingSettings.None;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issue = Error(_settingPath, "must be a string: none, or a provider id.");
            return null;
        }

        var value = element.GetString();
        var invalid = EmbeddingSettings.EmbeddingProfileKey.CheckValue(element);
        if (invalid is not null)
        {
            issue = Error(_settingPath, "must be none or a provider id of lowercase letters, digits, dots, underscores or hyphens.");
            return null;
        }

        return value;
    }

    private static List<SetupValidationIssue> ValidateProvider(EmbeddingProviderOptions entry)
    {
        var issues = new List<SetupValidationIssue>();
        var at = $"embeddings.providers[{entry.Id}]";

        if (entry.Dimensions <= 0 || entry.Dimensions > _maxDimensions)
        {
            issues.Add(Error($"{at}.dimensions", $"must be 1 to {_maxDimensions}."));
        }

        if (entry.Kind != EmbeddingProviderKind.Fake && string.IsNullOrWhiteSpace(entry.ModelId))
        {
            issues.Add(Error($"{at}.modelId", "is required."));
        }

        if (entry.Kind == EmbeddingProviderKind.OpenAiCompatible)
        {
            if (!Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                issues.Add(Error($"{at}.baseUrl", "must be an absolute http or https URL."));
            }
            else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                issues.Add(Error($"{at}.baseUrl", "must not carry credentials, a query or a fragment; give a key as apiKeyReference."));
            }
        }

        if (entry.ApiKeyReference is { } reference && !IsSecretReference(reference))
        {
            issues.Add(Error($"{at}.apiKeyReference", "must be a secret reference (env:, file:, dpapi:, systemd-creds: or secret://bazaar/), never a key."));
        }

        return issues;
    }

    private static bool IsSecretReference(string text)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["env:", "file:", "dpapi:", "systemd-creds:", "secret://bazaar/"])
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text.Length > prefix.Length && !text.Any(char.IsWhiteSpace);
            }
        }

        return false;
    }

    private static string ResolveModelId(EmbeddingProviderOptions entry) =>
        entry.Kind == EmbeddingProviderKind.Fake && string.IsNullOrWhiteSpace(entry.ModelId) ? FakeEmbeddingProvider.DefaultModelId : entry.ModelId;

    /// <summary>Scheme, host and port only. The URL was checked to carry no credentials, but only what is needed is shown.</summary>
    private static string? DisplayBaseUrl(EmbeddingProviderOptions entry) =>
        entry.Kind == EmbeddingProviderKind.OpenAiCompatible && Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    private static EmbeddingSetupPlan Refused(string? providerId, SetupValidationIssue issue) =>
        new(EmbeddingSetupAction.Refused, providerId, null, null, null, null, [issue]);

    private static SetupValidationIssue Error(string path, string message) => new(SetupIssueSeverity.Error, path, message);
}
