namespace Bazaar.Server.TalentPool;

/// <summary>
/// <see cref="ITalentPool"/> over a store, the model whitelist port and the hire gate. Fail closed: a template is instantiable only when
/// its stored status is hired AND the gate still says the hire motion for exactly that content passed.
/// </summary>
public sealed class TalentPoolService(ITalentTemplateStore store, ITalentModelWhitelist whitelist, ITalentHireGate hireGate) : ITalentPool
{
    public async Task<TemplateSubmitResult> SubmitAsync(TalentManifest manifest, TalentActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.PrincipalId);

        var allowed = await whitelist.AllowedModelsAsync(actor, ct).ConfigureAwait(false);
        var validation = TalentManifestValidator.Validate(manifest, allowed);
        if (validation.Validated is not { } validated)
            return new TemplateSubmitResult(TemplateSubmitOutcome.Rejected, null, null, null, validation.Errors);

        var templateId = manifest.TemplateId ?? Guid.CreateVersion7();
        if (manifest.TemplateId is not null)
        {
            var versions = await store.ListVersionsAsync(templateId, ct).ConfigureAwait(false);
            if (versions.Count == 0)
                return TemplateSubmitResult.Refused(new TalentManifestError("unknown_template", "The template id does not exist; leave it empty to start a new template."));
            // Only the originating Soul may extend a template's lineage; a stranger's version would ride on the original's name.
            if (versions[0].SourceSoulId != actor.SoulId)
                return TemplateSubmitResult.Refused(new TalentManifestError("not_template_source", "Only the Soul that offered a template may add a version to it."));
        }

        var added = await store.AddVersionAsync(templateId, validated, actor.SoulId, actor.PrincipalId, ct).ConfigureAwait(false);
        return new TemplateSubmitResult(
            added.Created ? TemplateSubmitOutcome.Quarantined : TemplateSubmitOutcome.Duplicate,
            added.Template.TemplateId, added.Template.Version, added.Template.ContentSha, []);
    }

    public async Task<TalentTemplateStatus?> ConfirmHireAsync(Guid templateId, int version, CancellationToken ct)
    {
        var template = await store.GetAsync(templateId, version, ct).ConfigureAwait(false);
        if (template is null)
            return null;
        if (template.Status != TalentTemplateStatus.Quarantined)
            return template.Status;

        if (!await hireGate.HasHirePassedAsync(templateId, version, template.ContentSha, ct).ConfigureAwait(false))
            return TalentTemplateStatus.Quarantined;

        await store.TryResolveAsync(templateId, version, TalentTemplateStatus.Hired, ct).ConfigureAwait(false);
        return (await store.GetAsync(templateId, version, ct).ConfigureAwait(false))?.Status;
    }

    public async Task<TalentInstantiationDecision> EvaluateInstantiationAsync(TalentInstantiateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tier2 = request.RequiresTier2;
        var template = await store.GetAsync(request.TemplateId, request.Version, ct).ConfigureAwait(false);
        if (template is null)
            return TalentInstantiationDecision.Refuse(InstantiationRefusal.UnknownTemplate, tier2);
        if (template.Status != TalentTemplateStatus.Hired)
            return TalentInstantiationDecision.Refuse(InstantiationRefusal.NotHired, tier2, template);
        if (!await hireGate.HasHirePassedAsync(template.TemplateId, template.Version, template.ContentSha, ct).ConfigureAwait(false))
            return TalentInstantiationDecision.Refuse(InstantiationRefusal.HireNotApproved, tier2, template);

        if (request.GrantedTools.Any(t => !template.Manifest.ToolAllowlist.Contains(t, StringComparer.Ordinal)))
            return TalentInstantiationDecision.Refuse(InstantiationRefusal.ToolNotAllowed, tier2, template);

        // A model outside the whitelist is refused at instantiation (docs/design/25, Behaviour), even if it was on the whitelist at submission.
        var installModels = await whitelist.AllowedModelsAsync(request.Actor, ct).ConfigureAwait(false);
        if (request.Models.Count == 0
            || request.Models.Any(m => !template.Manifest.ModelWhitelist.Contains(m, StringComparer.Ordinal) || !installModels.Contains(m)))
            return TalentInstantiationDecision.Refuse(InstantiationRefusal.ModelNotAllowed, tier2, template);

        return new TalentInstantiationDecision(true, InstantiationRefusal.None, tier2, template);
    }
}
