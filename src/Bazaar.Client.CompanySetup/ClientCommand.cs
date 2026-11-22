using System.Windows.Input;

namespace Bazaar.Client.CompanySetup;

/// <summary>A command that runs an async action and is disabled while it runs, so a double press cannot send two requests.</summary>
internal sealed class ClientCommand(Func<Task> run, Func<bool> canRun) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && canRun();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Changed();
        try
        {
            await run();
        }
        finally
        {
            _running = false;
            Changed();
        }
    }

    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
