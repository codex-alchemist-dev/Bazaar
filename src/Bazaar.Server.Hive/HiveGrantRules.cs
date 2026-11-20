using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Checks shared by the grant service and the wire handler, so both refuse the same inputs with the same message.</summary>
internal static class HiveGrantRules
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the pair is two different projects and the kind may cross.</summary>
    public static void ValidatePair(ProjectId from, ProjectId to, string kind)
    {
        if (from.IsEmpty || to.IsEmpty || from.Equals(to))
            throw new ArgumentException("A grant needs two different projects.");
        if (!HiveKinds.All.Contains(kind))
            throw new ArgumentException($"Kind '{kind}' cannot cross projects.", nameof(kind));
    }
}
