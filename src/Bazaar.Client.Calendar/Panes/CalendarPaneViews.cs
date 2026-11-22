using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Calendar.Panes;

/// <summary>The month-grid body of the calendar pane. Colours come from theme tokens, never fixed values.</summary>
internal static class CalendarPaneViews
{
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";
    private static readonly string[] _weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    internal static StackPanel Calendar(CalendarPaneViewModel calendar)
    {
        var root = new StackPanel { Spacing = 6 };

        void Render()
        {
            root.Children.Clear();
            root.Children.Add(Header(calendar, Render));
            root.Children.Add(WeekdayRow());
            var body = new StackPanel { Spacing = 4 };
            foreach (var week in calendar.BuildWeeks())
            {
                body.Children.Add(WeekRow(week));
            }

            root.Children.Add(body);
        }

        Render();
        return root;
    }

    private static StackPanel Header(CalendarPaneViewModel calendar, Action render)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(NavButton("Previous month", "<", () => { calendar.PreviousMonth(); render(); }));
        header.Children.Add(new TextBlock { Text = calendar.MonthLabel, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(NavButton("Next month", ">", () => { calendar.NextMonth(); render(); }));
        header.Children.Add(NavButton("Go to today", "Today", () => { calendar.GoToToday(); render(); }));
        return header;
    }

    private static Button NavButton(string name, string text, Action action)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private static Grid WeekdayRow()
    {
        var grid = SevenColumns();
        for (var i = 0; i < 7; i++)
        {
            var label = new TextBlock { Text = _weekdays[i], HorizontalAlignment = HorizontalAlignment.Center };
            SetThemeBrush(label, TextBlock.ForegroundProperty, _secondaryTextToken);
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }

        return grid;
    }

    private static Grid WeekRow(IReadOnlyList<CalendarDayCell> week)
    {
        var grid = SevenColumns();
        for (var i = 0; i < week.Count; i++)
        {
            var cell = DayCell(week[i]);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private static Grid SevenColumns()
    {
        var grid = new Grid();
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        return grid;
    }

    private static Border DayCell(CalendarDayCell day)
    {
        var stack = new StackPanel { Spacing = 2 };
        var number = new TextBlock
        {
            Text = day.Date.Day.ToString(CultureInfo.InvariantCulture),
            FontWeight = day.IsToday ? FontWeight.Bold : FontWeight.Normal,
        };
        if (!day.InVisibleMonth)
        {
            SetThemeBrush(number, TextBlock.ForegroundProperty, _secondaryTextToken);
        }

        stack.Children.Add(number);
        foreach (var chip in day.Chips)
        {
            stack.Children.Add(Chip(chip));
        }

        var border = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(4),
            MinHeight = 72,
            BorderThickness = new Thickness(day.IsToday ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Child = stack,
        };
        SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
        return border;
    }

    private static Border Chip(CalendarChip chip)
    {
        var text = new TextBlock { Text = chip.Label, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        if (chip.IsCancelled)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
            SetThemeBrush(text, TextBlock.ForegroundProperty, _secondaryTextToken);
        }

        var border = new Border
        {
            Padding = new Thickness(4, 2),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = text,
        };
        SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
        AutomationProperties.SetName(border, chip.AccessibleName);
        return border;
    }

    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
