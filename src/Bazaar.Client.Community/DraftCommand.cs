using System.Windows.Input;

namespace Bazaar.Client.Community;

/// <summary>
/// Command for the draft-review buttons. It starts an async action and is enabled by a predicate over the item's state.
/// The item calls <see cref="RaiseCanExecuteChanged"/> after every state change. <see cref="Last"/> holds the running or
/// finished action, so a caller (or a test) can await it.
/// </summary>
public sealed class DraftCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    /// <summary>The most recent action started by <see cref="Execute"/>; completed when none has run.</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            Last = execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
