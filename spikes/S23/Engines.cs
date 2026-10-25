using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace Spike.S23;

public sealed record MergeOut(bool Clean, string? Sha, string[] ConflictPaths);

/// <summary>Spike-local shape of the operations IGitProvider needs (docs 17 and 18). One implementation per engine.</summary>
public abstract class Engine
{
    public abstract string Name { get; }
    public abstract void Init(string path);
    public abstract void CommitAll(string workdir, string message);
    public abstract void AddWorktree(string repo, string branch, string wtPath);
    public abstract void RemoveWorktree(string repo, string wtPath);
    public abstract void CreateBranch(string repo, string branch);
    public abstract void Checkout(string workdir, string branch);
    public abstract MergeOut Merge(string workdir, string branch);
    public abstract void AbortMerge(string workdir);
    public abstract IReadOnlyList<string> DiffPaths(string repo, string oldSha, string newSha);

    public static void Write(string workdir, string rel, string content)
    {
        var p = Path.Combine(workdir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }
}

public static class Cli
{
    public static (int Exit, string Out, string Err) Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "S23";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "s23@example.invalid";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.TrimEnd(), err.Result.TrimEnd());
    }

    public static string Ok(string dir, params string[] args)
    {
        var r = Run(dir, args);
        if (r.Exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} -> {r.Exit}: {r.Err}");
        return r.Out;
    }
}

public sealed class CliEngine : Engine
{
    public override string Name => "git CLI";

    public override void Init(string path)
    {
        Directory.CreateDirectory(path);
        Cli.Ok(path, "init", "-q", "-b", "main");
        Cli.Ok(path, "config", "core.autocrlf", "false");
        Cli.Ok(path, "config", "user.name", "S23");
        Cli.Ok(path, "config", "user.email", "s23@example.invalid");
    }

    public override void CommitAll(string workdir, string message)
    {
        Cli.Ok(workdir, "add", "-A");
        Cli.Ok(workdir, "commit", "-q", "-m", message);
    }

    public override void AddWorktree(string repo, string branch, string wtPath)
    {
        var exists = Cli.Run(repo, "rev-parse", "--verify", "-q", "refs/heads/" + branch).Exit == 0;
        if (exists) Cli.Ok(repo, "worktree", "add", wtPath, branch);
        else Cli.Ok(repo, "worktree", "add", "-b", branch, wtPath);
    }

    public override void RemoveWorktree(string repo, string wtPath) => Cli.Ok(repo, "worktree", "remove", "--force", wtPath);
    public override void CreateBranch(string repo, string branch) => Cli.Ok(repo, "branch", branch);
    public override void Checkout(string workdir, string branch) => Cli.Ok(workdir, "checkout", "-q", branch);

    public override MergeOut Merge(string workdir, string branch)
    {
        var r = Cli.Run(workdir, "merge", "--no-ff", "-m", "merge " + branch, branch);
        if (r.Exit == 0) return new(true, Cli.Ok(workdir, "rev-parse", "HEAD"), []);
        var conflicts = Cli.Run(workdir, "diff", "--name-only", "--diff-filter=U").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Order().ToArray();
        if (conflicts.Length == 0) throw new InvalidOperationException($"merge failed without conflicts: {r.Err} {r.Out}");
        return new(false, null, conflicts);
    }

    public override void AbortMerge(string workdir) => Cli.Ok(workdir, "merge", "--abort");

    public override IReadOnlyList<string> DiffPaths(string repo, string oldSha, string newSha) =>
        Cli.Ok(repo, "diff", "--name-only", oldSha, newSha).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

public sealed class LibGit2Engine : Engine
{
    public override string Name => "LibGit2Sharp";
    static Signature Sig() => new("S23", "s23@example.invalid", DateTimeOffset.UtcNow);

    public override void Init(string path)
    {
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var r = new Repository(path);
        r.Config.Set("core.autocrlf", "false");
        r.Config.Set("user.name", "S23");
        r.Config.Set("user.email", "s23@example.invalid");
        // Repository.Init uses the global default branch name; make it deterministic.
        File.WriteAllText(Path.Combine(r.Info.Path, "HEAD"), "ref: refs/heads/main\n");
    }

    public override void CommitAll(string workdir, string message)
    {
        using var r = new Repository(workdir);
        Commands.Stage(r, "*");
        var s = Sig();
        r.Commit(message, s, s);
    }

    public override void AddWorktree(string repo, string branch, string wtPath)
    {
        using var r = new Repository(repo);
        var b = r.Branches[branch] ?? r.CreateBranch(branch);
        r.Worktrees.Add(b.CanonicalName, Path.GetFileName(wtPath), wtPath, false);
    }

    public override void RemoveWorktree(string repo, string wtPath)
    {
        using var r = new Repository(repo);
        var wt = r.Worktrees[Path.GetFileName(wtPath)] ?? throw new InvalidOperationException("worktree not found");
        wt.WorktreeRepository.Dispose();
        r.Worktrees.Prune(wt, true);
        if (Directory.Exists(wtPath)) Directory.Delete(wtPath, true);
    }

    public override void CreateBranch(string repo, string branch)
    {
        using var r = new Repository(repo);
        r.CreateBranch(branch);
    }

    public override void Checkout(string workdir, string branch)
    {
        using var r = new Repository(workdir);
        Commands.Checkout(r, r.Branches[branch]);
    }

    public override MergeOut Merge(string workdir, string branch)
    {
        using var r = new Repository(workdir);
        var res = r.Merge(r.Branches[branch], Sig(), new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
        return res.Status switch
        {
            MergeStatus.Conflicts => new(false, null, r.Index.Conflicts
                .Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor).Path).Distinct().Order().ToArray()),
            MergeStatus.UpToDate => new(true, r.Head.Tip.Sha, []),
            _ => new(true, res.Commit.Sha, []),
        };
    }

    public override void AbortMerge(string workdir)
    {
        using var r = new Repository(workdir);
        r.Reset(ResetMode.Hard);
    }

    public override IReadOnlyList<string> DiffPaths(string repo, string oldSha, string newSha)
    {
        using var r = new Repository(repo);
        return r.Diff.Compare<TreeChanges>(r.Lookup<Commit>(oldSha).Tree, r.Lookup<Commit>(newSha).Tree).Select(c => c.Path).ToArray();
    }
}
