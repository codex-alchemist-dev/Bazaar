namespace Bazaar.Server.Jobs;

/// <summary>Runs one kind of background job. Handlers must be idempotent: a job whose lease expired is run again.</summary>
public interface IJobHandler
{
    /// <summary>The <c>job.kind</c> this handler serves. Each kind has exactly one handler.</summary>
    string Kind { get; }

    /// <summary>Runs the job. Throwing marks the attempt failed and schedules a retry (or fails the job for good).</summary>
    Task HandleAsync(JobRecord job, CancellationToken ct);
}
