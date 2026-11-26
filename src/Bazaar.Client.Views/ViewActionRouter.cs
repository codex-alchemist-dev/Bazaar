namespace Bazaar.Client.Views;

/// <summary>
/// The head's <see cref="IViewActions"/>, built from whatever the head can do (doc 41c, section 6). An action the head has
/// not wired fails closed: it throws, and the mapper reports it through <see cref="ViewContext.OnActionError"/>.
/// </summary>
public sealed partial class ViewActionRouter : IViewActions
{
    private readonly Func<string, Task>? _navigate;
    private readonly Func<string, Task>? _invoke;
    private readonly Func<string, Task>? _openPane;
    private readonly Func<string, string, Task>? _setSetting;

    public ViewActionRouter(
        Func<string, Task>? navigate = null,
        Func<string, Task>? invoke = null,
        Func<string, Task>? openPane = null,
        Func<string, string, Task>? setSetting = null)
    {
        _navigate = navigate;
        _invoke = invoke;
        _openPane = openPane;
        _setSetting = setSetting;
    }

    public Task NavigateAsync(string target) => Run(_navigate, "navigate", target);

    public Task InvokeAsync(string command) => Run(_invoke, "invoke", command);

    public Task OpenPaneAsync(string paneKind) => Run(_openPane, "openPane", paneKind);

    public Task SetSettingAsync(string key, string value) =>
        _setSetting is null ? NotWired("setSetting") : _setSetting(key, value);

    private static Task Run(Func<string, Task>? handler, string name, string argument) =>
        handler is null ? NotWired(name) : handler(argument);

    private static Task NotWired(string name) =>
        Task.FromException(new NotSupportedException($"The '{name}' view action is not wired in this head."));
}
