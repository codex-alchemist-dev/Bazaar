using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Retention;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Retention.Tests;

internal sealed class FakeCategory(string name = "fake") : IRetentionCategory
{
    public TimeSpan? Period { get; set; } = TimeSpan.FromDays(30);
    public int BatchSize { get; set; } = 10;
    public List<Guid> Persons { get; } = [];
    public int Remaining { get; set; }
    public List<(DateTimeOffset Cutoff, int Size)> Calls { get; } = [];

    public string Name => name;
    public Task<TimeSpan?> GetPeriodAsync(CancellationToken ct) => Task.FromResult(Period);
    public Task<int> GetBatchSizeAsync(CancellationToken ct) => Task.FromResult(BatchSize);
    public Task<IReadOnlyCollection<Guid>> GetCoveredPersonsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyCollection<Guid>>(Persons);

    public Task<int> PurgeBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        Calls.Add((cutoff, batchSize));
        var n = Math.Min(Remaining, batchSize);
        Remaining -= n;
        return Task.FromResult(n);
    }
}

internal sealed class FakeHolds : ILegalHoldReader, ILegalHoldScopeReader
{
    public bool NonPerson { get; set; }
    public HashSet<Guid> HeldPersons { get; } = [];
    public bool Throw { get; set; }

    public Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("secret detail") : Task.FromResult(HeldPersons.Contains(personId));

    public Task<bool> HasActiveNonPersonHoldAsync(CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("secret detail") : Task.FromResult(NonPerson);
}

internal sealed class FakeAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>Serves defaults except for explicitly set int values; only reads are supported.</summary>
internal sealed class FakeSettings : ISettingStore
{
    public Dictionary<string, int> Ints { get; } = [];

    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(Ints.TryGetValue(key.Name, out var v) ? (T)(object)v : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
