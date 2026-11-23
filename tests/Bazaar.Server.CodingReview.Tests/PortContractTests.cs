using System.Reflection;
using Bazaar.Server.CodingReview.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingReview.Tests;

public sealed class PortContractTests
{
    [Fact]
    public void The_result_sink_exposes_no_merge_operation()
    {
        var methods = typeof(IReviewResultSink).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToArray();

        Assert.DoesNotContain(methods, name => name.Contains("Merge", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["PostReviewAsync", "SetApprovalFlagAsync"], methods.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_in_memory_sink_exposes_no_merge_operation_either()
    {
        var methods = typeof(InMemoryReviewResultSink).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(methods, name => name.Contains("Merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Untrusted_text_has_no_implicit_conversion_to_string()
    {
        var implicitOperators = typeof(UntrustedText).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit");

        Assert.Empty(implicitOperators);
    }

    [Fact]
    public void A_null_untrusted_text_wraps_to_empty_text_with_its_source()
    {
        var wrapped = UntrustedText.From(UntrustedTextSource.Diff, null);

        Assert.Equal(string.Empty, wrapped.Text);
        Assert.Equal(UntrustedTextSource.Diff, wrapped.Source);
        Assert.Equal(0, wrapped.Length);
    }

    [Fact]
    public async Task An_approval_sets_the_flag_only_and_the_sink_is_asked_for_nothing_else()
    {
        var sink = new InMemoryReviewResultSink();

        await sink.SetApprovalFlagAsync(ReviewFixtures.PullRequest, true, TestContext.Current.CancellationToken);

        Assert.Equal([(ReviewFixtures.PullRequest, true)], sink.Flags.ToArray());
        Assert.Empty(sink.Posted);
    }

    [Fact]
    public void The_module_registers_the_builder_service_and_default_limits()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReviewContextSources>(new InMemoryReviewContextSources());
        services.AddSingleton<IReviewModelDirectory>(new InMemoryReviewModelDirectory());
        services.AddSingleton<IReviewerSession>(new ScriptedReviewerSession(new ReviewerSessionResult(true, "ok")));
        services.AddSingleton<IReviewResultSink>(new InMemoryReviewResultSink());

        services.AddBazaarCodingReview();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ReviewService>());
        Assert.Equal(ReviewContextLimits.Default, provider.GetRequiredService<ReviewContextLimits>());
    }
}
