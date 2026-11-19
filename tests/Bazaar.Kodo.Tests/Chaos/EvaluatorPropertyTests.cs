using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Chaos;

/// <summary>
/// Property tests for the state machine (doc 46 section 17.4): no transition outside section 8.2, no healthy with an
/// open critical rule, hysteresis respected, under random observations and random (including backwards) clocks.
/// Fixed seeds, no waits.
/// </summary>
public sealed class EvaluatorPropertyTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<(KodoState From, KodoState To)> _allowed =
    [
        (KodoState.Healthy, KodoState.Degraded),
        (KodoState.Healthy, KodoState.Failing),
        (KodoState.Degraded, KodoState.Failing),
        (KodoState.Degraded, KodoState.Healthy),
        (KodoState.Failing, KodoState.Recovering),
        (KodoState.Recovering, KodoState.Healthy),
        (KodoState.Recovering, KodoState.Failing),
    ];

#pragma warning disable CA5394 // fixed seeds, not security material
    private static KodoObservation RandomObservation(Random random, bool mostlyGreen) => new()
    {
        CriticalRed = random.Next(mostlyGreen ? 12 : 6) == 0,
        CriticalUnknown = random.Next(mostlyGreen ? 12 : 6) == 0,
        IntegrityOrContractFailure = random.Next(30) == 0,
        CrashLoop = random.Next(40) == 0,
        NonCriticalRedBeyondGrace = random.Next(mostlyGreen ? 20 : 8) == 0 ? random.Next(1, 4) : 0,
        NonCriticalAmberBeyondGrace = random.Next(mostlyGreen ? 12 : 6) == 0,
        NonCriticalWithinGrace = random.Next(mostlyGreen ? 10 : 5) == 0,
        SloWarningBurn = random.Next(15) == 0,
        SloBreach = random.Next(25) == 0,
        Recovery = (RecoveryStatus)random.Next(3),
        IncidentActive = random.Next(20) == 0,
    };
#pragma warning restore CA5394

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void RandomRunsOnlyUseTheDocumentedTransitions(int seed, bool mostlyGreen)
    {
#pragma warning disable CA5394
        Random random = new(seed);
#pragma warning restore CA5394
        KodoEvaluatorSettings settings = new();
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        DateTimeOffset now = _start;

        for (int step = 0; step < 20_000; step++)
        {
            now = now.AddSeconds(random.Next(1, 120));
            KodoObservation observation = RandomObservation(random, mostlyGreen);
            KodoEvaluation result = KodoEvaluator.Evaluate(memory, observation, now, settings);

            KodoState from = memory.Underlying;
            KodoState to = result.Underlying;
            Assert.NotEqual(KodoState.Frozen, to);
            if (from != to)
            {
                // The one documented exception is the flap rule (section 8.2): it forces degraded.
                bool flapForced = result.Flapping && to == KodoState.Degraded;
                Assert.True(_allowed.Contains((from, to)) || flapForced, $"step {step}: {from} -> {to} is not in section 8.2");
            }

            // No healthy with an open critical rule or an open degrading rule, ever.
            if (to == KodoState.Healthy)
            {
                Assert.False(observation.CriticalRed || observation.IntegrityOrContractFailure || observation.CrashLoop,
                    $"step {step}: healthy with an open critical rule");
            }

            // Hysteresis: becoming healthy needs a full stability window of green samples.
            if (from != KodoState.Healthy && to == KodoState.Healthy)
            {
                Assert.True(observation.AllRulesGreen, $"step {step}: became healthy with a rule that is not green");
                Assert.NotNull(result.Next.GreenSince);
                Assert.True(now - result.Next.GreenSince!.Value >= settings.StabilityWindow, $"step {step}: healthy before the stability window");
            }

            // Frozen is shown only while incident mode is active, and the underlying state still moves.
            Assert.Equal(observation.IncidentActive, result.State == KodoState.Frozen);

            // Failing is never left directly for healthy (section 8.2).
            Assert.False(from == KodoState.Failing && to == KodoState.Healthy);

            memory = result.Next;
        }
    }

    [Fact]
    public void ClockRunningBackwardsNeverMakesTheStateHealthierOrThrows()
    {
#pragma warning disable CA5394
        Random random = new(99);
#pragma warning restore CA5394
        KodoEvaluatorSettings settings = new();
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], false);
        DateTimeOffset now = _start;

        for (int step = 0; step < 10_000; step++)
        {
            // Skew in both directions, up to a day each way.
            now = now.AddSeconds(random.Next(-86_400, 86_400));
            KodoObservation observation = RandomObservation(random, mostlyGreen: true);
            KodoEvaluation result = KodoEvaluator.Evaluate(memory, observation, now, settings);

            if (result.Underlying == KodoState.Healthy && memory.Underlying != KodoState.Healthy)
            {
                Assert.True(observation.AllRulesGreen);
            }

            Assert.True(result.Next.Transitions.Count <= 10_000);
            memory = result.Next;
        }
    }

    [Fact]
    public void FlappingNeverHidesACriticalRed()
    {
        KodoEvaluatorSettings settings = new() { FlapLimit = 2 };
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        DateTimeOffset now = _start;
        KodoObservation red = new() { CriticalRed = true };
        KodoObservation green = new();

        for (int step = 0; step < 200; step++)
        {
            now = now.AddMinutes(step % 7 == 0 ? 6 : 1);
            KodoEvaluation result = KodoEvaluator.Evaluate(memory, step % 2 == 0 ? red : green, now, settings);
            if (step % 2 == 0)
            {
                Assert.True(result.Underlying is KodoState.Failing or KodoState.Recovering, $"step {step}: critical red hidden as {result.Underlying}");
            }

            memory = result.Next;
        }
    }
}
