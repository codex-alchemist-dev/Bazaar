using Bazaar.Conduit.Security;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.Security;

public sealed class ConduitAccessGuardTests
{
    private const string _listener = "lan";
    private const string _client = "203.0.113.7";

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

    private static ConduitAccessGuard NewGuard(FakeTimeProvider clock, ConduitAccessOptions? options = null) =>
        new(options ?? ConduitAccessOptions.Shipped, clock);

    /// <summary>Options with a small rate and a one-failure threshold, so lockout tests need one call per step.</summary>
    private static ConduitAccessOptions Lockout(int threshold = 1, int baseMinutes = 1, int maxMinutes = 4) => ConduitAccessOptions.Shipped with
    {
        FailureThreshold = threshold,
        BaseLockout = TimeSpan.FromMinutes(baseMinutes),
        MaxLockout = TimeSpan.FromMinutes(maxMinutes),
    };

    [Fact]
    public void Constructor_RejectsInvalidOptions()
    {
        var invalid = ConduitAccessOptions.Shipped with { RequestLimit = 0 };

        Assert.Throws<ArgumentException>(() => new ConduitAccessGuard(invalid));
    }

    [Fact]
    public void Constructor_RejectsOptionsLooserThanFloor()
    {
        var loose = ConduitAccessOptions.Shipped with { FailureThreshold = ConduitAccessFloor.MaxFailureThreshold + 5 };

        Assert.Throws<ArgumentException>(() => new ConduitAccessGuard(loose));
    }

    [Fact]
    public void TryAdmitRequest_AdmitsRequestsUnderTheLimit()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 3 });

        for (var i = 0; i < 3; i++)
        {
            Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        }
    }

    [Fact]
    public void TryAdmitRequest_DeniesOverLimitWithRetryAfterUntilOldestRequestLeavesWindow()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 3, RequestWindow = TimeSpan.FromSeconds(10) });

        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);

        // The first request was at t=0 and leaves the 10 s window at t=10; now is t=3, so 7 s remain.
        clock.Advance(TimeSpan.FromSeconds(1));
        var denied = guard.TryAdmitRequest(_listener, _client);

        Assert.False(denied.IsAllowed);
        Assert.Equal(ConduitAccessVerdict.Denied, denied.Verdict);
        Assert.Equal(ConduitDenyReason.RateLimited, denied.Reason);
        Assert.Equal(TimeSpan.FromSeconds(7), denied.RetryAfter);
    }

    [Fact]
    public void TryAdmitRequest_AdmitsAgainWhenRetryAfterHasElapsed()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 1, RequestWindow = TimeSpan.FromSeconds(10) });

        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        clock.Advance(TimeSpan.FromSeconds(3));
        var denied = guard.TryAdmitRequest(_listener, _client);
        Assert.Equal(TimeSpan.FromSeconds(7), denied.RetryAfter);

        clock.Advance(denied.RetryAfter);

        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
    }

    [Fact]
    public void TryAdmitRequest_DeniedRequestsAreNotCounted()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 1, RequestWindow = TimeSpan.FromSeconds(10) });

        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        for (var i = 0; i < 20; i++)
        {
            Assert.False(guard.TryAdmitRequest(_listener, _client).IsAllowed);
        }

        // Only the first request was admitted, so the window still ends at t=10 and not later.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
    }

    [Fact]
    public void LockoutStartsAfterThresholdFailures()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 3, baseMinutes: 1, maxMinutes: 4));

        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);
        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);

        var locked = guard.RecordAuthResult(_listener, _client, success: false);

        Assert.False(locked.IsAllowed);
        Assert.Equal(ConduitDenyReason.LockedOut, locked.Reason);
        Assert.Equal(TimeSpan.FromMinutes(1), locked.RetryAfter);
        Assert.Equal(ConduitDenyReason.LockedOut, guard.TryAdmitRequest(_listener, _client).Reason);
    }

    [Fact]
    public void LockoutEndsWhenItsDurationHasPassed()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 2, baseMinutes: 1, maxMinutes: 4));
        guard.RecordAuthResult(_listener, _client, success: false);
        guard.RecordAuthResult(_listener, _client, success: false);

        clock.Advance(TimeSpan.FromSeconds(59));
        var stillLocked = guard.TryAdmitRequest(_listener, _client);
        Assert.Equal(ConduitDenyReason.LockedOut, stillLocked.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), stillLocked.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(guard.TryAdmitRequest(_listener, _client).IsAllowed);
    }

    [Fact]
    public void LockoutBacksOffExponentiallyAndCapsAtMax()
    {
        var clock = NewClock();
        // Threshold 1 means every failure locks. Base 1 min, cap 4 min: expect 1, 2, 4, then 4 again.
        var guard = NewGuard(clock, Lockout(threshold: 1, baseMinutes: 1, maxMinutes: 4));
        var expected = new[] { 1, 2, 4, 4, 4 };

        foreach (var minutes in expected)
        {
            var decision = guard.RecordAuthResult(_listener, _client, success: false);

            Assert.Equal(ConduitDenyReason.LockedOut, decision.Reason);
            Assert.Equal(TimeSpan.FromMinutes(minutes), decision.RetryAfter);
            clock.Advance(TimeSpan.FromMinutes(minutes));
        }
    }

    [Fact]
    public void BackoffLevelIsForgottenAfterMaxLockoutOfQuiet()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1, baseMinutes: 1, maxMinutes: 4));

        guard.RecordAuthResult(_listener, _client, success: false);
        clock.Advance(TimeSpan.FromMinutes(1));
        guard.RecordAuthResult(_listener, _client, success: false);
        clock.Advance(TimeSpan.FromMinutes(2));

        // Quiet for the full maximum lockout after the last lock ended, so the next lock starts at the base again.
        clock.Advance(TimeSpan.FromMinutes(4));
        var next = guard.RecordAuthResult(_listener, _client, success: false);

        Assert.Equal(TimeSpan.FromMinutes(1), next.RetryAfter);
    }

    [Fact]
    public void SuccessClearsFailureCount()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 3, baseMinutes: 1, maxMinutes: 4));

        guard.RecordAuthResult(_listener, _client, success: false);
        guard.RecordAuthResult(_listener, _client, success: false);
        Assert.True(guard.RecordAuthResult(_listener, _client, success: true).IsAllowed);

        // Two more failures are below the threshold of three because the earlier two were cleared.
        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);
        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);
        Assert.Equal(ConduitDenyReason.LockedOut, guard.RecordAuthResult(_listener, _client, success: false).Reason);
    }

    [Fact]
    public void SuccessResetsBackoffLevel()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1, baseMinutes: 1, maxMinutes: 4));

        guard.RecordAuthResult(_listener, _client, success: false);
        clock.Advance(TimeSpan.FromMinutes(1));
        guard.RecordAuthResult(_listener, _client, success: false);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(guard.RecordAuthResult(_listener, _client, success: true).IsAllowed);

        var next = guard.RecordAuthResult(_listener, _client, success: false);

        Assert.Equal(TimeSpan.FromMinutes(1), next.RetryAfter);
    }

    [Fact]
    public void FailuresOutsideTheFailureWindowDoNotCount()
    {
        var clock = NewClock();
        var options = Lockout(threshold: 3, baseMinutes: 1, maxMinutes: 4) with { FailureWindow = TimeSpan.FromMinutes(15) };
        var guard = NewGuard(clock, options);

        guard.RecordAuthResult(_listener, _client, success: false);
        guard.RecordAuthResult(_listener, _client, success: false);
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);
        Assert.True(guard.RecordAuthResult(_listener, _client, success: false).IsAllowed);
    }

    [Fact]
    public void ResultsDuringALockoutDoNotExtendIt()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1, baseMinutes: 1, maxMinutes: 4));
        guard.RecordAuthResult(_listener, _client, success: false);

        clock.Advance(TimeSpan.FromSeconds(30));
        var during = guard.RecordAuthResult(_listener, _client, success: false);

        Assert.Equal(ConduitDenyReason.LockedOut, during.Reason);
        Assert.Equal(TimeSpan.FromSeconds(30), during.RetryAfter);
    }

    [Fact]
    public void Lockout_IsIsolatedPerListener()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1));
        guard.RecordAuthResult("lan", _client, success: false);

        Assert.Equal(ConduitDenyReason.LockedOut, guard.TryAdmitRequest("lan", _client).Reason);
        Assert.True(guard.TryAdmitRequest("overlay", _client).IsAllowed);
    }

    [Fact]
    public void Lockout_IsIsolatedPerClient()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1));
        guard.RecordAuthResult(_listener, "198.51.100.1", success: false);

        Assert.Equal(ConduitDenyReason.LockedOut, guard.TryAdmitRequest(_listener, "198.51.100.1").Reason);
        Assert.True(guard.TryAdmitRequest(_listener, "198.51.100.2").IsAllowed);
    }

    [Fact]
    public void RateLimit_IsIsolatedPerListenerAndPerClient()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 1, RequestWindow = TimeSpan.FromSeconds(10) });

        Assert.True(guard.TryAdmitRequest("lan", _client).IsAllowed);
        Assert.False(guard.TryAdmitRequest("lan", _client).IsAllowed);
        Assert.True(guard.TryAdmitRequest("overlay", _client).IsAllowed);
        Assert.True(guard.TryAdmitRequest("lan", "198.51.100.9").IsAllowed);
    }

    [Fact]
    public void Concurrent_TryAdmitRequest_NeverExceedsLimit()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 20, RequestWindow = TimeSpan.FromSeconds(1) });

        // The clock is frozen, so nothing leaves the window and exactly 20 admissions are possible.
        var admitted = 0;
        Parallel.For(0, 64, _ =>
        {
            for (var i = 0; i < 100; i++)
            {
                if (guard.TryAdmitRequest(_listener, _client).IsAllowed)
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        });

        Assert.Equal(20, admitted);
    }

    [Fact]
    public void Concurrent_RecordAuthResult_LocksOutOnceAtThreshold()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 5, baseMinutes: 1, maxMinutes: 4));

        // Fifty concurrent failures: the first four are allowed, and the fifth and the rest are denied.
        var denied = 0;
        Parallel.For(0, 50, _ =>
        {
            if (!guard.RecordAuthResult(_listener, _client, success: false).IsAllowed)
            {
                Interlocked.Increment(ref denied);
            }
        });

        Assert.Equal(46, denied);
        Assert.Equal(TimeSpan.FromMinutes(1), guard.TryAdmitRequest(_listener, _client).RetryAfter);
    }

    [Fact]
    public void Prune_RemovesIdleClientsAndKeepsActiveOnes()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, ConduitAccessOptions.Shipped with { RequestLimit = 5, RequestWindow = TimeSpan.FromSeconds(1) });
        for (var i = 0; i < 1000; i++)
        {
            guard.TryAdmitRequest(_listener, $"198.51.100.{i}");
        }

        Assert.Equal(1000, guard.TrackedClientCount);

        clock.Advance(ConduitAccessOptions.Shipped.FailureWindow + TimeSpan.FromSeconds(1));
        // This call runs the prune: every earlier client is idle, so only the new client remains.
        guard.TryAdmitRequest(_listener, _client);

        Assert.Equal(1, guard.TrackedClientCount);
    }

    [Fact]
    public void Prune_KeepsClientsThatAreStillLockedOut()
    {
        var clock = NewClock();
        var guard = NewGuard(clock, Lockout(threshold: 1, baseMinutes: 30, maxMinutes: 30));
        guard.RecordAuthResult(_listener, _client, success: false);

        // The prune runs at 15 minutes, while the 30-minute lockout is still active, so the locked client must stay.
        clock.Advance(TimeSpan.FromMinutes(16));
        guard.TryAdmitRequest(_listener, "198.51.100.200");

        Assert.Equal(2, guard.TrackedClientCount);
    }

    [Fact]
    public void ConstructorRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new ConduitAccessGuard(null!));
    }

    [Fact]
    public void Calls_RejectEmptyKeys()
    {
        var guard = NewGuard(NewClock());

        Assert.Throws<ArgumentException>(() => guard.TryAdmitRequest("", _client));
        Assert.Throws<ArgumentException>(() => guard.RecordAuthResult(_listener, "", success: false));
    }
}
