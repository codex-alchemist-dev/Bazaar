using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Council.Hiring.Tests;

public class HiringServiceRegistrationTests
{
    private static ServiceProvider Build(IEnumerable<string>? classes = null, bool withTime = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICouncilService>(new FakeCouncilService());
        services.AddSingleton<IHireDecisionStore>(new InMemoryHireDecisionStore());
        if (withTime)
        {
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Samples.Now));
        }

        services.AddBazaarCouncilHiring(classes);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Registration_resolves_the_gate_builder_and_sink()
    {
        using var provider = Build();

        Assert.IsType<CouncilTalentHireGate>(provider.GetRequiredService<IHireGate>());
        Assert.IsType<HireMotionDraftBuilder>(provider.GetRequiredService<HireMotionDraftBuilder>());
        Assert.IsType<HireOutcomeSink>(provider.GetRequiredService<IMembershipOutcomeSink>());
    }

    [Fact]
    public void Registration_applies_the_configured_bigger_change_classes()
    {
        using var provider = Build(classes: ["retire"]);
        var builder = provider.GetRequiredService<HireMotionDraftBuilder>();

        var motion = builder.Build(Samples.Request());

        Assert.Equal(MotionClass.Ordinary, motion.Draft.MotionClass);
    }

    [Fact]
    public void Registration_with_no_classes_uses_the_default_bigger_change()
    {
        using var provider = Build();
        var builder = provider.GetRequiredService<HireMotionDraftBuilder>();

        Assert.Equal(MotionClass.BiggerChange, builder.Build(Samples.Request()).Draft.MotionClass);
    }

    [Fact]
    public void Unknown_configured_class_fails_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddBazaarCouncilHiring(["hire", "not_a_class"]));
    }

    [Fact]
    public void Host_time_provider_is_kept_when_registered()
    {
        using var provider = Build(withTime: true);

        Assert.IsType<FakeTimeProvider>(provider.GetRequiredService<TimeProvider>());
    }
}
