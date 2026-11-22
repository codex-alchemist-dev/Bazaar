using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Api;

/// <summary>Shared fixtures for the read API and stream tests.</summary>
internal static class KodoApiTestData
{
    /// <summary>A fixed UTC instant so every encoding and age in the tests is exact.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An evaluation with the given displayed and underlying states. Only the states are read by the API.</summary>
    public static KodoEvaluation Evaluation(KodoState state, KodoState? underlying = null)
    {
        KodoState under = underlying ?? state;
        return new KodoEvaluation(
            state,
            under,
            Flapping: false,
            RaiseFlapAlert: false,
            Next: new KodoEvaluatorMemory(under, null, [], false));
    }

    /// <summary>A 32-byte journal head whose hex is easy to read: 0x00 .. 0x1f.</summary>
    public static byte[] JournalHead()
    {
        byte[] head = new byte[32];
        for (int i = 0; i < head.Length; i++)
        {
            head[i] = (byte)i;
        }

        return head;
    }

    /// <summary>The lowercase hex of <see cref="JournalHead"/>.</summary>
    public const string JournalHeadHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    /// <summary>A small snapshot at the given revision, for stream tests.</summary>
    public static KodoStatusSnapshotDto Snapshot(long revision, DateTimeOffset? generatedAt = null) =>
        KodoStatusSnapshotBuilder.Build(
            Evaluation(KodoState.Healthy),
            new KodoObservation(),
            score: 100,
            components: [],
            revision: revision,
            generatedAt: generatedAt ?? Now,
            journalHead: null);

    /// <summary>A component input that is green since <see cref="Now"/>.</summary>
    public static KodoComponentInput Green(string id) => new(id, ProbeStatus.Green, null, Now);
}
