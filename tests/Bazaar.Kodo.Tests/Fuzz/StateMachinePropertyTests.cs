using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;
using Bazaar.Kodo.Tests.Sentinel;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Seeded property tests for the small state machines (doc 46 section 17.4): the client display derivation, the restart backoff, the
/// alert rate limiter and the slot switcher. Each test replays a random script from a fixed seed and checks an oracle after every
/// step, so a failure names the seed and the step. Time comes from fake clocks only, and nothing waits.
/// </summary>
public sealed class StateMachinePropertyTests
{
    private static readonly DateTimeOffset _origin = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    // --- Display derivation (doc 46 section 8.3) ---

    [Theory]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(503)]
    public void DisplayDerivationMatchesTheOracleForAnyHistory(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        KodoDisplayState[] known = [KodoDisplayState.Healthy, KodoDisplayState.Degraded, KodoDisplayState.Failing, KodoDisplayState.Recovering, KodoDisplayState.Frozen];

        for (int round = 0; round < 40; round++)
        {
            KodoUnobservedBudgets budgets = KodoUnobservedBudgets.Create(TimeSpan.FromMilliseconds(random.Next(1, 120_000)));
            KodoDisplayState? lastKnown = null;
            DateTimeOffset? lastEvent = null;
            DateTimeOffset clock = _origin;

            for (int step = 0; step < 60; step++)
            {
                string where = $"seed={seed} round={round} step={step}";
                clock = clock.AddMilliseconds(random.Next(0, 90_000));
                switch (random.Next(4))
                {
                    case 0:
                        // A snapshot or a state change: new state, fresh event.
                        lastKnown = known[random.Next(known.Length)];
                        lastEvent = clock;
                        break;
                    case 1:
                        // A heartbeat: freshness only.
                        lastEvent = clock;
                        break;
                    case 2:
                        // The client clock steps backwards.
                        clock = clock.AddMilliseconds(-random.Next(0, 200_000));
                        break;
                }

                KodoDisplayState shown = KodoUnobserved.Derive(lastKnown, clock, lastEvent, budgets);

                if (lastKnown is null || lastEvent is null)
                {
                    Assert.Equal(KodoDisplayState.Unobserved, shown);
                }
                else
                {
                    TimeSpan age = clock - lastEvent.Value;
                    bool silent = age > budgets.Heartbeat;
                    Assert.True(
                        silent ? shown == KodoDisplayState.Unobserved : shown == lastKnown,
                        $"{where}: age {age.TotalMilliseconds} ms, budget {budgets.Heartbeat.TotalMilliseconds} ms, last {lastKnown}, shown {shown}");
                }

                // Staleness is monotone: once silent past the budget, a later reading with no new event stays unobserved.
                if (lastKnown is not null && lastEvent is not null && shown == KodoDisplayState.Unobserved)
                {
                    Assert.Equal(
                        KodoDisplayState.Unobserved,
                        KodoUnobserved.Derive(lastKnown, clock.AddMilliseconds(random.Next(1, 100_000)), lastEvent, budgets));
                }
            }
        }
    }

    [Fact]
    public void DisplayDerivationBoundaryIsInclusiveAndUnobservedIsNeverAnInput()
    {
        KodoUnobservedBudgets budgets = KodoUnobservedBudgets.Defaults;

        Assert.Equal(KodoDisplayState.Failing, KodoUnobserved.Derive(KodoDisplayState.Failing, _origin + budgets.Heartbeat, _origin, budgets));
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(KodoDisplayState.Failing, _origin + budgets.Heartbeat + TimeSpan.FromTicks(1), _origin, budgets));
        Assert.Throws<ArgumentOutOfRangeException>(() => KodoUnobserved.Derive(KodoDisplayState.Unobserved, _origin, _origin, budgets));
        Assert.Throws<ArgumentOutOfRangeException>(() => KodoUnobservedBudgets.Create(TimeSpan.Zero));

        // Extreme instants never overflow the age.
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(KodoDisplayState.Healthy, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, budgets));
        Assert.Equal(KodoDisplayState.Healthy, KodoUnobserved.Derive(KodoDisplayState.Healthy, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, budgets));
    }

    [Fact]
    public void EveryServerStateHasOneWireNameThatReadsBackToItsDisplayValue()
    {
        foreach (KodoState state in Enum.GetValues<KodoState>())
        {
            string wire = KodoWireNames.State(state);

            Assert.True(KodoUnobserved.TryDisplayOf(wire, out KodoDisplayState display), wire);
            Assert.Equal(KodoWireNames.DisplayOf(state), display);
            Assert.Equal(wire, KodoWireNames.Display(display));
        }

        Assert.False(KodoUnobserved.TryDisplayOf("unobserved", out _));
        Random random = FuzzKit.Rng(511);
        string[] valid = [.. Enum.GetValues<KodoState>().Select(KodoWireNames.State)];
        for (int i = 0; i < 2000; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 12)).Select(_ => (char)random.Next(32, 127)).ToArray());
            Assert.Equal(valid.Contains(text, StringComparer.Ordinal), KodoUnobserved.TryDisplayOf(text, out _));
        }
    }

    // --- Restart backoff (doc 46 section 7.1) ---

    [Theory]
    [InlineData(521)]
    [InlineData(522)]
    [InlineData(523)]
    public void RestartBackoffFollowsTheScheduleAndResetsAfterAStableRun(int seed)
    {
        Random random = FuzzKit.Rng(seed);

        for (int round = 0; round < 40; round++)
        {
            int length = random.Next(1, 8);
            List<int> schedule = [];
            int value = random.Next(1, 5);
            for (int i = 0; i < length; i++)
            {
                schedule.Add(value);
                value += random.Next(0, 90);
            }

            TimeSpan stable = TimeSpan.FromSeconds(random.Next(1, 1200));
            RestartBackoff backoff = new(schedule, stable);
            int consecutive = 0;
            TimeSpan previousDelay = TimeSpan.Zero;
            DateTimeOffset at = _origin;

            for (int step = 1; step <= 50; step++)
            {
                TimeSpan ran = random.Next(4) switch
                {
                    0 => stable,
                    1 => stable - TimeSpan.FromTicks(1),
                    2 => stable + TimeSpan.FromSeconds(random.Next(0, 3000)),
                    _ => TimeSpan.FromSeconds(random.Next(0, (int)stable.TotalSeconds + 1)),
                };
                if (ran < TimeSpan.Zero)
                {
                    ran = TimeSpan.Zero;
                }

                at = at.AddSeconds(random.Next(0, 100));
                bool isStable = ran >= stable;
                consecutive = isStable ? 1 : consecutive + 1;
                string where = $"seed={seed} round={round} step={step}";

                RestartDecision decision = backoff.RecordExit(at, at + ran);

                TimeSpan expected = TimeSpan.FromSeconds(schedule[Math.Min(consecutive - 1, schedule.Count - 1)]);
                Assert.True(decision.Delay == expected, $"{where}: delay {decision.Delay}, expected {expected}");
                Assert.True(decision.Delay <= backoff.Cap, where);
                Assert.Equal(consecutive, decision.ConsecutiveExits);
                Assert.Equal(step, decision.RestartCount);
                Assert.Equal(step, backoff.RestartCount);
                if (!isStable)
                {
                    Assert.True(decision.Delay >= previousDelay, $"{where}: the delay shrank without a stable run");
                }

                previousDelay = decision.Delay;
            }
        }
    }

    // --- Alert rate limiter (doc 46 sections 7.4 and 16.1) ---

    [Theory]
    [InlineData(531)]
    [InlineData(532)]
    [InlineData(533)]
    [InlineData(534)]
    public void AlertRateLimiterAdmitsExactlyTheBudgetPerChannelAndWindow(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        int maxPer = random.Next(1, 7);
        AlertOptions options = new()
        {
            GroupWindow = TimeSpan.Zero,
            RateWindow = TimeSpan.FromMinutes(random.Next(1, 20)),
            MaxPerChannel = maxPer,
            MaxCriticalPerChannel = maxPer + random.Next(0, 15),
        };
        FakeTimeProvider clock = new(_origin);
        AlertRouter router = new(options, clock);
        Dictionary<(AlertChannelKind, bool), List<DateTimeOffset>> admitted = [];
        AlertSeverity[] severities = Enum.GetValues<AlertSeverity>();

        for (int step = 0; step < 400; step++)
        {
            string where = $"seed={seed} step={step}";
            clock.Advance(TimeSpan.FromSeconds(random.Next(0, 240)));
            DateTimeOffset now = clock.GetUtcNow();
            AlertSeverity severity = severities[random.Next(severities.Length)];
            bool critical = severity == AlertSeverity.Critical;
            KodoAlert alert = new($"host.p{step}:x", "host", severity, "disk_low", now, new Dictionary<string, string>());

            AlertRouteResult result = router.Route(alert);

            Assert.NotEqual(AlertDecision.Rejected, result.Decision);
            HashSet<AlertChannelKind> delivered = [.. result.Deliveries.Select(d => d.Channel)];
            HashSet<AlertChannelKind> limited = [.. result.Suppressions.Where(s => s.Reason == "rate_limited").Select(s => s.Channel)];
            Assert.Equal(options.Channels[severity].Count, delivered.Count + limited.Count);

            int budget = critical ? options.MaxCriticalPerChannel : options.MaxPerChannel;
            foreach (AlertChannelKind channel in options.Channels[severity])
            {
                if (!admitted.TryGetValue((channel, critical), out List<DateTimeOffset>? times))
                {
                    times = [];
                    admitted[(channel, critical)] = times;
                }

                int inWindow = times.Count(t => now - t < options.RateWindow);
                if (inWindow >= budget)
                {
                    Assert.True(limited.Contains(channel), $"{where}: {channel} had {inWindow} of {budget} and still delivered");
                }
                else
                {
                    Assert.True(delivered.Contains(channel), $"{where}: {channel} had {inWindow} of {budget} and was limited");
                    times.Add(now);
                }
            }
        }
    }

    // --- Slot switcher (doc 46 sections 6.3 and 7.8) ---

    private sealed class SlotWorld
    {
        public SlotWorld()
        {
            Rig = SentinelHarness.NewRig();
            Pointers[SentinelHarness.SlotA] = SentinelHarness.InstallSlot(Rig.Store, SentinelHarness.SlotA, "a");
            Pointers[SentinelHarness.SlotB] = SentinelHarness.InstallSlot(Rig.Store, SentinelHarness.SlotB, "b");
            Pristine[SentinelHarness.SlotA] = Copy(Rig.Store.Slots[SentinelHarness.SlotA]);
            Pristine[SentinelHarness.SlotB] = Copy(Rig.Store.Slots[SentinelHarness.SlotB]);
            Rig.Store.Root[RuntimeLayout.PointerFileName] = Pointers[SentinelHarness.SlotA];
        }

        public SentinelHarness.Rig Rig { get; }

        public Dictionary<string, byte[]> Pointers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>> Pristine { get; } = new(StringComparer.Ordinal);

        public static Dictionary<string, ReadOnlyMemory<byte>> Copy(Dictionary<string, ReadOnlyMemory<byte>> files) =>
            files.ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.ToArray(), StringComparer.Ordinal);

        /// <summary>An independent check of a pointer: the pointer, the manifest it names and every file of the slot.</summary>
        public bool Verifies(byte[]? pointerBytes)
        {
            if (pointerBytes is null || RuntimeManifestCodec.ParsePointer(pointerBytes) is not { } pointer || !RuntimeLayout.IsSlotId(pointer.SlotId))
            {
                return false;
            }

            if (!Rig.Store.Slots.TryGetValue(pointer.SlotId, out Dictionary<string, ReadOnlyMemory<byte>>? files)
                || !files.TryGetValue(SentinelLayout.ManifestFileName, out ReadOnlyMemory<byte> manifest))
            {
                return false;
            }

            RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
            byte[] envelope = manifest.ToArray();
            Dictionary<string, ReadOnlyMemory<byte>> payload = files
                .Where(pair => pair.Key != SentinelLayout.ManifestFileName)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new SlotPointerVerifier(checker).Verify(pointerBytes, envelope, RuntimeTestSigning.Now) == SlotPointerStatus.Verified
                && new RuntimeManifestVerifier(checker).Verify(envelope, payload, RuntimeTestSigning.Now).IsVerified;
        }

        public byte[]? Root(string name) => Rig.Store.Root.TryGetValue(name, out byte[]? bytes) ? [.. bytes] : null;
    }

    [Theory]
    [InlineData(541)]
    [InlineData(542)]
    [InlineData(543)]
    [InlineData(544)]
    [InlineData(545)]
    [InlineData(546)]
    public async Task SlotSwitcherNeverFollowsAnUnverifiedPointerWhateverTheScript(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        SlotWorld world = new();
        SentinelHarness.Rig rig = world.Rig;
        using SlotSwitcher switcher = rig.Switcher;
        await switcher.StartAsync(CancellationToken.None);
        int exits = 0;

        for (int step = 0; step < 30; step++)
        {
            string where = $"seed={seed} step={step}";
            string activeSlot = RuntimeManifestCodec.ParsePointer(world.Root(RuntimeLayout.PointerFileName) ?? [])?.SlotId ?? SentinelHarness.SlotA;
            string otherSlot = activeSlot == SentinelHarness.SlotA ? SentinelHarness.SlotB : SentinelHarness.SlotA;

            switch (random.Next(16))
            {
                case 0:
                case 1:
                    {
                        byte[]? pointerBefore = world.Root(RuntimeLayout.PointerFileName);
                        SentinelStatus status = await switcher.StartAsync(CancellationToken.None);
                        if (status.Phase == SentinelPhases.Running)
                        {
                            byte[]? pointerNow = world.Root(RuntimeLayout.PointerFileName);
                            Assert.True(world.Verifies(pointerNow), $"{where}: running on a pointer that does not verify");
                            Assert.Equal(RuntimeManifestCodec.ParsePointer(pointerNow!)!.SlotId, status.ActiveSlot);
                        }

                        if (status.Phase == SentinelPhases.Sealed)
                        {
                            Assert.Null(status.ActiveSlot);
                            Assert.False(world.Verifies(pointerBefore) && world.Verifies(world.Root(RuntimeLayout.PreviousPointerFileName)), $"{where}: sealed although a pointer verifies");
                        }

                        break;
                    }

                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                case 10:
                    await PromoteAsync(world, switcher, random, otherSlot, where);
                    break;
                case 11:
                    {
                        // Disk corruption: one bit of one file of a slot.
                        string slot = random.Next(2) == 0 ? SentinelHarness.SlotA : SentinelHarness.SlotB;
                        Dictionary<string, ReadOnlyMemory<byte>> files = rig.Store.Slots[slot];
                        string path = files.Keys.Order(StringComparer.Ordinal).ElementAt(random.Next(files.Count));
                        byte[] bytes = files[path].ToArray();
                        if (bytes.Length > 0)
                        {
                            bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                            files[path] = bytes;
                        }

                        break;
                    }

                case 12:
                case 13:
                    {
                        // Repair one slot from its pristine copy.
                        string slot = random.Next(2) == 0 ? SentinelHarness.SlotA : SentinelHarness.SlotB;
                        rig.Store.Slots[slot] = SlotWorld.Copy(world.Pristine[slot]);
                        if (random.Next(4) != 0)
                        {
                            // The operator reinstalls: the pointer is rewritten to a known good one for that slot.
                            rig.Store.Root[RuntimeLayout.PointerFileName] = world.Pointers[slot];
                        }

                        break;
                    }

                case 14:
                    {
                        // A root pointer file changed on disk behind the sentinel's back.
                        byte[] bytes = world.Root(RuntimeLayout.PointerFileName) ?? [0];
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                        rig.Store.Root[RuntimeLayout.PointerFileName] = bytes;
                        break;
                    }

                default:
                    {
                        DateTimeOffset started = _origin.AddMinutes(step);
                        RestartDecision decision = switcher.RecordKodoExit(started, started.AddSeconds(random.Next(0, 1200)));
                        exits++;
                        Assert.Equal(exits, decision.RestartCount);
                        Assert.Equal(exits, switcher.Status.RestartCount);
                        break;
                    }
            }

            Assert.Equal(exits, switcher.Status.RestartCount);
            if (switcher.Status.Phase == SentinelPhases.Sealed)
            {
                Assert.Null(switcher.Status.ActiveSlot);
            }
        }
    }

    private static async Task PromoteAsync(SlotWorld world, SlotSwitcher switcher, Random random, string target, string where)
    {
        SentinelHarness.Rig rig = world.Rig;
        byte[] candidate = world.Pointers[target];
        string kind;
        switch (random.Next(10))
        {
            case 0:
                kind = "tampered pointer";
                candidate = [.. candidate];
                candidate[random.Next(candidate.Length)] ^= (byte)(1 << random.Next(8));
                break;
            case 1:
                kind = "untrusted signer";
                candidate = RuntimeTestSigning.SignedPointer(target, rig.Store.Slots[target][SentinelLayout.ManifestFileName].ToArray(), RuntimeTestSigning.OtherKey);
                break;
            case 2:
                kind = "garbage";
                candidate = FuzzKit.RandomBytes(random, 60);
                break;
            case 3:
                kind = "already active";
                candidate = world.Root(RuntimeLayout.PointerFileName) ?? candidate;
                break;
            default:
                kind = "valid";
                break;
        }

        rig.Launcher.Outcomes.Clear();
        if (random.Next(3) == 0)
        {
            rig.Launcher.Outcomes.Enqueue(random.Next(2) == 0
                ? new KodoRunOutcome(KodoRunKind.Exited, random.Next(1, 200))
                : new KodoRunOutcome(KodoRunKind.FailedToStart, null));
        }

        byte[]? activeBefore = world.Root(RuntimeLayout.PointerFileName);
        byte[]? previousBefore = world.Root(RuntimeLayout.PreviousPointerFileName);
        bool candidateVerifies = world.Verifies(candidate);
        bool activeVerifies = world.Verifies(activeBefore);
        int logStart = rig.Log.Count;

        PromotionOutcome outcome = await switcher.PromoteAsync(candidate, CancellationToken.None);

        string detail = $"{where}: {kind}, outcome {outcome}";
        byte[]? activeAfter = world.Root(RuntimeLayout.PointerFileName);
        List<string> writes = [.. rig.Log.Skip(logStart).Where(entry => entry.StartsWith("replace:", StringComparison.Ordinal) || entry.StartsWith("delete:", StringComparison.Ordinal))];

        // A candidate that does not verify, or that cannot be kept beside a verified active pointer, is rejected and changes nothing.
        if (!candidateVerifies || !activeVerifies)
        {
            Assert.True(outcome == PromotionOutcome.Rejected, detail);
        }

        switch (outcome)
        {
            case PromotionOutcome.Rejected:
                Assert.Equal(activeBefore, activeAfter);
                Assert.Equal(previousBefore, world.Root(RuntimeLayout.PreviousPointerFileName));
                Assert.True(writes.Count == 0, $"{detail}: wrote {string.Join(",", writes)}");
                break;
            case PromotionOutcome.Promoted:
                Assert.Equal(candidate, activeAfter);
                Assert.True(world.Verifies(activeAfter), detail);
                Assert.Equal(activeBefore, world.Root(RuntimeLayout.PreviousPointerFileName));
                Assert.Null(world.Root(SentinelLayout.ProbationMarkerFileName));
                Assert.Equal(SentinelPhases.Running, switcher.Status.Phase);
                break;
            case PromotionOutcome.RolledBack:
                Assert.Equal(activeBefore, activeAfter);
                Assert.True(world.Verifies(activeAfter), $"{detail}: rolled back onto a pointer that does not verify");
                Assert.NotNull(switcher.Status.LastRollback);
                Assert.True(
                    rig.Log.Skip(logStart).ToList().FindIndex(entry => entry == "journal:" + SentinelJournalEvent.RollbackKind)
                        < rig.Log.Skip(logStart).ToList().FindLastIndex(entry => entry == "replace:" + RuntimeLayout.PointerFileName),
                    $"{detail}: the rollback was not journalled before the pointer moved");
                break;
            case PromotionOutcome.RollbackBlocked:
                Assert.Equal(SentinelPhases.Failing, switcher.Status.Phase);
                break;
        }

    }
}
