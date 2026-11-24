using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Testing;

/// <summary>Seeded property tests for the state machine (doc 46 section 17.4). No test waits on wall time.</summary>
public sealed class EvaluatorPropertyTests
{
    private static readonly (KodoState From, KodoState To)[] _allowed =
    [
        (KodoState.Healthy, KodoState.Degraded),
        (KodoState.Healthy, KodoState.Failing),
        (KodoState.Degraded, KodoState.Failing),
        (KodoState.Degraded, KodoState.Healthy),
        (KodoState.Failing, KodoState.Recovering),
        (KodoState.Recovering, KodoState.Healthy),
        (KodoState.Recovering, KodoState.Failing),
    ];

    private static KodoObservation RandomObservation(Random random) => new()
    {
        CriticalRed = random.Next(10) == 0,
        CriticalUnknown = random.Next(10) == 0,
        IntegrityOrContractFailure = random.Next(30) == 0,
        CrashLoop = random.Next(30) == 0,
        NonCriticalRedBeyondGrace = random.Next(8) == 0 ? random.Next(1, 3) : 0,
        NonCriticalAmberBeyondGrace = random.Next(8) == 0,
        NonCriticalWithinGrace = random.Next(8) == 0,
        SloWarningBurn = random.Next(15) == 0,
        SloBreach = random.Next(15) == 0,
        Recovery = (RecoveryStatus)random.Next(3),
        IncidentActive = random.Next(12) == 0,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomSamplesNeverBreakTheStateMachineInvariants(int seed)
    {
        Random random = new(seed);
        KodoEvaluatorSettings settings = new();
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        DateTimeOffset now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 4000; i++)
        {
            now = now.AddSeconds(random.Next(1, 120));
            KodoObservation observation = RandomObservation(random);
            KodoState before = memory.Underlying;

            KodoEvaluation evaluation = KodoEvaluator.Evaluate(memory, observation, now, settings);
            memory = evaluation.Next;

            Assert.NotEqual(KodoState.Frozen, evaluation.Underlying);
            Assert.Equal(observation.IncidentActive ? KodoState.Frozen : evaluation.Underlying, evaluation.State);

            if (evaluation.Underlying != before)
            {
                // Known deviation (finding, doc 46 17.5): flap detection holds a would-be healthy at degraded, so a flapping
                // recovering state can drop to degraded, which is not a row of section 8.2. Only that pair is allowed, and
                // only while flapping.
                bool flapHold = evaluation.Flapping && before == KodoState.Recovering && evaluation.Underlying == KodoState.Degraded;
                if (!flapHold)
                {
                    Assert.Contains((before, evaluation.Underlying), _allowed);
                }
            }

            // No healthy while a critical rule is open, and never healthy off a sample that is not all green.
            if (evaluation.Underlying == KodoState.Healthy && before != KodoState.Healthy)
            {
                Assert.True(observation.AllRulesGreen, $"Seed {seed} sample {i}: became healthy off a non-green sample.");
            }

            if (observation.CriticalRed || observation.IntegrityOrContractFailure || observation.CrashLoop)
            {
                Assert.NotEqual(KodoState.Healthy, evaluation.Underlying);
            }
        }
    }
}
