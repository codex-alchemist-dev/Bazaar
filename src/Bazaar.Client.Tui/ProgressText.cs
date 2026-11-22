using System.Globalization;

namespace Bazaar.Client.Tui;

/// <summary>The one text form of a progress line, shared by every sink so the console and the full-screen views read the same.</summary>
public static class ProgressText
{
    /// <summary>For example <c>[#####---] Step 3 of 8: Custody</c>. The bar is <paramref name="barWidth"/> characters of # and -.</summary>
    public static string Line(TuiProgress progress, int barWidth)
    {
        var filled = progress.Total <= 0 ? 0 : Math.Clamp(progress.Current * barWidth / progress.Total, 0, barWidth);
        var bar = "[" + new string('#', filled) + new string('-', barWidth - filled) + "]";
        return string.Create(CultureInfo.InvariantCulture, $"{bar} Step {progress.Current} of {progress.Total}: {progress.Title}");
    }
}
