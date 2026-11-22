namespace Bazaar.Server.Retention;

/// <summary>
/// The default <see cref="IRetentionPlanner"/>. Legal hold wins over everything: a held record is never due for removal.
/// A category with no row, no period, or no removal method is kept. Crypto-erase categories are never "purge due" here,
/// because their removal is the erasure coordinator's job and produces a signed proof.
/// </summary>
public sealed class RetentionPlanner(IRetentionPolicy policy) : IRetentionPlanner
{
    public RetentionDisposition Decide(string category, DateTimeOffset createdAt, DateTimeOffset? softDeletedAt, DateTimeOffset now, bool underLegalHold)
    {
        var row = policy.Find(category);
        if (row is null || row.Period is null || row.Method != ErasureMethod.SoftDeleteThenPurge)
            return RetentionDisposition.Keep;

        var expired = now - createdAt >= row.Period.Value;
        if (!expired)
            return RetentionDisposition.Keep;
        if (underLegalHold)
            return RetentionDisposition.Held;
        if (softDeletedAt is null)
            return RetentionDisposition.SoftDeleteDue;
        return now - softDeletedAt.Value >= row.SoftDeleteWindow ? RetentionDisposition.PurgeDue : RetentionDisposition.Keep;
    }
}
