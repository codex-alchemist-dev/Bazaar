using System.Windows.Input;

namespace Bazaar.Client.Approvals.Avalonia;

/// <summary>Button command for the card. It is disabled by a predicate over the card's state, which the view model re-raises after each change.</summary>
public sealed class ApprovalCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
