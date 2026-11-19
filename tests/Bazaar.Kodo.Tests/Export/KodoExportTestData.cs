using System.Security.Cryptography;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Tests.Api;

namespace Bazaar.Kodo.Tests.Export;

/// <summary>Shared fixtures for the export tests. Keys are generated at run time, never written into the source.</summary>
internal static class KodoExportTestData
{
    /// <summary>The fixed instant of every snapshot and webhook in these tests.</summary>
    public static readonly DateTimeOffset Now = KodoApiTestData.Now;

    /// <summary>A snapshot with the given states, score and components, at revision 1.</summary>
    public static KodoStatusSnapshotDto Snapshot(
        KodoState state,
        KodoState? underlying = null,
        int score = 100,
        params KodoComponentInput[] components)
    {
        return KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(state, underlying),
            new KodoObservation(),
            score,
            components,
            revision: 1,
            generatedAt: Now,
            journalHead: null);
    }

    /// <summary>A component that is green since <see cref="Now"/>.</summary>
    public static KodoComponentInput Component(string id, ProbeStatus status, string? cause = null) =>
        new(id, status, status == ProbeStatus.Green ? null : cause ?? "test_cause", Now);

    /// <summary>A fresh signing key with a random 32-byte secret.</summary>
    public static KodoWebhookSigningKey NewKey(string keyId) =>
        new(keyId, RandomNumberGenerator.GetBytes(KodoWebhookSigningKey.MinimumSecretLength));

    /// <summary>A jitter source that always returns the same value, for exact schedules.</summary>
    internal sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
