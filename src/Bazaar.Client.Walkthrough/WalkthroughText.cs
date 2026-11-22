namespace Bazaar.Client.Walkthrough;

/// <summary>
/// The walkthrough's words, in one place so a translation can replace them (doc 45 section 2.2: translated strings on every screen).
/// Plain sentences, no timers and no content that appears only through animation.
/// </summary>
public static class WalkthroughText
{
    public static string Title(WalkthroughStep step) => step switch
    {
        WalkthroughStep.Welcome => "Welcome to your company",
        WalkthroughStep.CreateCompany => "Create your company",
        WalkthroughStep.MeetAgent => "Meet your first agent",
        WalkthroughStep.FirstTask => "Give a first task",
        _ => "You are set",
    };

    public static string Body(WalkthroughStep step, WalkthroughFacts facts) => step switch
    {
        WalkthroughStep.Welcome =>
            "This company is yours. Agents work here as colleagues, and you talk with them in group chats. Three short steps get you started, and you can skip any of them.",
        WalkthroughStep.CreateCompany => facts.CompanyCreated
            ? "Your company exists. Nothing more to do here."
            : "Name your company and yourself. The server checks everything before it changes anything.",
        WalkthroughStep.MeetAgent => facts.FirstAgentName is { Length: > 0 } name
            ? $"{name} is your first agent. Open the profile to see who they are and what they can do."
            : "Your first agent appears once the company exists. Open the profile to see who they are and what they can do.",
        WalkthroughStep.FirstTask => facts.FirstTaskGiven
            ? "You have given a first task. Follow it in the group chat or on the Kanban board."
            : "Ask your agent for something small, for example a summary of the repository. They reply in the group chat.",
        _ => "That is the walkthrough. Run it again any time from the command palette. Replaying never changes your settings.",
    };

    /// <summary>The label of the button that opens a step's real screen, or null when the step has none.</summary>
    public static string? ActionLabel(WalkthroughStep step) => step switch
    {
        WalkthroughStep.CreateCompany => "Open create company",
        WalkthroughStep.MeetAgent => "Open agent profile",
        WalkthroughStep.FirstTask => "Open task composer",
        _ => null,
    };

    public static string Progress(WalkthroughStep step) => $"Step {(int)step + 1} of {StepCount}";

    public static int StepCount => Enum.GetValues<WalkthroughStep>().Length;
}
