using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.RateLimits;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Http;

namespace Bazaar.Server.Setup.Http.Tests.Support;

/// <summary>In-memory bootstrap code store. Keeps a SHA-256 digest of the code, never the code, and retires it on the first match.</summary>
public sealed class InMemoryBootstrapCodeStore : IBootstrapCodeStore
{
    private readonly Lock _lock = new();
    private byte[]? _digest;

    public Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            _digest = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        }

        return Task.FromResult(code);
    }

    public Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedCode));
        lock (_lock)
        {
            // A missing code compares against zeros, so the check takes the same path either way.
            var expected = _digest ?? new byte[presented.Length];
            var matches = CryptographicOperations.FixedTimeEquals(presented, expected) && _digest is not null;
            if (matches)
            {
                _digest = null;
            }

            return Task.FromResult(matches);
        }
    }

    public Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _digest = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>In-memory apply result store. The first saved result for a hash is kept.</summary>
public sealed class InMemoryApplyResultStore : IApplyResultStore
{
    private readonly ConcurrentDictionary<string, SetupApplyResult> _results = new(StringComparer.Ordinal);

    public int Count => _results.Count;

    public Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_results.TryGetValue(planHash, out var result) ? result : null);

    public Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default)
    {
        _results.TryAdd(planHash, result);
        return Task.CompletedTask;
    }
}

public sealed class FakeOwnerDirectory : ISetupOwnerDirectory
{
    public bool Exists { get; set; }

    public Task<bool> OwnerExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Exists);
}

public sealed class FakeProgressSource : ISetupProgressSource
{
    public SetupProgressSnapshot Snapshot { get; set; } = new(
        [new SetupStepProgress("database.schema", SetupStepState.Done)],
        [new SetupHeldApproval("install.updatePolicy", "needs approval")]);

    public Task<SetupProgressSnapshot> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
}

public sealed class FakeVerificationSource(SetupVerificationSummary? summary) : ISetupVerificationSource
{
    public Task<SetupVerificationSummary?> GetLastAsync(CancellationToken cancellationToken = default) => Task.FromResult(summary);
}

/// <summary>Allows calls until <see cref="Deny"/> is set, then refuses every call with a retry time.</summary>
public sealed class FakeRateLimiter : IRateLimiter
{
    public bool Deny { get; set; }

    public int Calls { get; private set; }

    public RateLimitDecision TryAcquire(RateLimitPolicy policy, string scopeKey)
    {
        Calls++;
        return Deny
            ? new RateLimitDecision(false, 0, TimeSpan.FromSeconds(7))
            : new RateLimitDecision(true, 29, TimeSpan.Zero);
    }
}

/// <summary>Maps bearer tokens to principals. An unknown token fails, as the real authenticator does.</summary>
public sealed class FakeAuthenticator : ISetupCallerAuthenticator
{
    public Dictionary<string, SetupPrincipal> Tokens { get; } = new(StringComparer.Ordinal);

    public ValueTask<SetupPrincipal?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Tokens.TryGetValue(bearerToken, out var principal) ? principal : null);
}

/// <summary>The transport rule. Plaintext from off the machine is refused unless a test allows it.</summary>
public sealed class FakeTransportPolicy : ISetupTransportPolicy
{
    public bool AllowPlaintextOverlay { get; set; }
}

/// <summary>Keeps every settings audit row in memory, in order.</summary>
public sealed class FakeSettingAudit : ISetupSettingAudit
{
    private readonly List<(string Key, string ActorId, string? OldValueJson, string NewValueJson)> _entries = [];

    public IReadOnlyList<(string Key, string ActorId, string? OldValueJson, string NewValueJson)> Entries => _entries;

    public Task AppendInstallChangeAsync(string key, string actorId, string? oldValueJson, string newValueJson, CancellationToken cancellationToken = default)
    {
        _entries.Add((key, actorId, oldValueJson, newValueJson));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A three-key settings catalog: the keys the fixture plans use. Values are checked by type and range only, and a status bind
/// other than <c>loopback</c> is held, like the real registry.
/// </summary>
public sealed class FakeSettingCatalog : ISetupSettingCatalog
{
    private readonly Dictionary<string, ISetupSettingDefinition> _byName = new(StringComparer.Ordinal)
    {
        ["host.status.bind"] = new Definition("host.status.bind", typeof(string), CheckBind, held: value => value.GetString() != "loopback"),
        ["host.status.port"] = new Definition("host.status.port", typeof(int), value => CheckRange(value, 0, 65535)),
        ["host.shutdown_timeout_seconds"] = new Definition("host.shutdown_timeout_seconds", typeof(int), value => CheckRange(value, 5, 120)),
    };

    public IReadOnlyCollection<ISetupSettingDefinition> Definitions => _byName.Values;

    public bool TryGet(string name, out ISetupSettingDefinition? definition) => _byName.TryGetValue(name, out definition);

    private static string? CheckBind(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? null : "must be a string.";

    private static string? CheckRange(JsonElement value, int min, int max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
            ? null
            : $"must be a whole number from {min} to {max}.";

    private sealed class Definition(string name, Type valueType, Func<JsonElement, string?> check, Func<JsonElement, bool>? held = null) : ISetupSettingDefinition
    {
        public string Name => name;

        public bool IsInstallScope => true;

        public Type ValueType => valueType;

        public string? CheckValue(JsonElement value) => check(value);

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) =>
            held is not null && held(proposed)
                ? new SetupSettingEvaluation(false, "needs owner approval.", proposed.GetRawText())
                : new SetupSettingEvaluation(true, null, proposed.GetRawText());
    }
}

/// <summary>Keeps every audit row in memory, in order.</summary>
public sealed class FakeAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>When set, every append throws. Used to prove a failed audit write is not answered as success.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (Fail)
        {
            throw new IOException("audit store unavailable");
        }

        lock (_entries)
        {
            _entries.Add(e);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Counts how often the lock is taken. The lease does nothing.</summary>
public sealed class FakeInstanceLock : ISetupInstanceLock
{
    public int Acquisitions { get; private set; }

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Acquisitions++;
        return Task.FromResult<IAsyncDisposable>(new Lease());
    }

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A step that counts its applies. It can succeed or report a failure, so the tests can drive the applier's outcomes.</summary>
public sealed class CountingStep(string id, int order) : ISetupStep
{
    public string Id { get; } = id;

    public int Order { get; } = order;

    /// <summary>Covers every plan section by default, so a plan applies. A test narrows it to drive the unapplied-sections hold.</summary>
    public IReadOnlyCollection<string> Covers { get; set; } = [.. SetupPlanSections.All.Select(section => section.Id)];

    public bool Fail { get; set; }

    public int ApplyCalls { get; private set; }

    public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        return Task.FromResult(Fail ? new SetupStepResult(false, "refused") : SetupStepResult.Ok);
    }
}
