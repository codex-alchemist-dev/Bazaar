using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Portability.KanbanTarget.Tests;

public class GcImportSinkTests
{
    private static readonly GcPrincipal _owner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), PrincipalKind.Owner, "Owner");
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    private sealed class FakeProvisioner : IGcProvisioner
    {
        public Dictionary<Guid, (string Type, string Title, IReadOnlyDictionary<string, string>? Metadata)> Created { get; } = [];

        public Task<bool> CreateProjectGcAsync(Guid gcId, string title, GcPrincipal owner, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CreateAsync(Guid gcId, string typeKey, string title, GcPrincipal owner, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
        {
            if (Created.ContainsKey(gcId)) return Task.FromResult(false);
            Created[gcId] = (typeKey, title, metadata);
            return Task.FromResult(true);
        }

        public Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct)
        {
            var hit = Created.FirstOrDefault(kv => kv.Value.Type == typeKey
                && kv.Value.Metadata is not null && kv.Value.Metadata.TryGetValue(metadataKey, out var v) && v == metadataValue);
            return Task.FromResult<Guid?>(hit.Value.Type is null ? null : hit.Key);
        }

        public Task<bool> AddMemberAsync(Guid gcId, GcPrincipal member, CancellationToken ct) => throw new NotSupportedException();

        public Task<Guid> EnsureDmAsync(GcPrincipal first, GcPrincipal second, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeMessages : IImportGcMessages
    {
        public List<(Guid GcId, string Text)> Stored { get; } = [];

        public Task<Guid> AppendAsync(Guid gcId, string text, CancellationToken ct)
        {
            Stored.Add((gcId, text));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<string>> ReadTextsAsync(Guid gcId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Stored.Where(s => s.GcId == gcId).Select(s => s.Text).ToList());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FixedClock Clock() => new(_now);

    [Fact]
    public async Task Job_gc_is_created_once_with_the_job_id_in_metadata()
    {
        var provisioner = new FakeProvisioner();
        var sink = new GcImportSink(provisioner, new FakeMessages(), _owner, Clock());

        var first = await sink.EnsureJobGcAsync("job-1", "Launch board", CancellationToken.None);
        var second = await sink.EnsureJobGcAsync("job-1", "Launch board", CancellationToken.None);

        Assert.Equal(first, second);
        var created = Assert.Single(provisioner.Created);
        Assert.Equal(GcImportSink.TypeKey, created.Value.Type);
        Assert.Equal("job-1", created.Value.Metadata![GcImportSink.JobMetadataKey]);
        Assert.Equal("Launch board import 2026-11-22 18:00 UTC", created.Value.Title);
    }

    [Fact]
    public void Job_gc_id_differs_by_job_and_by_source()
    {
        Assert.NotEqual(GcImportSink.JobGcId("job-1", "A"), GcImportSink.JobGcId("job-2", "A"));
        Assert.NotEqual(GcImportSink.JobGcId("job-1", "A"), GcImportSink.JobGcId("job-1", "B"));
        Assert.Equal(GcImportSink.JobGcId("job-1", "A"), GcImportSink.JobGcId("job-1", "A"));
    }

    [Fact]
    public async Task Messages_go_to_the_job_gc_and_are_read_back_through_the_metadata_lookup()
    {
        var provisioner = new FakeProvisioner();
        var messages = new FakeMessages();
        var sink = new GcImportSink(provisioner, messages, _owner, Clock());
        var gcId = await sink.EnsureJobGcAsync("job-1", "Launch board", CancellationToken.None);

        await sink.AppendMessageAsync(gcId, "[import:a] A", CancellationToken.None);
        await sink.AppendMessageAsync(gcId, "[import:b] B", CancellationToken.None);

        var read = await sink.ReadJobMessagesAsync("job-1", CancellationToken.None);
        Assert.Equal(["[import:a] A", "[import:b] B"], read);
    }

    [Fact]
    public async Task Reading_a_job_with_no_gc_returns_nothing()
    {
        var sink = new GcImportSink(new FakeProvisioner(), new FakeMessages(), _owner, Clock());

        Assert.Empty(await sink.ReadJobMessagesAsync("never-created", CancellationToken.None));
    }
}
