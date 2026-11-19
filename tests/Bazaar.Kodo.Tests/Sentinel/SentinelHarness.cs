using System.Security.Cryptography;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Sentinel;

/// <summary>
/// Fake file store for the sentinel. Every write and journal or status call goes into one shared log, so tests can assert order.
/// No real files are touched.
/// </summary>
internal sealed class FakeSentinelFileStore(List<string> log) : ISentinelFileStore
{
    /// <summary>Root files by name.</summary>
    public Dictionary<string, byte[]> Root { get; } = new(StringComparer.Ordinal);

    /// <summary>Slot files by slot id, then by slot-relative path.</summary>
    public Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>> Slots { get; } = new(StringComparer.Ordinal);

    /// <summary>Root names whose reads throw, as an unreadable file would.</summary>
    public HashSet<string> UnreadableRoots { get; } = new(StringComparer.Ordinal);

    /// <summary>Root names whose replace throws, as a failed write would.</summary>
    public HashSet<string> FailingReplaces { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public byte[]? ReadRootFile(string name)
    {
        if (UnreadableRoots.Contains(name))
        {
            throw new IOException($"'{name}' is unreadable in this test.");
        }

        return Root.TryGetValue(name, out byte[]? bytes) ? bytes.ToArray() : null;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ReadSlot(string slotId) =>
        Slots.TryGetValue(slotId, out Dictionary<string, ReadOnlyMemory<byte>>? files)
            ? files
            : new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

    /// <inheritdoc />
    public void ReplaceRootFile(string name, ReadOnlyMemory<byte> bytes)
    {
        if (FailingReplaces.Contains(name))
        {
            throw new IOException($"'{name}' cannot be replaced in this test.");
        }

        log.Add("replace:" + name);
        Root[name] = bytes.ToArray();
    }

    /// <inheritdoc />
    public void DeleteRootFile(string name)
    {
        log.Add("delete:" + name);
        Root.Remove(name);
    }
}

/// <summary>Fake launcher. Each probation run returns the next queued outcome, and survives when the queue is empty.</summary>
internal sealed class FakeKodoProcessLauncher(List<string> log) : IKodoProcessLauncher
{
    /// <summary>Outcomes to return, in order.</summary>
    public Queue<KodoRunOutcome> Outcomes { get; } = new();

    /// <summary>Every probation run, as slot id and window.</summary>
    public List<(string Slot, TimeSpan Window)> Calls { get; } = [];

    /// <summary>Runs before the outcome is returned, so a test can change files while the slot is on probation.</summary>
    public Action? BeforeOutcome { get; set; }

    /// <summary>When set, the run throws this, as a launcher that cannot start the process would.</summary>
    public Exception? Throw { get; set; }

    /// <inheritdoc />
    public Task<KodoRunOutcome> RunProbationAsync(string slotId, TimeSpan window, CancellationToken cancellationToken)
    {
        log.Add("probation:" + slotId);
        Calls.Add((slotId, window));
        BeforeOutcome?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null)
        {
            throw Throw;
        }

        KodoRunOutcome outcome = Outcomes.Count > 0 ? Outcomes.Dequeue() : new KodoRunOutcome(KodoRunKind.SurvivedWindow, null);
        return Task.FromResult(outcome);
    }
}

/// <summary>Fake journal. It records events, and throws <see cref="IOException"/> when <see cref="Fail"/> is set.</summary>
internal sealed class FakeSentinelJournal(List<string> log) : ISentinelJournal
{
    /// <summary>Events that were appended.</summary>
    public List<SentinelJournalEvent> Events { get; } = [];

    /// <summary>When true, every append throws.</summary>
    public bool Fail { get; set; }

    /// <inheritdoc />
    public void Append(SentinelJournalEvent sentinelEvent)
    {
        log.Add("journal:" + sentinelEvent.Kind);
        if (Fail)
        {
            throw new IOException("The journal is unavailable in this test.");
        }

        Events.Add(sentinelEvent);
    }
}

/// <summary>Fake status file. It keeps every write, so tests can read the phases that were announced.</summary>
internal sealed class FakeStatusFile(List<string> log) : ISentinelStatusFile
{
    /// <summary>Every whole-file write, as UTF-8 text.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>When set, a write whose text matches throws <see cref="IOException"/> and is not recorded.</summary>
    public Predicate<string>? FailOn { get; set; }

    /// <inheritdoc />
    public void Replace(ReadOnlyMemory<byte> bytes)
    {
        string text = System.Text.Encoding.UTF8.GetString(bytes.Span);
        if (FailOn?.Invoke(text) == true)
        {
            throw new IOException("The status file cannot be written in this test.");
        }

        log.Add("status");
        Writes.Add(text);
    }
}

/// <summary>Builds signed slots, pointers and switchers for the sentinel tests. Keys are generated at runtime.</summary>
internal static class SentinelHarness
{
    /// <summary>The active slot in most scenarios.</summary>
    public const string SlotA = RuntimeLayout.SlotA;

    /// <summary>The second slot in most scenarios.</summary>
    public const string SlotB = RuntimeLayout.SlotB;

    /// <summary>The window the shipped options use.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The contents of a slot, varied by tag so two slots never share a manifest.</summary>
    public static Dictionary<string, string> Contents(string tag) => new(StringComparer.Ordinal)
    {
        ["kodo.dll"] = "binary-" + tag,
        ["probes/catalogue.json"] = "{}",
        ["runbooks/restart.md"] = "# restart " + tag,
    };

    /// <summary>
    /// Puts a signed slot into the store, with its manifest envelope as <see cref="SentinelLayout.ManifestFileName"/>, and returns the
    /// pointer that selects it. <paramref name="key"/> signs both the manifest and the pointer (default: the trusted key).
    /// </summary>
    public static byte[] InstallSlot(FakeSentinelFileStore store, string slotId, string tag, RSA? key = null)
    {
        Dictionary<string, string> contents = Contents(tag);
        byte[] envelope = RuntimeTestSigning.SignedEnvelope(RuntimeTestSigning.ManifestFor(slotId, contents), key);
        Dictionary<string, ReadOnlyMemory<byte>> files = RuntimeTestSigning.ToSlotFiles(contents);
        files[SentinelLayout.ManifestFileName] = envelope;
        store.Slots[slotId] = files;
        return RuntimeTestSigning.SignedPointer(slotId, envelope, key);
    }

    /// <summary>A switcher over the fakes, with the trusted key, the shipped backoff and a 10 minute window.</summary>
    public static SlotSwitcher Switcher(
        FakeSentinelFileStore store,
        FakeKodoProcessLauncher launcher,
        FakeSentinelJournal journal,
        FakeStatusFile statusFile,
        FakeTimeProvider clock) =>
        new(
            store,
            new RuntimeSignatureChecker(RuntimeTestSigning.TrustRoot()),
            launcher,
            journal,
            statusFile,
            new RestartBackoff(RestartBackoff.DefaultScheduleSeconds, RestartBackoff.DefaultStablePeriod),
            clock,
            SlotSwitcherOptions.Default);

    /// <summary>The fakes and a clock at the fixed test time, bundled for one scenario.</summary>
    public sealed record Rig(
        List<string> Log,
        FakeSentinelFileStore Store,
        FakeKodoProcessLauncher Launcher,
        FakeSentinelJournal Journal,
        FakeStatusFile StatusFile,
        FakeTimeProvider Clock,
        SlotSwitcher Switcher);

    /// <summary>A new rig with empty fakes. Install slots and pointers into <see cref="Rig.Store"/> before starting.</summary>
    public static Rig NewRig()
    {
        List<string> log = [];
        FakeSentinelFileStore store = new(log);
        FakeKodoProcessLauncher launcher = new(log);
        FakeSentinelJournal journal = new(log);
        FakeStatusFile statusFile = new(log);
        FakeTimeProvider clock = new(RuntimeTestSigning.Now);
        return new Rig(log, store, launcher, journal, statusFile, clock, Switcher(store, launcher, journal, statusFile, clock));
    }
}
