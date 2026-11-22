using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Decides whether a membership motion is a bigger change, from the install setting <c>council.bigger_change_classes</c>
/// (docs/design/10-council-and-voting.md, Membership and role votes). The shipped default is every membership motion.
/// Pure: no storage, clock or model calls.
/// </summary>
public sealed class HireMotionClassifier
{
    /// <summary>The setting key. Its list values are the class keys that <see cref="KeyFor"/> returns.</summary>
    public const string SettingKey = "council.bigger_change_classes";

    private readonly HashSet<MembershipMotionKind> _biggerChange;

    /// <summary>Creates the classifier from the configured class keys.</summary>
    /// <param name="biggerChangeClasses">The configured keys. Null uses the shipped default (every membership motion).
    /// An empty list makes every motion ordinary.</param>
    /// <exception cref="ArgumentException">A key is not a membership motion class.</exception>
    public HireMotionClassifier(IEnumerable<string>? biggerChangeClasses = null)
    {
        if (biggerChangeClasses is null)
        {
            _biggerChange = [.. Enum.GetValues<MembershipMotionKind>()];
            return;
        }

        _biggerChange = [];
        foreach (var key in biggerChangeClasses)
        {
            _biggerChange.Add(KindFor(key));
        }
    }

    /// <summary>The shipped default list: the key of every membership motion kind.</summary>
    public static IReadOnlyList<string> DefaultClasses { get; } = Enum.GetValues<MembershipMotionKind>().Select(KeyFor).ToArray();

    /// <summary>Returns the motion class for a membership motion of the given kind.</summary>
    public MotionClass Classify(MembershipMotionKind kind) =>
        _biggerChange.Contains(kind) ? MotionClass.BiggerChange : MotionClass.Ordinary;

    /// <summary>The setting key for a membership motion kind (the same string as its JSON name).</summary>
    public static string KeyFor(MembershipMotionKind kind) => kind switch
    {
        MembershipMotionKind.Hire => "hire",
        MembershipMotionKind.Promote => "promote",
        MembershipMotionKind.Retire => "retire",
        MembershipMotionKind.RoleChange => "role_change",
        MembershipMotionKind.ConstitutionChange => "constitution_change",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown membership motion kind."),
    };

    private static MembershipMotionKind KindFor(string key)
    {
        foreach (var kind in Enum.GetValues<MembershipMotionKind>())
        {
            if (string.Equals(KeyFor(kind), key, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        throw new ArgumentException($"'{key}' is not a membership motion class for {SettingKey}.", nameof(key));
    }
}
