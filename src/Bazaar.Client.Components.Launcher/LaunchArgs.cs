namespace Bazaar.Client.Components.Launcher;

/// <summary>Parses the command line: <c>--open &lt;path&gt;</c> (repeatable) and <c>--terminal</c>. Unknown arguments are ignored.</summary>
public static class LaunchArgs
{
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        var terminal = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--open" when i + 1 < args.Count:
                    paths.Add(args[++i]);
                    break;
                case "--terminal":
                    terminal = true;
                    break;
            }
        }

        return new LaunchRequest(paths, terminal);
    }
}
