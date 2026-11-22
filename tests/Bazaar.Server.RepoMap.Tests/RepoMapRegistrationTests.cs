using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.RepoMap.Tests;

public sealed class RepoMapRegistrationTests
{
    [Fact]
    public void AddBazaarRepoMap_resolves_the_builder_and_search_tool_over_a_host_workspace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepoWorkspace>(new InMemoryRepoWorkspace());
        services.AddBazaarRepoMap();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RepoMapBuilder>(provider.GetRequiredService<IRepoMapBuilder>());
        Assert.IsType<RipgrepSearchTool>(provider.GetRequiredService<ISearchTool>());
        Assert.IsType<CharQuarterTokenCounter>(provider.GetRequiredService<ITokenCounter>());
        Assert.IsType<RoslynCSharpSymbolExtractor>(Assert.Single(provider.GetServices<ISymbolExtractor>()));
    }

    [Fact]
    public void AddBazaarRepoMap_keeps_a_host_registration_that_was_added_first()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenCounter, FixedCounter>();
        services.AddBazaarRepoMap();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FixedCounter>(provider.GetRequiredService<ITokenCounter>());
    }

    [Fact]
    public void Char_quarter_counter_rounds_up_and_counts_empty_as_zero()
    {
        var counter = new CharQuarterTokenCounter();

        Assert.Equal(0, counter.Count(string.Empty));
        Assert.Equal(1, counter.Count("abcd"));
        Assert.Equal(2, counter.Count("abcde"));
        Assert.Equal(3, counter.Count(new string('x', 9)));
    }

    private sealed class FixedCounter : ITokenCounter
    {
        public int Count(string text) => text.Length;
    }
}
