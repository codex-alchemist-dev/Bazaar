using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// Clones a shipped AvaloniaEdit highlighting definition and recolours every colour from a <see cref="TokenColorMap"/>
/// (doc 57 §2). The shipped definition is never mutated, so editors with different themes do not affect each other.
/// </summary>
public static class ThemedHighlighting
{
    /// <summary>Returns a recoloured copy of <paramref name="source"/>.</summary>
    public static IHighlightingDefinition Apply(IHighlightingDefinition source, TokenColorMap map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        return new Cloner(source, map).Build();
    }

    private sealed class Cloner
    {
        private readonly IHighlightingDefinition _source;
        private readonly TokenColorMap _map;
        private readonly Dictionary<HighlightingColor, string> _names = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<HighlightingColor, HighlightingColor> _colours = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<HighlightingRuleSet, HighlightingRuleSet> _ruleSets = new(ReferenceEqualityComparer.Instance);

        public Cloner(IHighlightingDefinition source, TokenColorMap map)
        {
            _source = source;
            _map = map;
            foreach (HighlightingColor named in source.NamedHighlightingColors)
            {
                if (named.Name is { } name)
                {
                    _names[named] = name;
                }
            }
        }

        public ThemedDefinition Build()
        {
            HighlightingRuleSet main = Rules(_source.MainRuleSet);
            var named = new Dictionary<string, HighlightingColor>(StringComparer.Ordinal);
            foreach (HighlightingColor colour in _source.NamedHighlightingColors)
            {
                if (colour.Name is { } name)
                {
                    named[name] = Colour(colour)!;
                }
            }

            return new ThemedDefinition(_source.Name, main, named, this);
        }

        public HighlightingRuleSet? NamedRuleSet(string name) =>
            _source.GetNamedRuleSet(name) is { } ruleSet ? Rules(ruleSet) : null;

        private HighlightingRuleSet Rules(HighlightingRuleSet source)
        {
            if (_ruleSets.TryGetValue(source, out HighlightingRuleSet? existing))
            {
                return existing;
            }

            var copy = new HighlightingRuleSet { Name = source.Name };
            _ruleSets[source] = copy;
            foreach (HighlightingRule rule in source.Rules)
            {
                copy.Rules.Add(new HighlightingRule { Regex = rule.Regex, Color = Colour(rule.Color) });
            }

            foreach (HighlightingSpan span in source.Spans)
            {
                copy.Spans.Add(new HighlightingSpan
                {
                    StartExpression = span.StartExpression,
                    EndExpression = span.EndExpression,
                    RuleSet = span.RuleSet is null ? null : Rules(span.RuleSet),
                    StartColor = Colour(span.StartColor),
                    EndColor = Colour(span.EndColor),
                    SpanColor = Colour(span.SpanColor),
                    SpanColorIncludesStart = span.SpanColorIncludesStart,
                    SpanColorIncludesEnd = span.SpanColorIncludesEnd,
                });
            }

            return copy;
        }

        private HighlightingColor? Colour(HighlightingColor? source)
        {
            if (source is null)
            {
                return null;
            }

            if (_colours.TryGetValue(source, out HighlightingColor? existing))
            {
                return existing;
            }

            HighlightingColor copy = source.Clone();
            _names.TryGetValue(source, out string? name);
            string? scope = TokenColorMap.ScopeFor(name ?? source.Name);
            Color? colour = (scope is null ? null : _map.Resolve(scope)) ?? _map.Default;
            if (colour is { } c)
            {
                copy.Foreground = new SimpleHighlightingBrush(c);
            }

            _colours[source] = copy;
            return copy;
        }
    }

    private sealed class ThemedDefinition : IHighlightingDefinition
    {
        private readonly Cloner _owner;
        private readonly Dictionary<string, HighlightingColor> _named;

        public ThemedDefinition(string name, HighlightingRuleSet main, Dictionary<string, HighlightingColor> named, Cloner owner)
        {
            Name = name;
            MainRuleSet = main;
            _named = named;
            _owner = owner;
        }

        public string Name { get; }

        public HighlightingRuleSet MainRuleSet { get; }

        public IEnumerable<HighlightingColor> NamedHighlightingColors => _named.Values;

        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

        public HighlightingRuleSet? GetNamedRuleSet(string name) => _owner.NamedRuleSet(name);

        public HighlightingColor? GetNamedColor(string name) => _named.GetValueOrDefault(name);
    }
}
