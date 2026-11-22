using Avalonia.Controls;
using Avalonia.Threading;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>
/// Hosts one view document in a pane (doc 41c, section 11). It validates the document, checks its declared data sources
/// against the capabilities the module manifest grants, maps it, and rebuilds when a declared source changes. Any load
/// failure shows one placeholder for the whole pane and reports once to <paramref name="onAudit"/>; it never renders
/// part of an invalid document.
/// </summary>
public sealed class ViewHost : ContentControl, IDisposable
{
    private readonly ViewDocument? _document;
    private readonly ViewMapper _mapper;
    private readonly IViewData _data;
    private bool _disposed;

    /// <param name="document">The document to host.</param>
    /// <param name="data">The module's data object.</param>
    /// <param name="context">The head's view context.</param>
    /// <param name="grantedCapabilities">The data capabilities the module manifest declares, such as <c>wire:gc.history</c>.</param>
    /// <param name="onAudit">Receives the view name and the reason when the document is refused.</param>
    public ViewHost(
        ViewDocument document,
        IViewData data,
        ViewContext context,
        IReadOnlySet<string> grantedCapabilities,
        Action<string, string>? onAudit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);

        _mapper = new ViewMapper(context);
        _data = data;

        string? refusal = Refusal(document, grantedCapabilities);
        if (refusal is not null)
        {
            onAudit?.Invoke(document.Name, refusal);
            Content = _mapper.Placeholder(document.Name, "view", refusal);
            return;
        }

        _document = document;
        _data.Changed += OnChanged;
        Rebuild();
    }

    /// <summary>True when the document passed validation and the capability check.</summary>
    public bool IsAccepted => _document is not null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _data.Changed -= OnChanged;
    }

    private static string? Refusal(ViewDocument document, IReadOnlySet<string> granted)
    {
        var problems = ViewValidator.Validate(document);
        if (problems.Count > 0)
        {
            return string.Join("; ", problems.Select(p => $"{p.Code}: {p.Message}"));
        }

        string[] missing = [.. document.Data.Where(d => !granted.Contains(d.Value.Capability)).Select(d => d.Value.Capability).Distinct()];
        return missing.Length == 0 ? null : $"view.capability_not_granted: {string.Join(", ", missing)}";
    }

    private void OnChanged(string source)
    {
        if (_document is null || _disposed || !_document.Data.ContainsKey(source))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private void Rebuild()
    {
        if (_disposed)
        {
            return;
        }

        Content = _mapper.Build(_document!, _data.Snapshot());
    }
}
