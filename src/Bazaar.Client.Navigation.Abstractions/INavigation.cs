namespace Bazaar.Client.Navigation.Abstractions;

/// <summary>
/// Moves the user to a pane or another destination (doc 41c section 6: <c>navigate</c> and <c>openPane</c>). A target is
/// <c>scheme:value</c>, such as <c>gc:general</c>; a target with no scheme is a pane kind. The head decides how a pane shows.
/// </summary>
public interface IPaneNavigator
{
    /// <summary>Shows the pane of <paramref name="paneKind"/>. Throws <see cref="NavigationException"/> when no module provides it.</summary>
    Task OpenPaneAsync(string paneKind, CancellationToken cancellationToken = default);

    /// <summary>Goes to <paramref name="target"/>. Throws <see cref="NavigationException"/> for an unknown scheme or a refused target.</summary>
    Task NavigateAsync(string target, CancellationToken cancellationToken = default);
}

/// <summary>What the head does to show a pane. The head implements it; modules never see it.</summary>
public interface IPaneSurface
{
    /// <summary>True when a pane of this kind is registered and can be shown.</summary>
    bool CanShow(string paneKind);

    Task ShowAsync(string paneKind, CancellationToken cancellationToken);
}

/// <summary>Runs a named command a module declared (doc 41c section 6: <c>invoke</c>).</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs the handler registered for <paramref name="commandId"/>. Throws <see cref="NavigationException"/> when none is
    /// registered or the command is currently disabled. The handler runs the same approval flow as the control it mirrors;
    /// the runner adds no permission of its own.
    /// </summary>
    Task RunAsync(string commandId, CancellationToken cancellationToken = default);
}

/// <summary>A navigation or command request that cannot be carried out.</summary>
public sealed class NavigationException : Exception
{
    public NavigationException(string message)
        : base(message)
    {
    }

    public NavigationException()
    {
    }

    public NavigationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
