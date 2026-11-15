namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>
/// Backup-owner quorum for owner recovery (doc 48 section 7). Count and threshold are operator settings with a shipped
/// default; the threshold floor and the time-lock bounds are security invariants, not settings: an operator may
/// tighten inside them, never loosen below them. Build instances with <see cref="Create"/>.
/// </summary>
public sealed record QuorumPolicy
{
    /// <summary>The shortest time lock accepted. A floor, not a setting.</summary>
    public static readonly TimeSpan MinimumRecoveryTimeLock = TimeSpan.FromHours(24);

    /// <summary>The longest time lock accepted. A ceiling so a recovery can never be locked out indefinitely.</summary>
    public static readonly TimeSpan MaximumRecoveryTimeLock = TimeSpan.FromDays(30);

    private QuorumPolicy(int backupOwnerCount, int threshold, TimeSpan recoveryTimeLock)
    {
        BackupOwnerCount = backupOwnerCount;
        Threshold = threshold;
        RecoveryTimeLock = recoveryTimeLock;
    }

    /// <summary>
    /// Shipped default: 3 backup owners, threshold 2, 72 hour lock. Two of three survives the loss of one key or one
    /// person yet needs two people to act; the lock is the longer, more protective option (doc 48 section 7) while
    /// leaving a real owner several days to notice the alerts and cancel.
    /// </summary>
    public static QuorumPolicy Default { get; } = new(3, 2, TimeSpan.FromHours(72));

    /// <summary>Number of backup owners (n).</summary>
    public int BackupOwnerCount { get; }

    /// <summary>Distinct hardware-key assertions needed to complete (k).</summary>
    public int Threshold { get; }

    /// <summary>How long a recovery waits, with owner devices able to cancel, before it can complete.</summary>
    public TimeSpan RecoveryTimeLock { get; }

    /// <summary>
    /// Validates and creates a policy. A single backup owner needs threshold 1; with more than one, threshold 1 is
    /// refused and the threshold must be between 2 and the count.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its allowed range.</exception>
    public static QuorumPolicy Create(int backupOwnerCount, int threshold, TimeSpan recoveryTimeLock)
    {
        if (backupOwnerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(backupOwnerCount), "At least one backup owner is required.");
        }

        if (backupOwnerCount == 1)
        {
            if (threshold != 1)
            {
                throw new ArgumentOutOfRangeException(nameof(threshold), "With one backup owner the threshold must be 1.");
            }
        }
        else if (threshold < 2 || threshold > backupOwnerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                $"With {backupOwnerCount} backup owners the threshold must be between 2 and {backupOwnerCount}; 1 is refused.");
        }

        if (recoveryTimeLock < MinimumRecoveryTimeLock || recoveryTimeLock > MaximumRecoveryTimeLock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryTimeLock),
                $"The recovery time lock must be between {MinimumRecoveryTimeLock} and {MaximumRecoveryTimeLock}.");
        }

        return new QuorumPolicy(backupOwnerCount, threshold, recoveryTimeLock);
    }
}
