using System.Windows.Input;

namespace Bazaar.Client.GeneticMemory.Avalonia;

/// <summary>Button command that is disabled by a predicate the view model re-raises after each change.</summary>
public sealed class ReviewCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
