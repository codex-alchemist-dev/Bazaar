using Bazaar.Server.Git.Forge;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.Tests.Forge;

public class ForgeRegistryTests
{
    [Fact]
    public void Resolves_provider_and_executor_by_id_ignoring_case()
    {
        var forge = new FakeForge("github");
        var exec = new FakeExecutor("github");
        var registry = new ForgeRegistry([forge], [exec]);

        Assert.Same(forge, registry.Resolve("GitHub"));
        Assert.Same(exec, registry.ResolveMergeExecutor("github"));
        Assert.Equal(["github"], registry.ProviderIds);
    }

    [Fact]
    public void Duplicate_provider_ids_fail_at_construction() =>
        Assert.Throws<InvalidOperationException>(() => new ForgeRegistry([new FakeForge("a"), new FakeForge("A")], []));

    [Fact]
    public void Duplicate_executor_ids_fail_at_construction() =>
        Assert.Throws<InvalidOperationException>(() => new ForgeRegistry([], [new FakeExecutor("a"), new FakeExecutor("a")]));

    [Fact]
    public void Unknown_id_throws_typed_exception_with_the_id()
    {
        var registry = new ForgeRegistry([new FakeForge("github")], [new FakeExecutor("github")]);

        Assert.Equal("nope", Assert.Throws<UnknownForgeProviderException>(() => registry.Resolve("nope")).ProviderId);
        Assert.Throws<UnknownForgeProviderException>(() => registry.ResolveMergeExecutor("nope"));
    }

    [Fact]
    public void Di_extension_collects_all_registered_providers_and_executors()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForgeProvider>(new FakeForge("github"));
        services.AddSingleton<IForgeProvider>(new FakeForge("gitlab"));
        services.AddSingleton<IForgeMergeExecutor>(new FakeExecutor("gitlab"));
        services.AddBazaarForgeRegistry();

        var registry = services.BuildServiceProvider().GetRequiredService<IForgeRegistry>();

        Assert.Equal(2, registry.ProviderIds.Count);
        Assert.NotNull(registry.ResolveMergeExecutor("gitlab"));
        Assert.Throws<UnknownForgeProviderException>(() => registry.ResolveMergeExecutor("github"));
    }
}
