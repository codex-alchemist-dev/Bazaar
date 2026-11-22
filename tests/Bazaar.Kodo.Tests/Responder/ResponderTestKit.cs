using System.Security.Cryptography;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Responder;

/// <summary>Fakes and fixtures for the responder tests. The signing key is generated at run time.</summary>
internal static class ResponderTestKit
{
    // One RSA instance is shared by tests that run in parallel. The Windows CNG handle is not safe for concurrent use
    // (ObjectDisposedException on the key handle), so every use of the key goes through one lock and the public key is exported once.
    private static readonly Lock _keyLock = new();
    private static readonly Lazy<RSA> _key = new(() => RSA.Create(2048));
    private static readonly Lazy<byte[]> _publicKey = new(() =>
    {
        lock (_keyLock)
        {
            return _key.Value.ExportSubjectPublicKeyInfo();
        }
    });

    /// <summary>The SubjectPublicKeyInfo of the test signing key, exported once.</summary>
    public static byte[] PublicKey => _publicKey.Value;

    public static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    public static RunbookCatalogue Catalogue(params RunbookDefinition[] definitions)
    {
        RunbookCatalogue catalogue = new(new RsaPkcs1Sha256Verifier(), PublicKey);
        foreach (RunbookDefinition definition in definitions)
        {
            Assert.Null(catalogue.TryAdd(definition, Sign(definition)));
        }

        return catalogue;
    }

    public static byte[] Sign(RunbookDefinition definition)
    {
        byte[] bytes = RunbookCanonical.Bytes(definition);
        lock (_keyLock)
        {
            return _key.Value.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }

    public static RunbookStep Step(string id, RunbookStepKind kind, string operation, int attempts = 1, bool approval = false, TimeSpan? waitFor = null) =>
        new(id, kind, operation, new Dictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromSeconds(30), attempts, approval, waitFor);

    /// <summary>precheck, then do_a and do_b actions, then a verify; rollback undoes a.</summary>
    public static RunbookDefinition Demo(RunbookRisk risk = RunbookRisk.Medium, RunbookApprovalTier tier = RunbookApprovalTier.Approval, int version = 1) =>
        new(
            "demo.fix",
            version,
            "Demo fix",
            risk,
            tier,
            KodoControls.RunbookRun,
            TimeSpan.FromMinutes(1),
            [Step("pre", RunbookStepKind.Check, "demo.pre")],
            [
                Step("do_a", RunbookStepKind.Action, "demo.do_a"),
                Step("do_b", RunbookStepKind.Action, "demo.do_b"),
                Step("verify", RunbookStepKind.Verify, "demo.verify", attempts: 3),
            ],
            [Step("undo_a", RunbookStepKind.Action, "demo.undo_a")]);

    public static RunbookStartRequest Start(string runId = "run1", bool tainted = false, KodoState state = KodoState.Failing, RunbookDefinition? definition = null)
    {
        RunbookDefinition d = definition ?? Demo();
        return new RunbookStartRequest(
            runId, d.Id, d.Version, null, "owner", new HashSet<string>(StringComparer.Ordinal) { KodoControls.RunbookRun },
            new Dictionary<string, string>(StringComparer.Ordinal), tainted, state);
    }
}

internal sealed class FakeRunner : IRunbookActionRunner
{
    private readonly Dictionary<string, Queue<RunbookStepResult>> _script = new(StringComparer.Ordinal);

    public List<RunbookStepRequest> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public string? ThrowOn { get; set; }

    public void Script(string stepId, params RunbookStepOutcome[] outcomes) =>
        _script[stepId] = new Queue<RunbookStepResult>(outcomes.Select(o => new RunbookStepResult(o, o.ToString().ToLowerInvariant())));

    public ValueTask<RunbookStepResult> RunAsync(RunbookStepRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (Throw is not null && (ThrowOn is null || ThrowOn == request.StepId))
        {
            throw Throw;
        }

        if (_script.TryGetValue(request.StepId, out Queue<RunbookStepResult>? queue) && queue.Count > 0)
        {
            return ValueTask.FromResult(queue.Dequeue());
        }

        return ValueTask.FromResult(new RunbookStepResult(RunbookStepOutcome.Succeeded, "ok"));
    }

    public ValueTask<RunbookPreview> PreviewAsync(RunbookStepRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RunbookPreview(request.StepId, $"would.run.{request.Operation}"));
}

internal sealed class MemoryJournal : IRunbookJournal
{
    public List<RunbookJournalEntry> Entries { get; } = [];

    public Func<RunbookJournalEntry, bool>? FailWhen { get; set; }

    public IEnumerable<string> Kinds => Entries.Select(e => e.Kind + (e.StepId is null ? string.Empty : ":" + e.StepId));

    public ValueTask AppendAsync(RunbookJournalEntry entry, CancellationToken cancellationToken)
    {
        if (FailWhen?.Invoke(entry) == true)
        {
            throw new IOException("journal down");
        }

        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Keeps state as JSON bytes, so every save and load goes through the codec like a real store would.</summary>
internal sealed class MemoryStore : IRunbookStateStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public bool FailSaves { get; set; }

    public int? FailAfterSaves { get; set; }

    private int _saves;

    public ValueTask<RunbookRunState?> LoadAsync(string runId, CancellationToken cancellationToken)
    {
        if (!Files.TryGetValue(runId, out byte[]? bytes))
        {
            return ValueTask.FromResult<RunbookRunState?>(null);
        }

        if (!RunbookRunStateCodec.TryParse(bytes, out RunbookRunState? state))
        {
            throw new IOException("state unreadable");
        }

        return ValueTask.FromResult(state);
    }

    public ValueTask SaveAsync(RunbookRunState state, CancellationToken cancellationToken)
    {
        if (FailSaves || (FailAfterSaves is { } limit && ++_saves > limit))
        {
            throw new IOException("disk full");
        }

        Files[state.RunId] = RunbookRunStateCodec.ToUtf8Bytes(state);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedGate : IApprovalGate
{
    public Func<ApprovalRequest, ApprovalDecision> Decide { get; set; } = _ => ApprovalDecision.Pending;

    public bool StepUp { get; set; } = true;

    public string? DigestOverride { get; set; }

    public bool Throws { get; set; }

    public List<ApprovalRequest> Requests { get; } = [];

    public ValueTask<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Throws)
        {
            throw new InvalidOperationException("gate down");
        }

        return ValueTask.FromResult(new ApprovalResult(Decide(request), "ap-" + request.StepId.Replace(':', '-'), DigestOverride ?? request.RequestDigest, StepUp));
    }
}

internal sealed class Harness
{
    public Harness(RunbookDefinition? definition = null, RunbookOptions? options = null)
    {
        Definition = definition ?? ResponderTestKit.Demo();
        Clock = ResponderTestKit.Clock();
        Catalogue = ResponderTestKit.Catalogue(Definition);
        Options = options ?? new RunbookOptions();
    }

    public RunbookDefinition Definition { get; }

    public FakeTimeProvider Clock { get; }

    public RunbookCatalogue Catalogue { get; }

    public RunbookOptions Options { get; }

    public FakeRunner Runner { get; } = new();

    public MemoryJournal Journal { get; } = new();

    public MemoryStore Store { get; } = new();

    public ScriptedGate Gate { get; } = new();

    public RunbookExecutor Executor() => new(Catalogue, Runner, Journal, Store, Gate, Options, Clock);

    public void ApproveAll() => Gate.Decide = _ => ApprovalDecision.Approved;
}
