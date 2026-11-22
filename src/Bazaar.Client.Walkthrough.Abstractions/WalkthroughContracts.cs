namespace Bazaar.Client.Walkthrough;

/// <summary>The steps of the first-run walkthrough, in the order they are shown (doc 45 section 2.7).</summary>
public enum WalkthroughStep
{
    /// <summary>What the company is and what the next steps are.</summary>
    Welcome = 0,

    /// <summary>Create the company, if the server has none yet.</summary>
    CreateCompany = 1,

    /// <summary>Meet the company's first agent.</summary>
    MeetAgent = 2,

    /// <summary>Give the first agent a first task.</summary>
    FirstTask = 3,

    /// <summary>The closing step. Says how to run the walkthrough again.</summary>
    Done = 4,
}

/// <summary>What the person has already done, read from the company server. Each flag is a fact, never a guess.</summary>
/// <param name="CompanyCreated">The install has a company.</param>
/// <param name="FirstAgentName">The first agent's display name, or null when no agent exists yet.</param>
/// <param name="FirstTaskGiven">The person has given any agent a task (a Kanban card or a request in a group chat).</param>
public sealed record WalkthroughFacts(bool CompanyCreated, string? FirstAgentName, bool FirstTaskGiven)
{
    /// <summary>Nothing is known to be done.</summary>
    public static WalkthroughFacts None { get; } = new(false, null, false);
}

/// <summary>
/// Where the walkthrough stands for this person. It is a human preference saved with <c>prefs.set</c> (doc 27), so it follows the
/// account to every device. An empty store must never stop the app working.
/// </summary>
/// <param name="Dismissed">The person skipped the walkthrough. It stays hidden until they replay it.</param>
/// <param name="Finished">The person reached the last step.</param>
/// <param name="Current">The step to show next.</param>
/// <param name="Completed">Steps whose real action was seen done.</param>
public sealed record WalkthroughProgress(bool Dismissed, bool Finished, WalkthroughStep Current, IReadOnlyList<WalkthroughStep> Completed)
{
    /// <summary>A person who has not started.</summary>
    public static WalkthroughProgress Fresh { get; } = new(false, false, WalkthroughStep.Welcome, []);
}

/// <summary>Keeps the walkthrough's progress. The client head supplies one backed by <c>prefs.set</c>.</summary>
public interface IWalkthroughProgressStore
{
    /// <summary>The saved progress, or null when none is saved.</summary>
    Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the saved progress.</summary>
    Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default);
}

/// <summary>Reads what the person has already done from the server, so a step can show as done and a returning person skips ahead.</summary>
public interface IWalkthroughFactsSource
{
    /// <summary>Reads the facts. Throws when the server cannot be reached; the walkthrough then keeps what it last knew.</summary>
    Task<WalkthroughFacts> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Takes the person to the real screen for a step. The walkthrough shows no copy of those screens: each step's action opens the
/// create-company screen, the agent's profile or the task composer in the shell.
/// </summary>
public interface IWalkthroughActions
{
    /// <summary>Opens the screen where the step's action happens. Returns false when it could not be opened.</summary>
    Task<bool> OpenAsync(WalkthroughStep which, CancellationToken cancellationToken = default);
}
