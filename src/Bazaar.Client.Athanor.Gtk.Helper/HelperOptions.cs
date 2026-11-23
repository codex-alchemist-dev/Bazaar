using System.Globalization;

namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>The helper's command line (doc 57 section 2.1). Everything arrives as arguments; the helper reads no config file.</summary>
public sealed record HelperOptions(
    string BundleDirectory,
    string EntryFile,
    string Title,
    int Width,
    int Height,
    bool AllowNoSandbox)
{
    /// <summary>The page the helper loads from the bundle when <c>--entry</c> is not given.</summary>
    public const string DefaultEntry = "host.html";

    /// <summary>Parses the arguments. Returns false with a reason for a missing or unknown argument.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out HelperOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;
        string? bundle = null;
        string entry = DefaultEntry;
        string title = "Athanor";
        int width = 1100;
        int height = 700;
        bool allowNoSandbox = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--allow-no-sandbox")
            {
                allowNoSandbox = true;
                continue;
            }

            if (arg is not ("--bundle" or "--entry" or "--title" or "--width" or "--height"))
            {
                error = $"Unknown argument {arg}.";
                return false;
            }

            if (i + 1 >= args.Count)
            {
                error = $"{arg} needs a value.";
                return false;
            }

            string value = args[++i];
            switch (arg)
            {
                case "--bundle":
                    bundle = value;
                    break;
                case "--entry":
                    entry = value;
                    break;
                case "--title":
                    title = value;
                    break;
                default:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 100 or > 20000)
                    {
                        error = $"{arg} must be a whole number from 100 to 20000.";
                        return false;
                    }

                    if (arg == "--width")
                    {
                        width = n;
                    }
                    else
                    {
                        height = n;
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(bundle))
        {
            error = "--bundle <directory> is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry) || entry.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entry))
        {
            error = "--entry must be a relative path inside the bundle.";
            return false;
        }

        options = new HelperOptions(Path.GetFullPath(bundle), entry, title, width, height, allowNoSandbox);
        return true;
    }
}
