namespace Bazaar.Client.Tui;

/// <summary>One entry of a menu. <see cref="Shortcut"/> is the optional single letter (underlined in the menu, no Ctrl chord).</summary>
public sealed record MenuItemModel(string Id, string Title, char? Shortcut = null);

/// <summary>One menu of the menu bar. A menu with no items is itself the action (a top-level screen such as Health).</summary>
public sealed record MenuModel(string Id, string Title, char? Shortcut, IReadOnlyList<MenuItemModel> Items);
