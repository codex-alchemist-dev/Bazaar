namespace Bazaar.Client.Approvals;

/// <summary>
/// Operator-configurable gate details the docs leave open (doc 29 Approvals). Every value can only tighten: a larger
/// lifetime is clamped to <see cref="MaxBiometricGraceSeconds"/>, a smaller one to <see cref="MinBiometricGraceSeconds"/>.
/// </summary>
public sealed record ApprovalGateOptions
{
    /// <summary>Hard ceiling on how long a biometric success stays usable. The configured value never loosens past it.</summary>
    public const int MaxBiometricGraceSeconds = 30;

    /// <summary>Floor, so a zero or negative setting cannot make every approval impossible by accident.</summary>
    public const int MinBiometricGraceSeconds = 1;

    /// <summary>Seconds a biometric success is valid for signing one approval. Default 30, never more than 30.</summary>
    public int BiometricGraceSeconds { get; init; } = MaxBiometricGraceSeconds;

    /// <summary>When true, a deny also needs a fresh biometric. Default false: doc 29 requires biometric for approve only.</summary>
    public bool RequireBiometricForDeny { get; init; }

    /// <summary>The grace period after clamping.</summary>
    public TimeSpan EffectiveBiometricGrace =>
        TimeSpan.FromSeconds(Math.Clamp(BiometricGraceSeconds, MinBiometricGraceSeconds, MaxBiometricGraceSeconds));
}
