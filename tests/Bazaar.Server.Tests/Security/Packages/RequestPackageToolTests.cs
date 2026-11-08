using System.Text.Json;
using Bazaar.Server.Security.Packages;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Packages;

public class RequestPackageToolTests
{
    private static readonly Guid _session = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private const string _nugetOrg = "https://api.nuget.org/v3/index.json";

    /// <summary>Records every context it is given and returns a fixed decision.</summary>
    private sealed class FakeEngine(PolicyDecision answer) : IToolPolicyEngine
    {
        public List<DispatchContext> Calls { get; } = [];

        public PolicyDecision Evaluate(DispatchContext context)
        {
            Calls.Add(context);
            return answer;
        }
    }

    private static RequestPackageTool Tool(IToolPolicyEngine engine) =>
        new(engine, new NugetPackagePolicy(NugetPolicyOptions.Default), TimeProvider.System);

    [Fact]
    public void The_capability_is_tier_2_install_package_for_actors_only()
    {
        var capability = RequestPackageTool.Capability;

        Assert.Equal("request_package", capability.Name);
        Assert.Equal(ToolTier.OwnerApproves, capability.Tier);
        Assert.Equal(CapabilityClass.InstallPackage, capability.Class);
        Assert.Equal(SessionRoles.Actor, capability.Roles);
    }

    [Fact]
    public void A_well_formed_request_calls_the_engine_exactly_once_with_the_install_package_class()
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.OwnerApprovalRequired));

        var result = Tool(engine).Request(_session, "agent-a", "Newtonsoft.Json", "[13.0.3]", _nugetOrg, SessionTaint.Clean);

        var call = Assert.Single(engine.Calls);
        Assert.Equal("request_package", call.Tool);
        Assert.Equal(SessionRoles.Actor, call.Role);
        Assert.Equal("agent-a", call.AgentId);
        Assert.Equal(_session, call.SessionId);
        Assert.Equal(RequestPackageTool.Platform, call.Platform);
        Assert.Equal(PolicyOutcome.RequireApproval, result.Outcome);
        Assert.Equal(PolicyReason.OwnerApprovalRequired, result.EngineReason);
        Assert.Null(result.PackageReason);
    }

    [Fact]
    public void The_engine_decision_is_returned_as_is()
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.ApprovedByGrant));

        var result = Tool(engine).Request(_session, "agent-a", "Newtonsoft.Json", "13.0.3", _nugetOrg, SessionTaint.Clean);

        Assert.Equal(PolicyOutcome.Allow, result.Outcome);
        Assert.Equal(PolicyReason.ApprovedByGrant, result.EngineReason);
    }

    [Fact]
    public void The_arguments_carry_the_pinned_version_and_the_target_names_it()
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.OwnerApprovalRequired));

        Tool(engine).Request(_session, "agent-a", "Newtonsoft.Json", "[13.0.3]", _nugetOrg, SessionTaint.Clean);

        var call = Assert.Single(engine.Calls);
        Assert.Equal("13.0.3", call.Args.GetProperty("version").GetString());
        Assert.Equal("Newtonsoft.Json", call.Args.GetProperty("packageId").GetString());
        Assert.Equal(_nugetOrg, call.Args.GetProperty("sourceFeed").GetString());
        Assert.Equal($"{_nugetOrg}|Newtonsoft.Json@13.0.3", call.Target);
    }

    [Theory]
    [InlineData("1.*", "https://api.nuget.org/v3/index.json", "Newtonsoft.Json")]
    [InlineData("13.0.3", "https://evil.example/index.json", "Newtonsoft.Json")]
    [InlineData("13.0.3", _nugetOrg, "../evil")]
    public void A_request_that_fails_the_package_policy_is_denied_without_reaching_the_engine(string version, string feed, string packageId)
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous));

        var result = Tool(engine).Request(_session, "agent-a", packageId, version, feed, SessionTaint.Clean);

        Assert.Empty(engine.Calls);
        Assert.Equal(PolicyOutcome.Deny, result.Outcome);
        Assert.NotNull(result.PackageReason);
        Assert.Null(result.EngineReason);
    }

    [Fact]
    public void A_hard_tainted_session_is_denied_without_reaching_the_engine()
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.HardTaintTier2));

        var result = Tool(engine).Request(_session, "agent-a", "Newtonsoft.Json", "13.0.3", _nugetOrg, HardTainted());

        Assert.Empty(engine.Calls);
        Assert.Equal(NugetPolicyReason.HardTaintNoPackages, result.PackageReason);
    }

    [Fact]
    public void The_approval_binds_to_the_canonical_feed_and_id_not_the_raw_model_text()
    {
        var engine = new FakeEngine(new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.OwnerApprovalRequired));

        Tool(engine).Request(_session, "agent-a", "Newtonsoft.Json", "[13.0.3]", "HTTPS://API.NUGET.ORG/v3/index.json/", SessionTaint.Clean);

        var call = Assert.Single(engine.Calls);
        Assert.Equal(_nugetOrg, call.Args.GetProperty("sourceFeed").GetString());
        Assert.Equal("Newtonsoft.Json", call.Args.GetProperty("packageId").GetString());
        Assert.Equal($"{_nugetOrg}|Newtonsoft.Json@13.0.3", call.Target);
    }

    private static SessionTaint HardTainted() =>
        TaintRules.Apply(SessionTaint.Clean, new TaintInput(Guid.NewGuid(), ContentTrust.External), TaintPolicy.Default);
}
