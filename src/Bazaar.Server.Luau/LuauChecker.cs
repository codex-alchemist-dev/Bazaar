using System.ComponentModel;
using System.IO;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Runs <c>luau-analyze</c>, <c>selene</c> and <c>stylua --check</c> headlessly over files under one project root.
/// Tools run one after another, each with its own timeout, so a slow tool cannot starve the others of a shared budget.
/// A missing binary or a tool failure is reported per tool; the other tools still run. Arguments are always lists.
/// </summary>
public sealed class LuauChecker : ILuauChecker
{
    private static readonly LuauToolKind[] _allTools = [LuauToolKind.Analyze, LuauToolKind.Selene, LuauToolKind.StyLuaCheck];

    private readonly LuauToolOptions _options;
    private readonly ILuauToolRunner _runner;

    public LuauChecker(LuauToolOptions options, ILuauToolRunner runner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(options.AnalyzeBinaryPath) || string.IsNullOrWhiteSpace(options.SeleneBinaryPath)
            || string.IsNullOrWhiteSpace(options.StyLuaBinaryPath))
        {
            throw new ArgumentException("Luau tool binary paths must not be empty.", nameof(options));
        }

        if (options.DefaultTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Luau default timeout must be positive.", nameof(options));
        }

        if (options.MaxOutputCharacters <= 0)
        {
            throw new ArgumentException("Luau output cap must be positive.", nameof(options));
        }

        _options = options;
        _runner = runner;
    }

    /// <inheritdoc />
    public async Task<LuauCheckResult> CheckAsync(LuauCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = LuauPathGuard.RequireRoot(request.ProjectRoot);
        IReadOnlyList<string> files = LuauPathGuard.ResolveFiles(root, request.Files);
        IReadOnlyList<LuauToolKind> tools = request.Tools.Count == 0 ? _allTools : request.Tools.Distinct().ToArray();
        TimeSpan timeout = request.Timeout ?? _options.DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new LuauRequestRejectedException("Luau check timeout must be positive.");
        }

        var diagnostics = new List<LuauDiagnostic>();
        var reports = new List<LuauToolReport>();
        foreach (LuauToolKind tool in tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (LuauToolReport report, IReadOnlyList<LuauDiagnostic> found) = await RunToolAsync(tool, root, files, timeout, cancellationToken);
            reports.Add(report);
            diagnostics.AddRange(found);
        }

        return new LuauCheckResult(diagnostics, reports);
    }

    private async Task<(LuauToolReport Report, IReadOnlyList<LuauDiagnostic> Diagnostics)> RunToolAsync(
        LuauToolKind tool,
        string root,
        IReadOnlyList<string> files,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        LuauProcessResult process;
        try
        {
            process = await _runner.RunAsync(BinaryFor(tool), ArgumentsFor(tool, files), root, timeout, cancellationToken);
        }
        catch (Win32Exception ex)
        {
            return (new LuauToolReport(tool, LuauToolStatus.Unavailable, null, $"Could not start '{BinaryFor(tool)}': {ex.Message}", false), []);
        }
        catch (FileNotFoundException ex)
        {
            return (new LuauToolReport(tool, LuauToolStatus.Unavailable, null, $"Could not start '{BinaryFor(tool)}': {ex.Message}", false), []);
        }

        if (process.TimedOut)
        {
            return (new LuauToolReport(tool, LuauToolStatus.TimedOut, null, $"{ToolName(tool)} timed out after {timeout}.", false), []);
        }

        string stdout = LuauOutputCap.Apply(process.StandardOutput, _options.MaxOutputCharacters, out bool stdoutCut);
        string stderr = LuauOutputCap.Apply(process.StandardError, _options.MaxOutputCharacters, out bool stderrCut);
        bool truncated = stdoutCut || stderrCut;

        // Diagnostics may appear on either stream depending on the tool; each parser ignores lines it does not recognise.
        string combined = stdout + "\n" + stderr;
        IReadOnlyList<LuauDiagnostic> found = ParserFor(tool, combined);
        if (found.Count > 0)
        {
            return (new LuauToolReport(tool, LuauToolStatus.Findings, process.ExitCode, null, truncated), found);
        }

        if (process.ExitCode == 0)
        {
            return (new LuauToolReport(tool, LuauToolStatus.Passed, 0, null, truncated), []);
        }

        string reason = FirstLine(stderr) ?? FirstLine(stdout) ?? $"exited with code {process.ExitCode}";
        return (new LuauToolReport(tool, LuauToolStatus.Error, process.ExitCode, $"{ToolName(tool)} {reason}", truncated), []);
    }

    private string BinaryFor(LuauToolKind tool) => tool switch
    {
        LuauToolKind.Analyze => _options.AnalyzeBinaryPath,
        LuauToolKind.Selene => _options.SeleneBinaryPath,
        _ => _options.StyLuaBinaryPath,
    };

    /// <summary>Argument lists for each tool. Files are already validated and relative to the root.</summary>
    internal static IReadOnlyList<string> ArgumentsFor(LuauToolKind tool, IReadOnlyList<string> files)
    {
        var arguments = new List<string>();
        switch (tool)
        {
            case LuauToolKind.Selene:
                arguments.Add("--display-style=Json2");
                break;
            case LuauToolKind.StyLuaCheck:
                arguments.Add("--check");
                break;
        }

        arguments.AddRange(files);
        return arguments;
    }

    private static IReadOnlyList<LuauDiagnostic> ParserFor(LuauToolKind tool, string output) => tool switch
    {
        LuauToolKind.Analyze => LuauAnalyzeOutputParser.Parse(output),
        LuauToolKind.Selene => SeleneOutputParser.Parse(output),
        _ => StyLuaOutputParser.Parse(output),
    };

    private static string ToolName(LuauToolKind tool) => tool switch
    {
        LuauToolKind.Analyze => "luau-analyze",
        LuauToolKind.Selene => "selene",
        _ => "stylua --check",
    };

    /// <summary>First non-blank line of text, trimmed, or null. Tool text is untrusted; it is only shown as a reason.</summary>
    private static string? FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }
}
