using System.Security.Cryptography;
using Bazaar.Contracts.Gc;
using Bazaar.Core;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.CouncilAdapters;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace Bazaar.Server.Host.Tests;

public sealed class CouncilComponentTests
{
    private sealed class RecordingWriter : IGcLogWriter
    {
        public List<AppendRequest> Requests { get; } = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new AppendResult(request.GcId, Requests.Count, Guid.NewGuid(), false, DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Manifest_names_council_depending_on_gc_with_a_soft_vault_dependency()
    {
        var manifest = CouncilComponent.ManifestDefinition;

        Assert.Equal("council", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc"], manifest.DependsOn);
        Assert.Equal(["vault"], manifest.Soft);
        Assert.Equal("council", manifest.MigrationNamespace);
    }

    [Fact]
    public void Host_ships_the_council_component_after_gc_and_its_migrations_are_in_the_source()
    {
        Assert.Single(HostComponents.All, c => c.Manifest.Id == "council");
        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();
        Assert.True(order.IndexOf("gc") < order.IndexOf("council"));

        var source = HostMigrations.Source();
        Assert.Contains("council", source.Namespaces());
        Assert.Equal([1, 2, 3, 4], source.Load("council").Select(f => f.Version).ToList());
    }

    [Fact]
    public void The_host_graph_resolves_the_council_service_and_store()
    {
        const string dbEnv = "BAZAAR_HOST_COUNCIL_TEST_DB";
        const string tokenEnv = "BAZAAR_HOST_COUNCIL_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-graph-test");
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-test-" + Guid.NewGuid().ToString("N")),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            });
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            // The options provider reads council.* when the service is first resolved; the graph test has no database, so it reads defaults.
            builder.Services.Replace(ServiceDescriptor.Singleton<ISettingStore>(new DefaultsSettingStore()));
            using var host = builder.Build();

            Assert.IsType<CouncilService>(host.Services.GetRequiredService<ICouncilService>());
            Assert.IsType<CouncilStore>(host.Services.GetRequiredService<ICouncilStore>());
            Assert.IsType<CouncilGcEventsAdapter>(host.Services.GetRequiredService<ICouncilGcEvents>());
            Assert.IsType<PgGcMembershipAdapter>(host.Services.GetRequiredService<IGcMembership>());
            Assert.IsType<PersonKeyCouncilReasoningCipher>(host.Services.GetRequiredService<ICouncilReasoningCipher>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }

    [Fact]
    public async Task Gc_events_adapter_appends_a_system_event_keyed_by_the_idempotency_key()
    {
        var writer = new RecordingWriter();
        var gcId = Guid.NewGuid();

        await new CouncilGcEventsAdapter(writer).AppendAsync(gcId, "council.open", "{\"a\":1}", "council:v1:open", CancellationToken.None);

        var request = Assert.Single(writer.Requests);
        Assert.Equal(gcId, request.GcId);
        Assert.Equal("council.open", request.Kind);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
        Assert.Equal(CouncilGcEventsAdapter.SystemAuthorId, request.AuthorId);
        Assert.Equal(SourceTrust.System, request.SourceTrust);
        Assert.Equal("council:v1:open", request.ClientMsgId);
        Assert.Equal("{\"a\":1}", request.PayloadJson);
    }

    [Fact]
    public async Task Gc_events_adapter_refuses_a_key_longer_than_a_client_message_id()
    {
        var adapter = new CouncilGcEventsAdapter(new RecordingWriter());
        var tooLong = new string('k', AppendRequest.MaxClientMsgIdLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.AppendAsync(Guid.NewGuid(), "council.open", "{}", tooLong, CancellationToken.None));
    }

    [Theory]
    [InlineData("owner", GcMemberKind.Owner)]
    [InlineData("agent", GcMemberKind.Agent)]
    public void Membership_maps_owner_and_agent_kinds(string kind, GcMemberKind expected)
    {
        var id = Guid.NewGuid();

        var member = PgGcMembershipAdapter.Map(id, kind);

        Assert.Equal(new GcMember(id.ToString("D"), expected), member);
    }

    [Theory]
    [InlineData("human")]
    [InlineData(null)]
    public void Membership_skips_humans_and_principals_of_unknown_kind(string? kind) =>
        Assert.Null(PgGcMembershipAdapter.Map(Guid.NewGuid(), kind));

    [Fact]
    public void Membership_query_reads_current_members_who_have_not_left()
    {
        Assert.Contains("gc_member_current", PgGcMembershipAdapter.MembersSql);
        Assert.Contains("left_seq IS NULL", PgGcMembershipAdapter.MembersSql);
    }

    [Fact]
    public async Task Cipher_refuses_a_voter_id_that_is_not_a_person_guid()
    {
        // The id is checked before the key store is touched, so no database is needed.
        var cipher = new PersonKeyCouncilReasoningCipher(new PersonKeyStore(null!, null!));

        await Assert.ThrowsAsync<ArgumentException>(async () => await cipher.SealAsync("agent-7", "why", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await cipher.OpenAsync("agent-7", new ReasoningCiphertext(Guid.NewGuid(), new byte[16], new byte[12]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await cipher.SealAsync(Guid.Empty.ToString("D"), "why", CancellationToken.None));
    }

    [Fact]
    public async Task Cipher_refuses_a_malformed_seal_before_it_reads_a_key()
    {
        var cipher = new PersonKeyCouncilReasoningCipher(new PersonKeyStore(null!, null!));
        var voter = Guid.NewGuid().ToString("D");

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await cipher.OpenAsync(voter, new ReasoningCiphertext(Guid.NewGuid(), new byte[4], new byte[12]), CancellationToken.None));
        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await cipher.OpenAsync(voter, new ReasoningCiphertext(Guid.NewGuid(), new byte[32], new byte[3]), CancellationToken.None));
    }

    private sealed class DefaultsSettingStore : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) => Task.FromResult(key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
