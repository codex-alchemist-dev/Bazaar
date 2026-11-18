using Bazaar.Client.Core.Theme;

namespace Bazaar.Client.Athanor.Tests.Hosting;

/// <summary>Builds the theme and documents the hosting tests use. The palette values match the Core dark golden fixture.</summary>
internal static class HostingFixtures
{
    public static ThemeDefinition DarkTheme(string accent = "#89B4FA") =>
        new(
            SchemaVersion: 1,
            Name: "Test dark",
            Base: ThemeBase.Dark,
            Tokens: new Dictionary<string, string>
            {
                ["color.background.editor"] = "#181825",
                ["color.accent.default"] = accent,
                ["color.syntax.default"] = "#CDD6F4",
                ["color.syntax.keyword"] = "#CBA6F7",
                ["color.syntax.string"] = "#A6E3A1",
                ["color.syntax.comment"] = "#6C7086",
                ["color.syntax.number"] = "#FAB387",
                ["color.syntax.type"] = "#F9E2AF",
            });

    public static EditorDocument Document(string path, string content = "hello", bool readOnly = false) =>
        new(new EditorDocumentId("ws", path), content, "rev-1", "plaintext", readOnly);

    public static EditorKeyEvent Key(string key, bool ctrl = false) =>
        new(key, Shift: false, Alt: false, Meta: false, Ctrl: ctrl);
}
