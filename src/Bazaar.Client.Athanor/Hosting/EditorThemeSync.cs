using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>Where the editor learns the app theme (doc 57 section 3, doc 41 section 4.15). The head feeds it from its theme service.</summary>
public interface IEditorThemeSource
{
    /// <summary>The theme in force.</summary>
    ThemeDefinition Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed. May be raised on any thread.</summary>
    event EventHandler? Changed;
}

/// <summary>Adapts the client's <see cref="ClientThemeService"/> to <see cref="IEditorThemeSource"/>.</summary>
public sealed class ClientThemeEditorSource(ClientThemeService service) : IEditorThemeSource
{
    private readonly ClientThemeService _service = service ?? throw new ArgumentNullException(nameof(service));

    public ThemeDefinition Current => _service.Current;

    public event EventHandler? Changed
    {
        add => _service.Changed += value;
        remove => _service.Changed -= value;
    }
}

/// <summary>
/// Keeps the active editor host on the app theme: pushes the current theme when started and again after every change, without
/// reloading the editor (Monaco through setTheme, AvaloniaEdit through <see cref="IEditorHost.SetThemeAsync"/>). Pushes run one
/// at a time, newest theme last. A failed push (host disposed, crashed, not ready) is logged and never thrown, because the host
/// re-applies its last theme when it is ready. The host is looked up for each push, so a head that swaps hosts after a fault
/// keeps working. Dispose to stop.
/// </summary>
public sealed class EditorThemeSync : IDisposable
{
    private readonly IEditorThemeSource _source;
    private readonly EditorThemeProvider _provider;
    private readonly Func<IEditorHost?> _host;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public EditorThemeSync(IEditorThemeSource source, Func<IEditorHost?> host, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _host = host;
        _log = log;
        _provider = new EditorThemeProvider(() => source.Current);
    }

    /// <summary>Pushes the current theme and starts following changes. Completes when the first push did.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _source.Changed += OnChanged;
        return PushAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => _ = PushAsync(CancellationToken.None);

    private async Task PushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _host() is not { } host)
            {
                return;
            }

            await _provider.PushAsync(host, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _log($"Athanor: the editor theme was not applied ({ex.Message}).");
        }
        finally
        {
            _gate.Release();
        }
    }
}
