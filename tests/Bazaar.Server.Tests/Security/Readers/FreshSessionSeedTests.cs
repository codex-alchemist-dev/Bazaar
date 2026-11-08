using System.Reflection;
using Bazaar.Server.Security.Readers;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Readers;

public class FreshSessionSeedTests
{
    private static readonly OwnerPrincipal _owner = new("owner-1");

    [Fact]
    public void Seed_holds_owner_and_system_text_and_the_approved_summary_in_order()
    {
        var summary = ApprovedSummary.Approve(_owner, "Reviewed three threads; two need replies.");

        var seed = FreshSessionSeed.Build(
            [TrustedContent.FromOwner("Keep replies short."), TrustedContent.FromSystem("Today is the review day.")],
            summary);

        var owner = seed.Text.IndexOf("Keep replies short.", StringComparison.Ordinal);
        var system = seed.Text.IndexOf("Today is the review day.", StringComparison.Ordinal);
        var approved = seed.Text.IndexOf("Reviewed three threads", StringComparison.Ordinal);
        Assert.InRange(owner, 0, system);
        Assert.InRange(system, 0, approved);
        Assert.Equal(SessionTaint.Clean, seed.Taint);
    }

    [Fact]
    public async Task Seed_excludes_reader_scratch_and_hand_over_text()
    {
        // A reader wrote this into scratch and a validated hand-over carried it. Neither can reach the seed,
        // because Build accepts only trusted content and an approved summary.
        var scratch = new InMemoryReaderScratchStore();
        var sessionId = Guid.Parse("0196a6f0-0000-7000-8000-000000000501");
        await scratch.PutAsync(sessionId, "note", "TAINTED-SCRAPE", ReaderTestData.Ct);
        var handover = Assert.IsType<HandoverResult.Accepted>(
            ReaderTestData.Validator().Validate("test.sample", ReaderTestData.Parse("""{"subject":"TAINTED-HANDOVER","priority":"low","score":1}"""))).Handover;
        Assert.NotNull(handover);

        var seed = FreshSessionSeed.Build(
            [TrustedContent.FromOwner("Owner brief.")],
            ApprovedSummary.Approve(_owner, "Approved summary only."));

        Assert.DoesNotContain("TAINTED", seed.Text, StringComparison.Ordinal);
        Assert.Contains("Owner brief.", seed.Text, StringComparison.Ordinal);
        Assert.Contains("Approved summary only.", seed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_parameters_are_only_trusted_content_and_an_approved_summary()
    {
        var parameters = typeof(FreshSessionSeed).GetMethod(nameof(FreshSessionSeed.Build))!.GetParameters();

        Assert.Equal(
            [typeof(IEnumerable<TrustedContent>), typeof(ApprovedSummary)],
            parameters.Select(p => p.ParameterType));
    }

    [Fact]
    public void Trusted_content_can_only_be_owner_or_system()
    {
        Assert.Equal(ContentTrust.Owner, TrustedContent.FromOwner("text").Trust);
        Assert.Equal(ContentTrust.System, TrustedContent.FromSystem("text").Trust);
        Assert.Throws<ArgumentException>(() => TrustedContent.FromOwner(" "));
        Assert.Throws<ArgumentException>(() => TrustedContent.FromSystem(""));
    }

    [Fact]
    public void Only_an_owner_can_approve_a_summary()
    {
        var approve = typeof(ApprovedSummary).GetMethod(nameof(ApprovedSummary.Approve), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(approve);
        Assert.Equal([typeof(OwnerPrincipal), typeof(string)], approve.GetParameters().Select(p => p.ParameterType));
        Assert.Throws<ArgumentException>(() => ApprovedSummary.Approve(_owner, "  "));
    }
}
