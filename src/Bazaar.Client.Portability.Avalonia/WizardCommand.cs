using System.Windows.Input;

namespace Bazaar.Client.Portability.Avalonia;

/// <summary>
/// Command for the wizard buttons. It runs a transition and is disabled by a predicate over the current state.
/// The view model calls <see cref="RaiseCanExecuteChanged"/> after every state change.
/// </summary>
public sealed class WizardCommand(Action<object?> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
