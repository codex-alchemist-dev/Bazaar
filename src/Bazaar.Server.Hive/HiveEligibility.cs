using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Why a lesson may not be copied. <see cref="None"/> means eligible (a grant is still needed).</summary>
public enum HiveRefusal
{
    None = 0,
    InvalidInput,
    ExcludedClass,
    SubjectTagged,
    TrustTooLow,
    KindNotAllowed,
    SameProject,
}

/// <summary>
/// The fixed floors of doc 08 section 6 and doc 25. Settings only narrow <c>hive.allowed_kinds</c>; the excluded classes
/// (preferences, reputation, private memory, GC text, Vault text, credentials, subject-tagged items, T0 and T1) are not settings.
/// </summary>
internal static class HiveEligibility
{
    public static HiveRefusal Check(LessonRef lesson, ProjectId target, HiveOptions options)
    {
        if (lesson.Project.IsEmpty || target.IsEmpty
            || string.IsNullOrWhiteSpace(lesson.MemoryId)
            || string.IsNullOrWhiteSpace(lesson.Title)
            || string.IsNullOrWhiteSpace(lesson.Body)
            || string.IsNullOrWhiteSpace(lesson.OriginKind))
        {
            return HiveRefusal.InvalidInput;
        }

        if (lesson.SourceClass != HiveSourceClass.Lesson)
            return HiveRefusal.ExcludedClass;
        if (lesson.SubjectTagged)
            return HiveRefusal.SubjectTagged;
        if (lesson.Trust < HiveTrust.T2)
            return HiveRefusal.TrustTooLow;
        if (!HiveKinds.All.Contains(lesson.Kind) || !options.AllowedKinds.Contains(lesson.Kind))
            return HiveRefusal.KindNotAllowed;
        if (lesson.Project.Equals(target))
            return HiveRefusal.SameProject;
        return HiveRefusal.None;
    }
}
