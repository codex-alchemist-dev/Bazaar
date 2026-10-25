using LibGit2Sharp;

namespace Spike.S23;

/// <summary>Child process: 8 threads walk history and diff through a single Repository instance (documented as not thread-safe).</summary>
public static class SharedRead
{
    public static int Run(string path)
    {
        using var repo = new Repository(path);
        var errors = 0; var wrong = 0;
        var expected = repo.Commits.Count();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    var n = repo.Commits.Count();
                    if (n != expected) Interlocked.Increment(ref wrong);
                    var tip = repo.Head.Tip;
                    _ = repo.Diff.Compare<TreeChanges>(tip.Parents.First().Tree, tip.Tree).Count();
                }
                catch { Interlocked.Increment(ref errors); }
            }
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Console.WriteLine($"errors={errors} wrong_counts={wrong}");
        return errors == 0 && wrong == 0 ? 0 : 1;
    }
}
