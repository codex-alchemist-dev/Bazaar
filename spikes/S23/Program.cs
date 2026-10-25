using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LibGit2Sharp;
using Spike.S23;

// Usage: dotnet run -c Release [-- <scratch-dir>]   (prints RESULT lines; exit 0 always, read the table)
// Child mode: Spike.S23 shared-read <repo>  (one Repository instance shared by many threads)
if (args.Length >= 2 && args[0] == "shared-read") { return SharedRead.Run(args[1]); }

var scratch = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "s23-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(scratch);
Engine[] engines = [new LibGit2Engine(), new CliEngine()];
var results = new List<(string Scenario, string Engine, bool Ok, long Ms, string Detail)>();
var counter = 0;

string Fresh(string tag) => Path.Combine(scratch, $"{tag}-{++counter}");

void Record(string scenario, Engine e, Func<(bool Ok, string Detail)> body)
{
    var sw = Stopwatch.StartNew();
    try { var (ok, d) = body(); results.Add((scenario, e.Name, ok, sw.ElapsedMilliseconds, d)); }
    catch (Exception ex) { results.Add((scenario, e.Name, false, sw.ElapsedMilliseconds, "EXC " + ex.GetType().Name + ": " + Trim(ex.Message))); }
}

string Trim(string s) => s.Replace('\n', ' ').Replace('|', '/')[..Math.Min(s.Length, 160)];
string Fsck(string repo) { var r = Cli.Run(repo, "fsck", "--strict", "--no-dangling"); return r.Exit == 0 && r.Err.Length == 0 ? "fsck ok" : "FSCK FAIL: " + Trim(r.Err + r.Out); }
bool Clean(string wd) => Cli.Ok(wd, "status", "--porcelain").Length == 0;
string Tree(string wd) => Cli.Ok(wd, "rev-parse", "HEAD^{tree}");
string Lines(int n, string tag = "line") => string.Join("\n", Enumerable.Range(1, n).Select(i => $"{tag} {i}")) + "\n";

// Base repo: doc.md with 20 lines, other.md, notes/a.md; branches main, other.
string Base(Engine e, string tag)
{
    var repo = Fresh(tag);
    e.Init(repo);
    Engine.Write(repo, "doc.md", Lines(20));
    Engine.Write(repo, "other.md", "keep\n");
    Engine.Write(repo, "notes/a.md", "# A\nbody\n");
    e.CommitAll(repo, "base");
    return repo;
}

// ---------- A. Worktrees ----------
foreach (var e in engines)
{
    Record("A1 worktree add + commit + list + prune", e, () =>
    {
        var repo = Base(e, "wt"); var wt = repo + "-wt";
        e.AddWorktree(repo, "agent/t1", wt);
        Engine.Write(wt, "agent.md", "from worktree\n");
        e.CommitAll(wt, "wt commit");
        var onBranch = Cli.Ok(repo, "log", "-1", "--format=%s", "agent/t1");
        var listed = Cli.Ok(repo, "worktree", "list", "--porcelain").Contains("agent/t1");
        var mainUntouched = !File.Exists(Path.Combine(repo, "agent.md")) && Clean(repo);
        var fsck1 = Fsck(repo);
        e.RemoveWorktree(repo, wt);
        var gone = !Directory.Exists(wt) && !Cli.Ok(repo, "worktree", "list", "--porcelain").Contains("agent/t1");
        var ok = onBranch == "wt commit" && listed && mainUntouched && gone && fsck1 == "fsck ok";
        return (ok, $"branch tip='{onBranch}' listed={listed} main_untouched={mainUntouched} removed={gone} {fsck1}");
    });

    Record("A2 worktree on an existing branch with prior commits", e, () =>
    {
        var repo = Base(e, "wt2");
        e.CreateBranch(repo, "agent/t2");
        var wt = repo + "-wt";
        e.AddWorktree(repo, "agent/t2", wt);
        var ok = File.Exists(Path.Combine(wt, "doc.md")) && Cli.Ok(wt, "rev-parse", "--abbrev-ref", "HEAD") == "agent/t2";
        return (ok, "checked out " + Cli.Ok(wt, "rev-parse", "--abbrev-ref", "HEAD"));
    });

    Record("A3 CLI-created worktree usable by this engine (and reverse)", e, () =>
    {
        var repo = Base(e, "wt3"); var wt = repo + "-clicreated";
        Cli.Ok(repo, "worktree", "add", "-b", "agent/x", wt);
        Engine.Write(wt, "x.md", "x\n");
        e.CommitAll(wt, "by engine in cli worktree");
        var wt2 = repo + "-engcreated";
        e.AddWorktree(repo, "agent/y", wt2);
        Engine.Write(wt2, "y.md", "y\n");
        Cli.Ok(wt2, "add", "-A"); Cli.Ok(wt2, "commit", "-q", "-m", "by cli in engine worktree");
        var ok = Cli.Ok(repo, "log", "-1", "--format=%s", "agent/x") == "by engine in cli worktree"
                 && Cli.Ok(repo, "log", "-1", "--format=%s", "agent/y") == "by cli in engine worktree";
        return (ok, Fsck(repo));
    });
}

Record("A4 sparse worktree API (paths param of AddWorktreeAsync)", engines[0], () =>
{
    var asm = typeof(Repository).Assembly;
    var sparse = asm.GetTypes().SelectMany(t => t.GetMembers()).Where(m => m.Name.Contains("Sparse", StringComparison.OrdinalIgnoreCase)).Select(m => m.Name).Distinct().ToArray();
    return (sparse.Length > 0, sparse.Length == 0 ? "no sparse-checkout API in LibGit2Sharp 0.31.0" : string.Join(",", sparse));
});
Record("A4 sparse worktree via sparse-checkout", engines[1], () =>
{
    var repo = Base(engines[1], "sp"); var wt = repo + "-wt";
    Cli.Ok(repo, "worktree", "add", "--no-checkout", "-b", "agent/sp", wt);
    Cli.Ok(wt, "sparse-checkout", "set", "--no-cone", "notes/");
    Cli.Ok(wt, "checkout", "-q", "agent/sp");
    var ok = File.Exists(Path.Combine(wt, "notes/a.md")) && !File.Exists(Path.Combine(wt, "doc.md"));
    return (ok, "only notes/ checked out");
});

// ---------- B. Merge ----------
// Each scenario builds identical repos per engine; clean merges compare the result tree with the CLI reference.
var pending = new Dictionary<(string, string), string>();
(string Name, Action<Engine, string> Prep, string Branch, bool ExpectClean, string[] ExpectConflicts)[] merges =
[
    ("B1 disjoint edits, same file", (e, r) =>
    {
        e.Checkout(r, "main"); e.CreateBranch(r, "side"); e.Checkout(r, "side");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 2\n", "line 2 SIDE\n")); e.CommitAll(r, "side");
        e.Checkout(r, "main");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 18\n", "line 18 MAIN\n")); e.CommitAll(r, "main");
    }, "side", true, []),
    ("B2 overlapping edits, same lines", (e, r) =>
    {
        e.CreateBranch(r, "side"); e.Checkout(r, "side");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 SIDE\n")); e.CommitAll(r, "side");
        e.Checkout(r, "main");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 MAIN\n")); e.CommitAll(r, "main");
    }, "side", false, ["doc.md"]),
    ("B3 rename on one side, edit on the other", (e, r) =>
    {
        e.CreateBranch(r, "side"); e.Checkout(r, "side");
        File.Move(Path.Combine(r, "doc.md"), Path.Combine(r, "doc-renamed.md")); e.CommitAll(r, "rename");
        e.Checkout(r, "main");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 10\n", "line 10 MAIN\n")); e.CommitAll(r, "edit");
    }, "side", true, []),
    ("B4 modify/delete conflict", (e, r) =>
    {
        e.CreateBranch(r, "side"); e.Checkout(r, "side");
        File.Delete(Path.Combine(r, "other.md")); e.CommitAll(r, "delete");
        e.Checkout(r, "main");
        Engine.Write(r, "other.md", "keep\nchanged\n"); e.CommitAll(r, "modify");
    }, "side", false, ["other.md"]),
    ("B5 two files, one clean one conflicting", (e, r) =>
    {
        e.CreateBranch(r, "side"); e.Checkout(r, "side");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 SIDE\n")); Engine.Write(r, "notes/a.md", "# A\nbody\nside add\n"); e.CommitAll(r, "side");
        e.Checkout(r, "main");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 MAIN\n")); e.CommitAll(r, "main");
    }, "side", false, ["doc.md"]),
    ("B6 *.md merge=union attribute honoured (design forbids it; engines must agree)", (e, r) =>
    {
        Engine.Write(r, ".gitattributes", "*.md merge=union\n"); e.CommitAll(r, "attrs");
        e.CreateBranch(r, "side"); e.Checkout(r, "side");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 SIDE\n")); e.CommitAll(r, "side");
        e.Checkout(r, "main");
        Engine.Write(r, "doc.md", Lines(20).Replace("line 5\n", "line 5 MAIN\n")); e.CommitAll(r, "main");
    }, "side", true, []),
];

foreach (var (name, prep, branch, expectClean, expectConflicts) in merges)
{
    foreach (var e in engines)
    {
        Record(name, e, () =>
        {
            var repo = Base(e, "mg");
            prep(e, repo);
            var headBefore = Cli.Ok(repo, "rev-parse", "HEAD");
            var res = e.Merge(repo, branch);
            if (res.Clean != expectClean) return (false, $"clean={res.Clean} expected {expectClean} conflicts=[{string.Join(",", res.ConflictPaths)}]");
            if (res.Clean)
            {
                var parents = Cli.Ok(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1;
                var tree = Tree(repo);
                var key = name;
                lock (pending) { pending[(key, e.Name)] = tree; }
                return (Clean(repo) && parents == 2 && Fsck(repo) == "fsck ok", $"clean merge parents={parents} tree={tree[..8]}");
            }
            var conflictOk = res.ConflictPaths.SequenceEqual(expectConflicts);
            var markers = res.ConflictPaths.All(p => !File.Exists(Path.Combine(repo, p)) || File.ReadAllText(Path.Combine(repo, p)).Contains("<<<<<<<") || p == "other.md");
            e.AbortMerge(repo);
            var restored = Clean(repo) && Cli.Ok(repo, "rev-parse", "HEAD") == headBefore && !File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD"));
            var branchKept = Cli.Run(repo, "rev-parse", "--verify", "-q", "refs/heads/" + branch).Exit == 0;
            return (conflictOk && markers && restored && branchKept && Fsck(repo) == "fsck ok",
                $"conflicts=[{string.Join(",", res.ConflictPaths)}] markers={markers} abort_restores_clean={restored} branch_kept={branchKept}");
        });
    }
}

// Cross-engine check: same result tree for every clean merge.
foreach (var grp in pending.GroupBy(k => k.Key.Item1))
{
    var trees = grp.Select(x => x.Value).Distinct().ToArray();
    results.Add(("B-tree " + grp.Key[..2] + " result tree equal across engines", "both", trees.Length == 1, 0, trees.Length == 1 ? trees[0][..8] : string.Join(" vs ", grp.Select(x => x.Key.Item2 + ":" + x.Value[..8]))));
}

// ---------- C. Concurrent writers ----------
const int Workers = 8, CommitsEach = 40;

void Commits(string label, Func<int, Engine> pick)
{
    var name = label;
    var sw = Stopwatch.StartNew();
    try
    {
        var e0 = engines[1];
        var repo = Base(e0, "cw");
        var wts = Enumerable.Range(0, Workers).Select(i => repo + $"-w{i}").ToArray();
        for (var i = 0; i < Workers; i++) pick(i).AddWorktree(repo, $"agent/w{i}", wts[i]);
        var errors = new ConcurrentBag<string>();
        Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
        {
            var eng = pick(i);
            for (var c = 0; c < CommitsEach; c++)
            {
                try { Engine.Write(wts[i], $"w{i}/f{c}.md", $"w{i} commit {c}\n"); eng.CommitAll(wts[i], $"w{i}-{c}"); }
                catch (Exception ex) { errors.Add($"{eng.Name} w{i}: {Trim(ex.Message)}"); }
            }
        });
        var counts = Enumerable.Range(0, Workers).Select(i => int.Parse(Cli.Ok(repo, "rev-list", "--count", $"agent/w{i}")) - 1).ToArray();
        var ok = counts.All(c => c == CommitsEach) && errors.IsEmpty;
        var fsck = Fsck(repo);
        results.Add((name, label.Contains("mixed") ? "mixed" : pick(0).Name, ok && fsck == "fsck ok", sw.ElapsedMilliseconds,
            $"commits/branch=[{string.Join(",", counts)}] errors={errors.Count}{(errors.IsEmpty ? "" : " e.g. " + errors.First())} {fsck}"));
    }
    catch (Exception ex) { results.Add((name, "?", false, sw.ElapsedMilliseconds, "EXC " + Trim(ex.Message))); }
}

Commits("C1 8 writers, own worktree+branch, LibGit2Sharp, 40 commits each", _ => engines[0]);
Commits("C1 8 writers, own worktree+branch, git CLI, 40 commits each", _ => engines[1]);
Commits("C2 8 writers, mixed engines (even=LibGit2Sharp, odd=CLI)", i => engines[i % 2]);

// C3: writers merge their branch into one shared main checkout WITHOUT an external lock (shows what a VaultWriter queue must prevent).
void SharedMain(Engine e, bool serialise)
{
    var label = $"C3 4 writers merge into one shared checkout, {(serialise ? "with" : "no")} external lock, 6 merges each";
    var sw = Stopwatch.StartNew();
    try
    {
        var repo = Base(e, "sm"); const int W = 4, M = 6;
        var gate = new object(); var failedAttempts = 0; var giveUps = 0; var abortFailures = 0;
        var wts = Enumerable.Range(0, W).Select(i => repo + $"-w{i}").ToArray();
        for (var i = 0; i < W; i++) e.AddWorktree(repo, $"agent/w{i}", wts[i]);
        Parallel.For(0, W, new ParallelOptions { MaxDegreeOfParallelism = W }, i =>
        {
            for (var m = 0; m < M; m++)
            {
                Engine.Write(wts[i], $"w{i}/f{m}.md", $"w{i} {m}\n"); e.CommitAll(wts[i], $"w{i}-{m}");
                var merged = false;
                for (var attempt = 0; attempt < 60 && !merged; attempt++)
                {
                    try
                    {
                        if (serialise) { lock (gate) { merged = e.Merge(repo, $"agent/w{i}").Clean; } }
                        else merged = e.Merge(repo, $"agent/w{i}").Clean;
                    }
                    catch { Interlocked.Increment(ref failedAttempts); }
                    if (!merged)
                    {
                        try { lock (serialise ? gate : new object()) { e.AbortMerge(repo); } } catch { Interlocked.Increment(ref abortFailures); }
                        Thread.Sleep(Random.Shared.Next(5, 40));
                    }
                }
                if (!merged) Interlocked.Increment(ref giveUps);
            }
        });
        var notReachable = Enumerable.Range(0, W).Count(i => Cli.Run(repo, "merge-base", "--is-ancestor", $"agent/w{i}", "main").Exit != 0);
        // reset the shared checkout to HEAD before judging content: any difference is state left behind by a failed run
        var dirty = Cli.Ok(repo, "status", "--porcelain").Length > 0;
        var missingFiles = Enumerable.Range(0, W).SelectMany(i => Enumerable.Range(0, M).Select(m => (i, m)))
            .Count(t => Cli.Run(repo, "cat-file", "-e", $"main:w{t.i}/f{t.m}.md").Exit != 0);
        var fsck = Fsck(repo);
        var ok = giveUps == 0 && notReachable == 0 && missingFiles == 0 && !dirty && fsck == "fsck ok";
        results.Add((label, e.Name, ok, sw.ElapsedMilliseconds,
            $"failed_attempts={failedAttempts} give_ups={giveUps} abort_failures={abortFailures} branches_not_in_main={notReachable} files_missing_from_main={missingFiles} dirty_checkout={dirty} {fsck}"));
    }
    catch (Exception ex) { results.Add((label, e.Name, false, sw.ElapsedMilliseconds, "EXC " + Trim(ex.Message))); }
}

foreach (var e in engines) { SharedMain(e, false); SharedMain(e, true); }

// C4: one LibGit2Sharp Repository instance shared by threads (child process, a native crash must not kill the run).
{
    var repo = Base(engines[1], "shr");
    for (var i = 0; i < 50; i++) { Engine.Write(repo, $"f{i}.md", $"{i}\n"); engines[1].CommitAll(repo, $"c{i}"); }
    var sw = Stopwatch.StartNew();
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    psi.ArgumentList.Add("shared-read"); psi.ArgumentList.Add(repo);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd(); var er = p.StandardError.ReadToEnd(); p.WaitForExit();
    results.Add(("C4 8 threads reading through ONE shared Repository instance", engines[0].Name, p.ExitCode == 0, sw.ElapsedMilliseconds, $"exit={p.ExitCode} {Trim(o)} {Trim(er)}"));
}

// ---------- D. Throughput and growth: 400 small commits in one worktree ----------
foreach (var e in engines)
{
    Record("D1 400 one-file commits in a worktree (ms total)", e, () =>
    {
        var repo = Base(e, "perf"); var wt = repo + "-wt";
        e.AddWorktree(repo, "agent/perf", wt);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 400; i++) { Engine.Write(wt, $"mem/{i % 40}.md", $"memory {i}\n" + new string('x', 400) + "\n"); e.CommitAll(wt, $"m{i}"); }
        var ms = sw.ElapsedMilliseconds;
        var size = Cli.Ok(repo, "count-objects", "-v").Split('\n').Where(l => l.StartsWith("size:") || l.StartsWith("count:")).Select(l => l.Trim());
        return (true, $"{ms} ms total, {ms / 400.0:F1} ms/commit; {string.Join(" ", size)}");
    });
}

// ---------- E. Packaging ----------
{
    var nb = Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "libgit2sharp.nativebinaries")).First();
    var rt = Path.Combine(nb, "runtimes");
    var parts = new[] { "win-x64", "linux-x64", "linux-arm64", "osx-arm64" }.Select(r =>
    {
        var f = Directory.GetFiles(Path.Combine(rt, r, "native")).FirstOrDefault();
        return f is null ? $"{r}=none" : $"{r}={new FileInfo(f).Length / 1024} KiB ({Path.GetFileName(f)})";
    });
    results.Add(("E1 native libgit2 binaries in the NuGet package", "LibGit2Sharp", true, 0,
        $"nativebinaries {Path.GetFileName(nb)}; {string.Join("; ", parts)}"));
}

Console.WriteLine($"git: {Cli.Ok(scratch, "--version")}; .NET {Environment.Version}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; scratch={scratch}");
foreach (var r in results)
    Console.WriteLine($"RESULT|{r.Scenario}|{r.Engine}|{(r.Ok ? "PASS" : "FAIL")}|{r.Ms}|{r.Detail}");
try { Directory.Delete(scratch, true); } catch { /* read-only pack files on Windows; scratch is disposable */ }
return 0;
