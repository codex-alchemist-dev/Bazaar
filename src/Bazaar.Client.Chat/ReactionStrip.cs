using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Chat;

/// <summary>
/// The reaction chips under one message. It shows the chips of <see cref="Reactions"/> and follows an observable list edit by edit:
/// a chip that joins plays "Reaction chip added", a changed count bumps, a chip that leaves is removed. Chips present when the list is
/// set are not animated. Colours and sizes come from theme tokens.
/// </summary>
public sealed class ReactionStrip : WrapPanel
{
    /// <summary>The chips to show; an <see cref="IReactionCollection"/> makes later edits animate.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChatReaction>?> ReactionsProperty =
        AvaloniaProperty.Register<ReactionStrip, IReadOnlyList<ChatReaction>?>(nameof(Reactions));

    private readonly Dictionary<string, Chip> _chips = [];
    private ReactionChipsMotion? _motion;
    private INotifyCollectionChanged? _watched;

    public ReactionStrip()
    {
        Orientation = Orientation.Horizontal;
        ThemeBinding.BindLength(this, "space.xs", gap => { ItemSpacing = gap; LineSpacing = gap; });
    }

    public IReadOnlyList<ChatReaction>? Reactions
    {
        get => GetValue(ReactionsProperty);
        set => SetValue(ReactionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReactionsProperty) Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Release();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_watched is null && Reactions is not null) Rebuild();
    }

    private void Release()
    {
        _motion?.Dispose();
        _motion = null;
        if (_watched is not null) _watched.CollectionChanged -= OnReactionsChanged;
        _watched = null;
        foreach (Chip chip in _chips.Values) chip.Motion.Dispose();
    }

    private void Rebuild()
    {
        Release();
        Children.Clear();
        _chips.Clear();
        if (Reactions is not { } reactions) return;

        foreach (ChatReaction reaction in reactions) AddChip(reaction);

        // Attached after the first fill, so the chips that were already there stay still.
        _motion = ReactionChipsMotion.Attach(this);
        if (reactions is INotifyCollectionChanged watched)
        {
            _watched = watched;
            watched.CollectionChanged += OnReactionsChanged;
        }
    }

    private void OnReactionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Reactions is not { } reactions) return;

        // Every edit is reconciled against the list, keyed by emoji: leavers go first, then changed counts update, then joiners are added.
        foreach (string gone in _chips.Keys.Where(k => reactions.All(r => r.Emoji != k)).ToList())
        {
            _chips[gone].Motion.Dispose();
            Children.Remove(_chips[gone].Root);
            _chips.Remove(gone);
        }

        foreach (ChatReaction reaction in reactions)
        {
            if (_chips.TryGetValue(reaction.Emoji, out Chip? chip)) chip.Update(reaction);
            else AddChip(reaction);
        }
    }

    private void AddChip(ChatReaction reaction)
    {
        var chip = new Chip(reaction);
        _chips[reaction.Emoji] = chip;
        Children.Add(chip.Root);
    }

    private sealed class Chip
    {
        private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
        private BadgeMotion? _motion;

        public Chip(ChatReaction reaction)
        {
            var emoji = new TextBlock { Text = reaction.Emoji, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            ThemeBinding.BindLength(row, "space.xs", gap => row.Spacing = gap);
            row.Children.Add(emoji);
            row.Children.Add(_count);
            Root = new Border { Child = row, Tag = reaction.Emoji, BorderThickness = new Thickness(1) };
            ThemeBinding.BindRadius(Root, "radius.md");
            ThemeBinding.BindPadding(Root, "space.sm", "space.xs");
            ThemeBinding.BindBrush(_count, TextBlock.ForegroundProperty, "color.text.secondary");
            Apply(reaction);
            // The count text at this point is the baseline; later changes bump.
            _motion = BadgeMotion.Attach(Root, _count);
        }

        public Border Root { get; }

        public BadgeMotion Motion => _motion!;

        public void Update(ChatReaction reaction) => Apply(reaction);

        private void Apply(ChatReaction reaction)
        {
            _count.Text = reaction.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ThemeBinding.BindBrush(Root, Border.BorderBrushProperty, reaction.Mine ? "color.accent.default" : "color.border.default");
            ThemeBinding.BindBrush(Root, Border.BackgroundProperty, reaction.Mine ? "color.badge.owner.background" : "color.background.surface");
        }
    }
}
