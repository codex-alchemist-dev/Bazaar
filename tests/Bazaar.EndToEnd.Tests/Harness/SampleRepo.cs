using System.Diagnostics;
using System.Text;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// A throwaway git repository with one failing test, standing in for the repository named by the future
/// <c>Coding:Harness:Repo</c> setting (GAPS.md, gap 9). The worktree per task comes from the real flow
/// (<see cref="TaskWorkspaceFlow"/>). Uses the real <c>git</c> and <c>sh</c>; nothing here fakes either.
/// </summary>
public sealed class SampleRepo : IDisposable
{
    public const string BuggyAdd = "#!/bin/sh\n# add two integers\necho $(($1 - $2))\n";

    public const string AddTest =
        "#!/bin/sh\n# prints the failing check and exits 1 when add is wrong\n" +
        "got=$(sh \"$(dirname \"$0\")/../src/add.sh\" 2 3)\n" +
        "if [ \"$got\" = \"5\" ]; then echo \"ok: add 2 3 = 5\"; else echo \"FAIL: add 2 3 gave $got, want 5\"; exit 1; fi\n";

    /// <summary>The one-line fix, as a unified diff a model could return.</summary>
    public const string FixDiff =
        "--- a/src/add.sh\n+++ b/src/add.sh\n@@ -1,3 +1,3 @@\n #!/bin/sh\n # add two integers\n-echo $(($1 - $2))\n+echo $(($1 + $2))\n";

    /// <summary>A "fix" that makes the test pass by weakening it: the checks must flag this.</summary>
    public const string CheatDiff =
        "--- a/tests/test.sh\n+++ b/tests/test.sh\n@@ -2,3 +2,2 @@\n # prints the failing check and exits 1 when add is wrong\n-got=$(sh \"$(dirname \"$0\")/../src/add.sh\" 2 3)\n-if [ \"$got\" = \"5\" ]; then echo \"ok: add 2 3 = 5\"; else echo \"FAIL: add 2 3 gave $got, want 5\"; exit 1; fi\n+echo \"ok: nothing checked\"\n";

    private readonly string _root;

    private SampleRepo(string root) => _root = root;

    public string MainPath => Path.Combine(_root, "main");

    public string WorktreesPath => Path.Combine(_root, "worktrees");

    /// <summary>Null when <c>git</c> or <c>sh</c> is not on PATH (some Windows machines); the caller skips.</summary>
    public static async Task<SampleRepo?> TryCreateAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-e2e-repo-" + Guid.NewGuid().ToString("N"));
        var repo = new SampleRepo(root);
        Directory.CreateDirectory(repo.MainPath);
        Directory.CreateDirectory(repo.WorktreesPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(repo.MainPath, "src"));
            Directory.CreateDirectory(Path.Combine(repo.MainPath, "tests"));
            await File.WriteAllTextAsync(Path.Combine(repo.MainPath, "src", "add.sh"), BuggyAdd, ct);
            await File.WriteAllTextAsync(Path.Combine(repo.MainPath, "tests", "test.sh"), AddTest, ct);
            await GitAsync(repo.MainPath, ["init", "-q", "-b", "main"], ct);
            await GitAsync(repo.MainPath, ["add", "."], ct);
            await GitAsync(repo.MainPath, ["commit", "-q", "-m", "Initial commit: add is wrong"], ct);
            await RunAsync("sh", ["-c", "true"], repo.MainPath, ct);
            return repo;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            repo.Dispose();
            return null;
        }
    }

    /// <summary>Applies a unified diff to the worktree. Throws when it does not apply.</summary>
    public static async Task ApplyAsync(string worktree, string diff, CancellationToken ct)
    {
        var patch = Path.Combine(Path.GetTempPath(), "bazaar-e2e-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            await File.WriteAllTextAsync(patch, diff, ct);
            await GitAsync(worktree, ["apply", "--whitespace=nowarn", patch], ct);
        }
        finally
        {
            File.Delete(patch);
        }
    }

    /// <summary>Runs the repository's test script in <paramref name="dir"/>.</summary>
    public static Task<ProcessResult> RunTestsAsync(string dir, CancellationToken ct) => RunAsync("sh", ["tests/test.sh"], dir, ct);

    public static async Task<string> GitAsync(string dir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var all = new List<string> { "-c", "user.name=Acceptance Agent", "-c", "user.email=agent@bazaar.invalid", "-c", "commit.gpgsign=false" };
        all.AddRange(args);
        var result = await RunAsync("git", all, dir, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Output}");
        return result.Output;
    }

    public static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, string dir, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(dir, ".no-global-gitconfig");
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, new StringBuilder().Append(await stdout).Append(await stderr).ToString());
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record ProcessResult(int ExitCode, string Output);
