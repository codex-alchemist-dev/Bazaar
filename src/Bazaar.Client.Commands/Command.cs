using Bazaar.Rubedo;
using Bazaar.Rubedo.Rules;

namespace Bazaar.Client.Commands;

/// <summary>
/// A command the palette can list and run. Pure data plus an optional enabled predicate;
/// no UI types, so the registry and matcher stay testable without Avalonia.
/// </summary>
public sealed class Command
{
    public Command(
        string id,
        string title,
        string mnemonic,
        IEnumerable<string>? keywords,
        CommandSource source,
        Func<bool>? enabled = null,
        string? enabledWhen = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ValidateMnemonic(mnemonic);
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown command source.");
        }

        Id = id;
        Title = title;
        Mnemonic = mnemonic;
        Keywords = (keywords ?? []).Select(ValidateKeyword).ToArray();
        Source = source;
        Enabled = enabled;
        EnabledWhen = enabledWhen;
        _enabledRule = CompileEnabledWhen(enabledWhen);
    }

    private readonly VisibilityRule? _enabledRule;

    public string Id { get; }

    public string Title { get; }

    /// <summary>Letters, digits and single inner spaces. Chords run on Enter, so one mnemonic may prefix another.</summary>
    public string Mnemonic { get; }

    public IReadOnlyList<string> Keywords { get; }

    public CommandSource Source { get; }

    /// <summary>Evaluated at query time, never cached. Null means always enabled.</summary>
    public Func<bool>? Enabled { get; }

    /// <summary>Optional Rubedo bool source (doc 41a section 10). Compiled at construction; null means no rule.</summary>
    public string? EnabledWhen { get; }

    /// <summary>
    /// Evaluates the enabled predicate now. A command with an <see cref="EnabledWhen"/> rule has no context here, so it
    /// is disabled (fail closed); use <see cref="IsEnabled(RubedoContext)"/> for those.
    /// </summary>
    public bool IsEnabled() => _enabledRule is null && (Enabled?.Invoke() ?? true);

    /// <summary>
    /// Evaluates the predicate and the rule; both must pass when both are set. A rule that fails at run time disables the
    /// command (fail closed). The predicate is not called when the rule already disables it.
    /// </summary>
    public bool IsEnabled(RubedoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_enabledRule is not null && !_enabledRule.Evaluate(context).Visible)
        {
            return false;
        }

        return Enabled?.Invoke() ?? true;
    }

    private static VisibilityRule? CompileEnabledWhen(string? enabledWhen)
    {
        if (enabledWhen is null)
        {
            return null;
        }

        (VisibilityRule? rule, IReadOnlyList<RubedoDiagnostic> diagnostics) = VisibilityRule.Compile(enabledWhen);
        if (rule is null)
        {
            throw new ArgumentException($"enabledWhen rejected ({diagnostics[0].Code}): {diagnostics[0].Message}", nameof(enabledWhen));
        }

        return rule;
    }

    private static void ValidateMnemonic(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrEmpty(mnemonic);
        if (mnemonic[0] == ' ' || mnemonic[^1] == ' ')
        {
            throw new ArgumentException("Mnemonic must not start or end with a space.", nameof(mnemonic));
        }

        foreach (var c in mnemonic)
        {
            if (c != ' ' && !char.IsLetterOrDigit(c))
            {
                throw new ArgumentException(
                    $"Mnemonic '{mnemonic}' contains '{c}'. Only letters, digits and spaces are allowed.",
                    nameof(mnemonic));
            }
        }
    }

    private static string ValidateKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return keyword;
    }
}
