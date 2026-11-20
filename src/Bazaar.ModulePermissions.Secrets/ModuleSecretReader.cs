using System.Text;
using Bazaar.Secrets;

namespace Bazaar.ModulePermissions.Secrets;

/// <summary>
/// The only way a hosted module reads a secret (doc 48 section 1: "A module cannot read a secret it did not name"). Every read
/// is demanded from the permission gate as a <c>secret</c> access before the store is asked, so a name the module does not hold
/// a grant for never reaches the store and the module cannot probe which names exist.
/// </summary>
/// <remarks>
/// A module names a secret as <c>segment/segment</c>. The store holds it as the single <see cref="SecretName"/>
/// <c>mod_&lt;moduleId&gt;_&lt;segment&gt;.&lt;segment&gt;</c> in the installation scope, so the name a module asks for is always
/// inside its own namespace, whatever its grant says. A module id is dot-separated labels (<c>acme.weather</c>); labels and name
/// segments use only lower-case letters, digits and hyphens. That keeps the mapping one-to-one: neither part can hold the
/// <c>_</c> that separates them, so the id ends at the second <c>_</c> and the rest is the name. The prefix
/// <c>mod_</c> is reserved for modules: the operator must not give a platform secret a name that starts with it.
/// </remarks>
public sealed class ModuleSecretReader
{
    /// <summary>Longest module id, in characters.</summary>
    public const int MaxModuleIdLength = 64;

    /// <summary>Longest module secret name, in characters.</summary>
    public const int MaxNameLength = 58;

    private const string _prefix = "mod_";
    private const string _unclean = "A module secret name must be a clean name.";

    private readonly IModulePermissionGate _gate;
    private readonly ISecretStore _store;
    private readonly string _moduleId;
    private readonly HostCaller _caller;

    /// <summary>Creates the reader for one module.</summary>
    public ModuleSecretReader(IModulePermissionGate gate, ISecretStore store, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        if (!IsCleanModuleId(moduleId))
        {
            throw new ArgumentException(
                "A module id is at most 64 characters: dot-separated labels of lower-case letters, digits or hyphens.",
                nameof(moduleId));
        }

        _gate = gate;
        _store = store;
        _moduleId = moduleId;
        // The store's policy grants this component by name. Whatever that grant allows, Map keeps every read inside the
        // module's own mod_<id>_ names.
        _caller = new HostCaller("module." + moduleId);
    }

    /// <summary>The purpose recorded in the store's audit for every module read.</summary>
    public static SecretPurpose Purpose { get; } = new("module.secret.read");

    /// <summary>
    /// Reads the latest version of the module's secret <paramref name="name"/>. The caller disposes the result as soon as it
    /// has used it. Throws <see cref="ModulePermissionDeniedException"/> when the gate refuses (the store is not asked), and
    /// <see cref="ModuleSecretRefusedException"/> when the name cannot be a module secret or the store could not give the
    /// value. The exception messages never carry the name or a value.
    /// </summary>
    public async Task<SecretValue> ReadAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();

        // The gate decides first and audits a malformed name itself, so a refusal here never depends on the store.
        _gate.Demand(_moduleId, new ModuleAccess(ModulePermissionClasses.Secret, name));
        if (!ModuleTargetRules.IsValidTarget(name))
        {
            throw new ModuleSecretRefusedException(_unclean);
        }

        var storeName = Map(_moduleId, name);
        try
        {
            return await _store.GetAsync(_caller, SecretScope.Installation, storeName, Purpose, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not found, refused by the store's policy and a store fault look the same to the module, and the store's message
            // is not passed on: it could carry the store-side name.
            throw new ModuleSecretRefusedException("The secret could not be read.");
        }
    }

    /// <summary>The store name of a module's secret. Throws <see cref="ModuleSecretRefusedException"/> when the name cannot be mapped.</summary>
    public static SecretName Map(string moduleId, string name)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(name);
        if (!IsCleanModuleId(moduleId) || name.Length > MaxNameLength)
        {
            throw new ModuleSecretRefusedException(_unclean);
        }

        var segments = name.Split('/');
        if (segments.Any(static s => !IsCleanSegment(s, MaxNameLength)))
        {
            throw new ModuleSecretRefusedException(_unclean);
        }

        return new SecretName(new StringBuilder(_prefix).Append(moduleId).Append('_').AppendJoin('.', segments).ToString());
    }

    private static bool IsCleanModuleId(string moduleId) =>
        moduleId.Length <= MaxModuleIdLength && moduleId.Split('.').All(static l => IsCleanSegment(l, MaxModuleIdLength));

    private static bool IsCleanSegment(string segment, int maxLength) =>
        segment.Length > 0 && segment.Length <= maxLength
        && (char.IsAsciiDigit(segment[0]) || char.IsAsciiLetterLower(segment[0]))
        && segment.All(static c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c) || c == '-');
}
