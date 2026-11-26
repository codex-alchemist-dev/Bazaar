namespace Bazaar.Client.Commands;

/// <summary>
/// Where a pane module says what one of its commands does. A head hands the router to modules (through the pane module
/// context) and sends every chosen command id to it, so no pane is named in the shell.
/// </summary>
public interface ICommandRouter
{
    /// <summary>
    /// Registers what <paramref name="commandId"/> does. Throws <see cref="InvalidOperationException"/> when the command
    /// already has a handler: one command has one owner. Disposing the returned handle removes the handler.
    /// </summary>
    IDisposable Register(string commandId, Action handler);
}
