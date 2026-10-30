using System.Text.Json;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Policy;

/// <summary>Shared fixed values and builders for the policy engine tests. Every time is fixed; nothing reads the clock.</summary>
internal static class PolicyFixtures
{
    public static readonly DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid _session = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    public static readonly Guid _hardSourceId = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");

    public static readonly Guid _otherHardSourceId = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");

    public static readonly Guid _softSourceId = Guid.Parse("0196a6f0-0000-7000-8000-000000000203");

    public static readonly ToolCapability _read = new("gc.read", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor);

    public static readonly ToolCapability _notice = new("mail.reply", ToolTier.AutonomousWithNotice, CapabilityClass.Send, SessionRoles.Actor);

    public static readonly ToolCapability _tier2 = new("repo.push", ToolTier.OwnerApproves, CapabilityClass.Push, SessionRoles.Actor);

    public static readonly ToolCapability _human = new("account.create", ToolTier.HumanPerforms, CapabilityClass.CreateAccount, SessionRoles.Actor);

    public static readonly ToolCapability _denied = new("secret.export", ToolTier.Denied, CapabilityClass.Read, SessionRoles.Actor);

    public static readonly ToolCapability _readerOnly = new("web.fetch", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader);

    public static readonly ToolCapability _actorOnly = new("gc.post", ToolTier.Autonomous, CapabilityClass.Post, SessionRoles.Actor);

    /// <summary>A registry holding one tool of each tier and role shape the engine tests use.</summary>
    public static CapabilityRegistry Standard() =>
        CapabilityRegistry.Create([_read, _notice, _tier2, _human, _denied, _readerOnly, _actorOnly]);

    /// <summary>Parses an arguments object. CA2026 asks for <c>JsonElement.Parse</c> over <c>JsonDocument.Parse(...).RootElement</c>.</summary>
    public static JsonElement Args(string json) => JsonElement.Parse(json);

    /// <summary>A session that has seen external content, so it is hard-tainted.</summary>
    public static SessionTaint Hard(Guid? sourceId = null) =>
        TaintRules.Apply(SessionTaint.Clean, new TaintInput(sourceId ?? _hardSourceId, ContentTrust.External), TaintPolicy.Default);

    /// <summary>A session that has seen agent text from a tainted source, so it is soft-tainted.</summary>
    public static SessionTaint Soft() =>
        TaintRules.Apply(SessionTaint.Clean, new TaintInput(_softSourceId, ContentTrust.Agent, TaintState.Soft), TaintPolicy.Default);

    /// <summary>The session taint at a given level.</summary>
    public static SessionTaint TaintAt(TaintState state) => state switch
    {
        TaintState.Clean => SessionTaint.Clean,
        TaintState.Soft => Soft(),
        TaintState.Hard => Hard(),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown taint level."),
    };

    /// <summary>Builds a dispatch context with fixed defaults; each test overrides only what it checks.</summary>
    public static DispatchContext Context(
        string tool,
        SessionRoles role = SessionRoles.Actor,
        SessionTaint? taint = null,
        string agent = "agent-a",
        string? platform = null,
        string? target = null,
        string args = "{}",
        DateTimeOffset? now = null,
        VerifiedApproval? grant = null) =>
        new(agent, _session, role, tool, platform, target, Args(args), taint ?? SessionTaint.Clean, now ?? _now, grant);
}
