using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Provenance;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Gc.Provenance;

/// <summary>The taint rules of <see cref="DefaultGcTrustHook"/> (docs/design/01 "Provenance and trust", docs/design/15).</summary>
public sealed class DefaultGcTrustHookTests
{
    private static readonly Guid Gc = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly Guid Author = Guid.Parse("00000000-0000-7000-8000-0000000000a1");

    private static AppendRequest Request(AuthorKind authorKind, SourceTrust trust) =>
        new(Gc, GcEventKinds.Message, authorKind, Author, trust, "k1");

    [Theory]
    [InlineData(AuthorKind.Owner, SourceTrust.Owner, false)]
    [InlineData(AuthorKind.System, SourceTrust.System, false)]
    [InlineData(AuthorKind.Human, SourceTrust.MemberHuman, false)]
    [InlineData(AuthorKind.Agent, SourceTrust.Agent, false)]
    [InlineData(AuthorKind.Agent, SourceTrust.External, true)]
    [InlineData(AuthorKind.Human, SourceTrust.External, true)]
    [InlineData(AuthorKind.System, SourceTrust.External, true)]
    [InlineData(AuthorKind.ExternalBridge, SourceTrust.External, true)]
    [InlineData(AuthorKind.ExternalBridge, SourceTrust.Agent, false)]
    [InlineData(AuthorKind.ExternalBridge, SourceTrust.Quarantined, false)]
    [InlineData(AuthorKind.Agent, SourceTrust.Quarantined, false)]
    [InlineData(AuthorKind.Owner, SourceTrust.Quarantined, false)]
    public void Unset_taint_is_derived_from_author_kind_and_source_trust(AuthorKind authorKind, SourceTrust trust, bool expected)
    {
        var resolved = new DefaultGcTrustHook().Resolve(Request(authorKind, trust));

        Assert.Equal(expected, resolved.TaintedAtWrite);
        Assert.Null(resolved.TaintSourcesJson);
    }

    [Fact]
    public void Agent_text_with_tainted_lineage_is_soft_tainted_and_keeps_the_lineage()
    {
        const string lineage = """["mail-7","issue-3"]""";
        var request = Request(AuthorKind.Agent, SourceTrust.Agent) with { TaintSourcesJson = lineage };

        var resolved = new DefaultGcTrustHook().Resolve(request);

        Assert.True(resolved.TaintedAtWrite);
        Assert.Equal(lineage, resolved.TaintSourcesJson);
    }

    [Fact]
    public void Agent_text_with_an_empty_lineage_is_clean()
    {
        var request = Request(AuthorKind.Agent, SourceTrust.Agent) with { TaintSourcesJson = "[]" };

        var resolved = new DefaultGcTrustHook().Resolve(request);

        Assert.False(resolved.TaintedAtWrite);
        Assert.Equal("[]", resolved.TaintSourcesJson);
    }

    [Fact]
    public void Explicit_taint_is_kept_even_where_the_rules_would_derive_otherwise()
    {
        var hook = new DefaultGcTrustHook();
        var untaintedExternal = Request(AuthorKind.Agent, SourceTrust.External) with { TaintedAtWrite = false };
        var taintedOwner = Request(AuthorKind.Owner, SourceTrust.Owner) with { TaintedAtWrite = true, TaintSourcesJson = """["x"]""" };

        Assert.False(hook.Resolve(untaintedExternal).TaintedAtWrite);
        var owner = hook.Resolve(taintedOwner);
        Assert.True(owner.TaintedAtWrite);
        Assert.Equal("""["x"]""", owner.TaintSourcesJson);
    }

    [Fact]
    public void Resolve_returns_the_same_instance_when_nothing_is_derived()
    {
        var explicitRequest = Request(AuthorKind.Owner, SourceTrust.Owner) with { TaintedAtWrite = false };

        Assert.Same(explicitRequest, new DefaultGcTrustHook().Resolve(explicitRequest));
    }

    [Fact]
    public void Resolve_leaves_every_other_field_unchanged()
    {
        var replyTo = 4L;
        var request = Request(AuthorKind.Agent, SourceTrust.External) with
        {
            SessionId = Guid.CreateVersion7(),
            ReplyToSeq = replyTo,
            PayloadJson = """{"n":1}""",
            Mentions = [Guid.CreateVersion7()],
        };

        var resolved = new DefaultGcTrustHook().Resolve(request);

        Assert.True(resolved.TaintedAtWrite);
        Assert.Equal(request.SessionId, resolved.SessionId);
        Assert.Equal(replyTo, resolved.ReplyToSeq);
        Assert.Equal(request.PayloadJson, resolved.PayloadJson);
        Assert.Equal(request.Mentions, resolved.Mentions);
        Assert.Equal(request.ClientMsgId, resolved.ClientMsgId);
    }

    [Fact]
    public void Null_request_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new DefaultGcTrustHook().Resolve(null!));
    }

    [Theory]
    [InlineData("""{"mail":1}""")]
    [InlineData("\"mail-7\"")]
    [InlineData("[")]
    [InlineData("")]
    public void Lineage_that_is_not_a_json_array_is_rejected(string lineage)
    {
        var request = Request(AuthorKind.Agent, SourceTrust.Agent) with { TaintSourcesJson = lineage };

        var ex = Assert.Throws<ArgumentException>(() => new DefaultGcTrustHook().Resolve(request));
        Assert.Equal("request", ex.ParamName);
    }

    [Fact]
    public void Add_bazaar_gc_provenance_registers_the_default_hook_once()
    {
        var services = new ServiceCollection();
        services.AddBazaarGcProvenance();
        services.AddBazaarGcProvenance();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DefaultGcTrustHook>(provider.GetRequiredService<IGcTrustHook>());
        Assert.Single(services, d => d.ServiceType == typeof(IGcTrustHook));
    }

    [Fact]
    public void Add_bazaar_gc_provenance_keeps_an_earlier_registration()
    {
        var services = new ServiceCollection();
        var custom = new FixedHook();
        services.AddSingleton<IGcTrustHook>(custom);
        services.AddBazaarGcProvenance();
        using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IGcTrustHook>());
    }

    [Fact]
    public void Add_gc_log_registers_the_trust_hook_through_the_gc_composition()
    {
        var services = new ServiceCollection();
        services.AddGcLog();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DefaultGcTrustHook>(provider.GetRequiredService<IGcTrustHook>());
    }

    private sealed class FixedHook : IGcTrustHook
    {
        public AppendRequest Resolve(AppendRequest request) => request;
    }
}
