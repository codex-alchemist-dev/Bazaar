namespace Bazaar.Client.Interrupts;

/// <summary>
/// What the stack indicator shows for one agent: depth 0 to 2 and the top frame source, as text. Depth 0 shows nothing.
/// Colour is never the only signal (the depth and source are words).
/// </summary>
public sealed record StackIndicatorViewModel
{
    public StackIndicatorViewModel(int depth, InterruptSource? topSource, bool isOverLimit)
    {
        Depth = Math.Clamp(depth, 0, AgentInterruptStack.MaxDepth);
        TopSource = Depth == 0 ? null : topSource;
        IsOverLimit = Depth > 0 && isOverLimit;
    }

    public static StackIndicatorViewModel Empty { get; } = new(0, null, false);

    public int Depth { get; }

    public InterruptSource? TopSource { get; }

    public bool IsOverLimit { get; }

    public bool IsVisible => Depth > 0;

    /// <summary>The visible text, for example <c>Interrupt 2/2 · owner</c>; empty at depth 0.</summary>
    public string Text => !IsVisible
        ? string.Empty
        : $"Interrupt {Depth}/{AgentInterruptStack.MaxDepth} · {SourceLabel(TopSource)}" + (IsOverLimit ? " · stack over limit" : string.Empty);

    /// <summary>The screen reader name, for example <c>Interrupt stack depth 2, top frame owner</c>.</summary>
    public string AccessibleName => !IsVisible
        ? string.Empty
        : $"Interrupt stack depth {Depth}, top frame {SourceLabel(TopSource)}" + (IsOverLimit ? ", stack over limit" : string.Empty);

    public static StackIndicatorViewModel From(AgentInterruptStack? stack) =>
        stack is null ? Empty : new StackIndicatorViewModel(stack.Depth, stack.Top?.Source, stack.IsOverLimit);

    /// <summary>The human wording of a source.</summary>
    public static string SourceLabel(InterruptSource? source) => source switch
    {
        InterruptSource.Owner => "owner",
        InterruptSource.SystemBuild => "system build",
        InterruptSource.SystemSecurity => "system security",
        _ => "unknown",
    };
}
