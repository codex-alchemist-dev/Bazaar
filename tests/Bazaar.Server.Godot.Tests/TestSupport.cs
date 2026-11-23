using Bazaar.Server.Engines.Abstractions;

namespace Bazaar.Server.Godot.Tests;

/// <summary>
/// Scripted runner. Records each call and answers from a handler, so adapter tests need no real binary. By default the canary
/// (the call that checks <c>canary.gd</c>) fails as a real Godot would, and every other call exits 0 with no output.
/// </summary>
internal sealed class FakeGodotRunner : IProcessRunner
{
    public const string CanaryFailureOutput = "SCRIPT ERROR: Parse Error: Expected closing \")\".\n          at: GDScript::reload (res://canary.gd:6)\n";

    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<ProcessResult>> _handler;
    private readonly object _gate = new();
    private readonly List<RecordedCall> _calls = [];

    public FakeGodotRunner(Func<IReadOnlyList<string>, string, CancellationToken, Task<ProcessResult>>? handler = null)
    {
        _handler = handler ?? ((_, _, _) => Ok());
    }

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<RecordedCall> NonCanaryCalls => [.. Calls.Where(c => !IsCanary(c.Arguments))];

    public static bool IsCanary(IReadOnlyList<string> arguments) => arguments.Contains("canary.gd");

    public static Task<ProcessResult> Ok(string stdout = "", string stderr = "") =>
        Task.FromResult(new ProcessResult(0, stdout, stderr, TimedOut: false));

    public static Task<ProcessResult> Exit(int code, string stdout = "", string stderr = "") =>
        Task.FromResult(new ProcessResult(code, stdout, stderr, TimedOut: false));

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add(new RecordedCall(fileName, arguments, workingDirectory, timeout));
        }

        if (IsCanary(arguments))
        {
            // The real canary files must exist where Godot would read them.
            Assert.True(File.Exists(Path.Combine(workingDirectory, "canary.gd")));
            Assert.True(File.Exists(Path.Combine(workingDirectory, "project.godot")));
            return await Exit(1, CanaryFailureOutput);
        }

        return await _handler(arguments, workingDirectory, cancellationToken);
    }

    internal sealed record RecordedCall(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);
}

/// <summary>A throwaway Godot project root. Deleted on dispose.</summary>
internal sealed class TempGodotProject : IDisposable
{
    public TempGodotProject(string projectGodot = "config_version=5\n\n[application]\nconfig/name=\"t\"\nconfig/features=PackedStringArray(\"4.7\")\n")
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-godot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "scripts"));
        Directory.CreateDirectory(Path.Combine(Root, "build"));
        File.WriteAllText(Path.Combine(Root, "project.godot"), projectGodot);
        File.WriteAllText(Path.Combine(Root, "scripts", "main.gd"), "extends Node\n");
        File.WriteAllText(Path.Combine(Root, "scripts", "run_tests.gd"), "extends SceneTree\n");
    }

    public string Root { get; }

    public string Path_(string relative) => Path.Combine(Root, relative);

    public void WritePresets(params string[] names)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < names.Length; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"[preset.{i}]\n\nname=\"{names[i]}\"\nplatform=\"Linux\"\nrunnable=true\n\n[preset.{i}.options]\n\nname=\"decoy-{i}\"\n\n");
        }

        File.WriteAllText(Path.Combine(Root, "export_presets.cfg"), text.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a test may leave a link that the OS still holds open.
        }
    }
}
