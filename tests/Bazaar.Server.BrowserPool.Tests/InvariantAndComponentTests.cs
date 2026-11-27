using System.Reflection;
using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool.Tests;

public class StructuralInvariantTests
{
    private static readonly string[] _forbiddenWords = ["cookie", "credential", "password", "token", "secret"];

    private static IEnumerable<string> AllPublicNames(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetExportedTypes())
        {
            yield return type.Name;
            foreach (var member in type.GetMembers(all))
            {
                var isPublic = member switch
                {
                    MethodBase m => m.IsPublic || m.IsFamily,
                    FieldInfo f => f.IsPublic || f.IsFamily,
                    PropertyInfo p => (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false),
                    EventInfo => true,
                    Type t => t.IsNestedPublic,
                    _ => false,
                };
                if (!isPublic)
                {
                    continue;
                }

                yield return member.Name;
                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.Name ?? string.Empty;
                    }
                }

                if (member is PropertyInfo prop)
                {
                    foreach (var parameter in prop.GetIndexParameters())
                    {
                        yield return parameter.Name ?? string.Empty;
                    }
                }
            }
        }
    }

    [Fact]
    public void Abstractions_has_no_public_name_that_could_carry_a_credential()
    {
        var offenders = AllPublicNames(typeof(IBrowserPool).Assembly)
            .Where(n => _forbiddenWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_name_scan_actually_sees_members_and_parameters()
    {
        var names = AllPublicNames(typeof(IBrowserPool).Assembly).ToList();

        Assert.Contains("OpenAsync", names);
        Assert.Contains("request", names);
        Assert.Contains("AgentId", names);
        Assert.Contains("sessionId", names);
    }

    [Fact]
    public void The_implementation_assembly_also_exposes_no_credential_names_in_its_public_surface()
    {
        var offenders = AllPublicNames(typeof(BrowserPoolService).Assembly)
            .Where(n => _forbiddenWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_session_request_or_session_field_is_a_string_other_than_identifiers_and_the_url()
    {
        var names = typeof(PoolSessionRequest).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(["AgentId", "Display", "Height", "StartUrl", "Width"], names);
    }

    [Fact]
    public void Abstractions_references_nothing_beyond_the_base_library()
    {
        var refs = typeof(IBrowserPool).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(refs, r => r!.StartsWith("Bazaar.", StringComparison.Ordinal) && r != "Cdax.Core");
        Assert.DoesNotContain(refs, r => r!.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Implementation_references_only_core_and_its_own_abstractions()
    {
        var bazaar = typeof(BrowserPoolService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Bazaar.", StringComparison.Ordinal))
            .Order()
            .ToList();

        Assert.Equal(["Cdax.Core", "Cdax.Core.Hosting", "Bazaar.Server.BrowserPool.Abstractions"], bazaar);
    }

    [Fact]
    public void No_public_method_takes_a_profile_directory_or_path()
    {
        var methods = typeof(IBrowserPool).GetMethods().Concat(typeof(IPoolVmHost).GetMethods());
        var parameterNames = methods.SelectMany(m => m.GetParameters()).Select(p => p.Name!.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(parameterNames, n => n.Contains("profile", StringComparison.Ordinal) || n.Contains("path", StringComparison.Ordinal) || n.Contains("directory", StringComparison.Ordinal));
    }
}

public class ComponentTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null, Action<IServiceCollection>? before = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var services = new ServiceCollection();
        before?.Invoke(services);
        new BrowserPoolComponent().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        var m = new BrowserPoolComponent().Manifest;

        Assert.Equal("browser-pool", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vms", "browser", "security", "audit", "vm-display"], m.SoftDependsOn);
        Assert.Equal(["browser-pool:sessions"], m.Provides);
        Assert.Equal("browser_pool", m.MigrationNamespace);
    }

    [Fact]
    public void Registers_the_pool_and_binds_options()
    {
        using var sp = Build(new() { ["Bazaar:BrowserPool:MaxSessions"] = "8", ["Bazaar:BrowserPool:MaxSessionsPerAgent"] = "2" });

        var options = sp.GetRequiredService<IOptions<BrowserPoolOptions>>().Value;

        Assert.Equal(8, options.MaxSessions);
        Assert.IsType<BrowserPoolService>(sp.GetRequiredService<IBrowserPool>());
    }

    [Fact]
    public void Invalid_configuration_fails_when_options_are_read()
    {
        using var sp = Build(new() { ["Bazaar:BrowserPool:SlotRoot"] = "/var" });

        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<BrowserPoolOptions>>().Value);
    }

    [Fact]
    public void Default_gate_blocks_everyone()
    {
        using var sp = Build();

        Assert.IsType<UnconfiguredPoolGate>(sp.GetRequiredService<IPoolGate>());
        Assert.True(sp.GetRequiredService<IPoolGate>().IsBlocked("any-agent"));
    }

    [Fact]
    public async Task Out_of_the_box_nothing_opens()
    {
        using var sp = Build();

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => sp.GetRequiredService<IBrowserPool>().OpenAsync(new PoolSessionRequest("a"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
    }

    [Fact]
    public async Task Default_egress_policy_gives_no_proxy_and_default_host_fails()
    {
        using var sp = Build();

        Assert.Null(await sp.GetRequiredService<IPoolEgressPolicy>().ResolveAsync("a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<IPoolVmHost>().RunAsync(new PoolCommand(["true"]), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<IPoolVmHost>().ApplyFirewallAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Default_audit_sink_is_a_no_op_that_does_not_throw()
    {
        using var sp = Build();
        var sink = sp.GetRequiredService<IPoolAuditSink>();
        var session = new PoolSession("s", "a", 0, PoolDisplayMode.Headless, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        sink.Opened(session);
        sink.Closed(session, PoolCloseReason.Requested);
        sink.Refused("a", PoolFailure.Disabled, "x");
        sink.SlotQuarantined(0, "s", "a", "x");
    }

    [Fact]
    public void Host_registrations_made_first_win_over_the_defaults()
    {
        var gate = new FakeGate();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var sp = Build(before: s =>
        {
            s.AddSingleton<IPoolGate>(gate);
            s.AddSingleton<TimeProvider>(time);
        });

        Assert.Same(gate, sp.GetRequiredService<IPoolGate>());
        Assert.Same(time, sp.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task With_real_ports_registered_a_session_opens_through_the_container()
    {
        var host = new FakeHost();
        using var sp = Build(before: s =>
        {
            s.AddSingleton<IPoolGate>(new FakeGate());
            s.AddSingleton<IPoolEgressPolicy>(new FakeEgress());
            s.AddSingleton<IPoolVmHost>(host);
        });

        var session = await sp.GetRequiredService<IBrowserPool>().OpenAsync(new PoolSessionRequest("a"), TestContext.Current.CancellationToken);

        Assert.Equal(0, session.Slot);
        Assert.Single(host.Started);
    }

    [Fact]
    public void The_pool_is_a_singleton()
    {
        using var sp = Build();

        Assert.Same(sp.GetRequiredService<IBrowserPool>(), sp.GetRequiredService<IBrowserPool>());
    }
}
