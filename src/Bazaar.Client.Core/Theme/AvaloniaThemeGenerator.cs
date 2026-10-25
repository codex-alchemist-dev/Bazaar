using System.Globalization;
using System.Text;

namespace Bazaar.Client.Core.Theme;

/// <summary>
/// Emits an Avalonia ResourceDictionary as XAML text from a validated theme. Pure string output: no Avalonia types.
/// Keys are "Bazaar." plus the token name. Colours get a Color and a SolidColorBrush (brush key "Bazaar.brush." plus the name).
/// Input is validated, so values contain no XML-special characters and need no escaping.
/// </summary>
public static class AvaloniaThemeGenerator
{
    public static string Generate(ThemeDefinition theme)
    {
        var sb = new StringBuilder();
        sb.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n");
        sb.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n");
        sb.Append("  <!-- Generated from a Bazaar theme (schemaVersion ")
            .Append(theme.SchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Append(", base ")
            .Append(theme.Base == ThemeBase.Dark ? "dark" : "light")
            .Append("). Do not edit; regenerate from the theme file. -->\n");

        foreach (TokenSpec spec in ThemeTokens.Required)
        {
            string value = theme.Tokens[spec.Name];
            string key = "Bazaar." + spec.Name;
            switch (spec.Kind)
            {
                case TokenKind.Colour:
                    sb.Append("  <Color x:Key=\"").Append(key).Append("\">").Append(value).Append("</Color>\n");
                    sb.Append("  <SolidColorBrush x:Key=\"Bazaar.brush.").Append(spec.Name)
                        .Append("\" Color=\"{StaticResource ").Append(key).Append("}\" />\n");
                    break;
                case TokenKind.FontFamily:
                    sb.Append("  <FontFamily x:Key=\"").Append(key).Append("\">").Append(value).Append("</FontFamily>\n");
                    break;
                case TokenKind.Length:
                    sb.Append("  <x:Double x:Key=\"").Append(key).Append("\">").Append(value).Append("</x:Double>\n");
                    break;
                case TokenKind.Flag:
                    string flag = value == "true" ? "True" : "False";
                    sb.Append("  <x:Boolean x:Key=\"").Append(key).Append("\">").Append(flag).Append("</x:Boolean>\n");
                    break;
            }
        }

        sb.Append("</ResourceDictionary>\n");
        return sb.ToString();
    }
}
