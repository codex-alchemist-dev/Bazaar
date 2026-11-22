using System.Buffers;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Packages;

/// <summary>The outcome of one <c>request_package</c> call.</summary>
/// <param name="Outcome">What the dispatcher does.</param>
/// <param name="PackageReason">Set when the NuGet policy decided (a denial before the engine ran).</param>
/// <param name="EngineReason">Set when the tool policy engine decided.</param>
public sealed record PackageRequestResult(PolicyOutcome Outcome, NugetPolicyReason? PackageReason, PolicyReason? EngineReason);

/// <summary>
/// The <c>request_package</c> tool. It runs the NuGet policy first; a request that fails it is denied without reaching
/// the engine. A well-formed request always goes through <see cref="IToolPolicyEngine"/> exactly once, which decides
/// Tier 2 approval, grants and quotas. The tool never allows a call on its own.
/// </summary>
/// <remarks>
/// TODO(component): the install step (feed lookup, restore and <c>dotnet add package</c>) and the Postgres record of
/// package request status are not built yet. Status changes go through <see cref="PackageRequestStatusPublisher"/>.
/// </remarks>
public sealed class RequestPackageTool(IToolPolicyEngine engine, NugetPackagePolicy policy, TimeProvider? timeProvider = null)
{
    /// <summary>Tool name the model calls.</summary>
    public const string Name = "request_package";

    /// <summary>Platform key for quotas and audit.</summary>
    public const string Platform = "nuget";

    private readonly IToolPolicyEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly NugetPackagePolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The registry entry the server adds at startup: Tier 2, install-package, actor sessions only. Readers hold no
    /// outward tools (doc 15), so a reader can never request an install.
    /// </summary>
    public static ToolCapability Capability { get; } = new(Name, ToolTier.OwnerApproves, CapabilityClass.InstallPackage, SessionRoles.Actor);

    /// <summary>Asks to install one exact package version for an agent.</summary>
    /// <param name="sessionId">Session the call runs in.</param>
    /// <param name="agentId">Calling agent, set by the server.</param>
    /// <param name="packageId">Package id from the model.</param>
    /// <param name="versionRange">Version from the model. Must be an exact pin.</param>
    /// <param name="sourceFeed">Feed URL from the model.</param>
    /// <param name="taint">The session's taint now.</param>
    /// <param name="grant">A verified owner approval for this exact call, if one exists.</param>
    public PackageRequestResult Request(
        Guid sessionId,
        string agentId,
        string? packageId,
        string? versionRange,
        string? sourceFeed,
        SessionTaint taint,
        VerifiedApproval? grant = null)
    {
        ArgumentNullException.ThrowIfNull(taint);

        var request = new PackageRequest(packageId, versionRange, sourceFeed, agentId, taint.State);
        var packageDecision = _policy.Evaluate(request);
        if (packageDecision.Outcome == PolicyOutcome.Deny)
        {
            return new PackageRequestResult(PolicyOutcome.Deny, packageDecision.Reason, null);
        }

        // The arguments carry the pinned version and the target names the same pin, so the approval hash binds to it.
        // The id and feed are the canonical forms the policy checked, not the raw model text.
        var pinned = packageDecision.PinnedVersion!;
        var feed = packageDecision.CanonicalFeed!;
        var id = packageId!;
        var target = $"{feed}|{id}@{pinned}";
        var context = new DispatchContext(
            agentId,
            sessionId,
            SessionRoles.Actor,
            Name,
            Platform,
            target,
            ArgsElement(id, pinned, feed),
            taint,
            _time.GetUtcNow(),
            grant);

        var decision = _engine.Evaluate(context);
        return new PackageRequestResult(decision.Outcome, null, decision.Reason);
    }

    private static JsonElement ArgsElement(string packageId, string version, string sourceFeed)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("packageId", packageId);
            writer.WriteString("sourceFeed", sourceFeed);
            writer.WriteString("version", version);
            writer.WriteEndObject();
        }

        return JsonElement.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
