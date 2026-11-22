using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Localisation;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Analysis;
using Bazaar.Rubedo.Rules;

namespace Bazaar.Client.Rubedo.Playground;

/// <summary>What the playground edits: one Rubedo expression, or one rubedo-view JSON document (doc 41d section 7).</summary>
public enum PlaygroundMode
{
    Expression,
    View,
}

/// <summary>One diagnostic row: the stable code, the message and the source offsets, plus the localised line.</summary>
public sealed record PlaygroundDiagnosticLine(string Code, string Message, int Start, int End, string Text);

/// <summary>
/// The playground pane's view model (doc 41d section 7). Platform-neutral: it holds the source, the nine context values of
/// <c>RubedoSchema.Standard</c> and the live result, and recomputes the result whenever one changes. It runs the shared engine
/// in-process and makes no server call.
/// </summary>
public sealed class PlaygroundViewModel : IPaneViewModel, INotifyPropertyChanged
{
    /// <summary>The pane kind this module registers.</summary>
    public const string PaneKind = "bazaar.rubedo-playground";

    /// <summary>Values the text identifiers take (doc 41a section 3).</summary>
    public static IReadOnlyList<string> DeviceClasses { get; } = ["phone", "foldable", "tablet", "desktop"];

    public static IReadOnlyList<string> Displays { get; } = ["single", "cover", "inner"];

    public static IReadOnlyList<string> Postures { get; } = ["folded", "halfOpen", "unfolded"];

    public static IReadOnlyList<string> ColorSchemes { get; } = ["light", "dark"];

    private readonly LocalisedText _text;
    private string _source = "deviceClass == \"phone\"";
    private PlaygroundMode _mode;
    private string _deviceClass = "desktop";
    private string _display = "single";
    private string _posture = "unfolded";
    private decimal _widthDp = 1280;
    private decimal _heightDp = 800;
    private decimal _textScale = 1;
    private bool _reduceMotion;
    private bool _isOwner = true;
    private string _colorScheme = "light";
    private string _resultText = string.Empty;
    private string _formatMessage = string.Empty;
    private IReadOnlyList<PlaygroundDiagnosticLine> _diagnostics = [];

    public PlaygroundViewModel(LocalisedText? text = null)
    {
        _text = text ?? PlaygroundText.Default;
        Recompute();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind => PaneKind;

    public string Title => Text(PlaygroundText.Title);

    /// <summary>The localised text for a key from <see cref="PlaygroundText"/>. Views use it for every label.</summary>
    public string Text(string key) => _text.Resolve(key);

    public string Source
    {
        get => _source;
        set => Set(ref _source, value ?? string.Empty);
    }

    public PlaygroundMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                OnChanged(nameof(CanFormat));
            }
        }
    }

    public string DeviceClass
    {
        get => _deviceClass;
        set => Set(ref _deviceClass, value ?? string.Empty);
    }

    public string Display
    {
        get => _display;
        set => Set(ref _display, value ?? string.Empty);
    }

    public string Posture
    {
        get => _posture;
        set => Set(ref _posture, value ?? string.Empty);
    }

    public decimal WidthDp
    {
        get => _widthDp;
        set => Set(ref _widthDp, value);
    }

    public decimal HeightDp
    {
        get => _heightDp;
        set => Set(ref _heightDp, value);
    }

    public decimal TextScale
    {
        get => _textScale;
        set => Set(ref _textScale, value);
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set => Set(ref _reduceMotion, value);
    }

    public bool IsOwner
    {
        get => _isOwner;
        set => Set(ref _isOwner, value);
    }

    public string ColorScheme
    {
        get => _colorScheme;
        set => Set(ref _colorScheme, value ?? string.Empty);
    }

    /// <summary>The value of the expression as text, or a validity line for a view document. Empty while there are diagnostics.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetOutput(ref _resultText, value);
    }

    /// <summary>Diagnostics with offsets, empty when the source is valid.</summary>
    public IReadOnlyList<PlaygroundDiagnosticLine> Diagnostics
    {
        get => _diagnostics;
        private set
        {
            _diagnostics = value;
            OnChanged();
            OnChanged(nameof(HasDiagnostics));
        }
    }

    public bool HasDiagnostics => _diagnostics.Count > 0;

    /// <summary>The outcome of the last format attempt, empty after any other change.</summary>
    public string FormatMessage
    {
        get => _formatMessage;
        private set => SetOutput(ref _formatMessage, value);
    }

    /// <summary>Formatting uses <see cref="RubedoFormatter"/>, which handles expressions only.</summary>
    public bool CanFormat => _mode == PlaygroundMode.Expression;

    /// <summary>The context the expression runs against, built from the nine controls.</summary>
    public RubedoContext Context => RubedoContext.ForDevice(
        _deviceClass, _display, _posture, _widthDp, _heightDp, _textScale, _reduceMotion, _isOwner, _colorScheme);

    /// <summary>
    /// Replaces the source with its canonical form. Returns false and leaves the source unchanged when formatting does not
    /// apply (view mode) or the source has diagnostics.
    /// </summary>
    public bool Format()
    {
        if (!CanFormat)
        {
            FormatMessage = Text(PlaygroundText.FormatUnavailable);
            return false;
        }

        RubedoFormatResult result = RubedoFormatter.Format(_source);
        if (!result.IsValid)
        {
            FormatMessage = Text(PlaygroundText.FormatFailed);
            return false;
        }

        // Setting the source recomputes and clears the message, so the success path leaves it empty.
        Source = result.Formatted!;
        return true;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnChanged(name);
        FormatMessage = string.Empty;
        Recompute();
        return true;
    }

    private void SetOutput<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnChanged(name);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Recompute()
    {
        IReadOnlyList<AnalysisDiagnostic> found;
        string result = string.Empty;

        if (_mode == PlaygroundMode.View)
        {
            found = RubedoAnalyzer.DiagnoseView(_source);
            if (found.Count == 0)
            {
                result = Text(PlaygroundText.ResultValid);
            }
        }
        else
        {
            found = RubedoAnalyzer.Diagnose(_source);
            if (found.Count == 0)
            {
                // Diagnose found no problem, so Compile succeeds. Evaluation can still fail at run time (for example
                // a division by zero); that error is a diagnostic too.
                RubedoCompileResult compiled = RubedoEngine.Compile(_source);
                RubedoEvaluation evaluation = RubedoEngine.Evaluate(compiled.Program!, Context.ToValues());
                if (evaluation.IsOk)
                {
                    result = Show(evaluation.Value);
                }
                else
                {
                    int at = Math.Max(0, evaluation.Error!.Offset);
                    found = [new AnalysisDiagnostic(evaluation.Error.Code, evaluation.Error.Message, at, at)];
                }
            }
        }

        Diagnostics = [.. found.Select(ToLine)];
        ResultText = result;
    }

    private PlaygroundDiagnosticLine ToLine(AnalysisDiagnostic d) => new(
        d.Code,
        d.Message,
        d.Start,
        d.End,
        _text.Resolve(PlaygroundText.DiagnosticLine, new Dictionary<string, string>
        {
            ["code"] = d.Code,
            ["start"] = d.Start.ToString(CultureInfo.InvariantCulture),
            ["end"] = d.End.ToString(CultureInfo.InvariantCulture),
            ["message"] = d.Message,
        }));

    // Values print in the language's own literal form, so a Text result shows its quotes and a Bool is lower case.
    private static string Show(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => "\"" + s + "\"",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        null => string.Empty,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
