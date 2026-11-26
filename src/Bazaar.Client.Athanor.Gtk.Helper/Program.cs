using System.Text;

namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>
/// Entry point of the Athanor GTK helper (doc 57 section 2.1). Reads one JSON line per message on stdin, writes one on
/// stdout, logs to stderr. Exit codes are in <see cref="HelperExitCodes"/>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (!HelperOptions.TryParse(args, out HelperOptions? options, out string? error))
        {
            Console.Error.WriteLine("athanor-gtk-helper: " + error);
            return HelperExitCodes.BadArguments;
        }

        string entryPath = Path.Combine(options!.BundleDirectory, options.EntryFile);
        if (!File.Exists(entryPath))
        {
            Console.Error.WriteLine($"athanor-gtk-helper: bundle entry not found: {entryPath}");
            return HelperExitCodes.BundleMissing;
        }

        bool disableSwitch = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SandboxPolicy.DisableVariable));
        bool bubblewrap = SandboxPolicy.FindBubblewrap(Environment.GetEnvironmentVariable("PATH"));
        SandboxDecision sandbox = SandboxPolicy.Evaluate(options.AllowNoSandbox, disableSwitch, bubblewrap);
        if (!sandbox.CanStart)
        {
            Console.Error.WriteLine("athanor-gtk-helper: " + sandbox.Reason);
            return sandbox.ExitCode;
        }

        if (!GtkNative.TryLoadAll(out string? missing))
        {
            Console.Error.WriteLine($"athanor-gtk-helper: {missing} is not installed (WebKitGTK 4.1 and GTK 3 are runtime dependencies).");
            return HelperExitCodes.NativeLibraryMissing;
        }

        // The native environment is what WebKit reads, not .NET's copy, so go through setenv.
        if (sandbox.SandboxActive)
        {
            GtkNative.SetEnv(SandboxPolicy.ForceVariable, "1", 1);
        }
        else
        {
            GtkNative.SetEnv(SandboxPolicy.DisableVariable, "1", 1);
        }

        int argc = 0;
        if (!GtkNative.GtkInitCheck(ref argc, 0))
        {
            Console.Error.WriteLine("athanor-gtk-helper: GTK could not open a display.");
            return HelperExitCodes.NoDisplay;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var surface = new GtkEditorSurface(options.BundleDirectory, options.EntryFile);
        var core = new HelperCore(surface, stdout);
        surface.Create(core, options);
        core.SendHello(GtkEditorSurface.WebKitVersion(), sandbox.SandboxActive, Environment.ProcessId);

        var reader = new Thread(() => core.RunInputLoop(stdin)) { IsBackground = true, Name = "athanor-helper-stdin" };
        reader.Start();
        GtkEditorSurface.RunMainLoop();
        return HelperExitCodes.Ok;
    }
}
