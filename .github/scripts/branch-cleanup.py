#!/usr/bin/env python3
"""Plan and run the branch cleanup. Uses `gh api` REST only.

  branch-cleanup.py run     (env: GITHUB_REPOSITORY, GH_TOKEN, DRY_RUN, STALE_DAYS, MAX_DELETES, ...)
  branch-cleanup.py --self-test

Rules (see docs/ci.md, "Branch cleanup"):
  KEEP: default branch, release/*, protected, the always-keep list, heads of open PRs,
        bases of open PRs, EXCLUDE_BRANCH_REGEX matches.
  DELETE: heads of merged PRs (tip unchanged since the merge); heads of closed-unmerged PRs and
        branches with no PR whose last commit is older than STALE_DAYS.
"""
import json, os, re, subprocess, sys, time, urllib.parse
from datetime import datetime, timedelta, timezone

ALWAYS_KEEP = {"claude/project-thread-2xmtpj"}


def plan(branches, prs, default, repo, stale_days, now, exclude_regex="", commit_dates=None):
    """branches: [{name, sha, protected}], prs: REST pull objects, commit_dates: {sha: aware datetime}.
    Returns (decisions, needs_date): decisions {name: (action, reason)}; needs_date = branches whose
    age is required but missing from commit_dates (caller fetches and calls again)."""
    commit_dates = commit_dates or {}
    rx = re.compile(exclude_regex) if exclude_regex else None
    open_heads, open_bases, merged, closed = set(), set(), {}, set()
    for p in prs:
        h = p.get("head") or {}
        if (h.get("repo") or {}).get("full_name") != repo:
            continue  # fork PR: its head branch is not ours
        ref = h.get("ref")
        if p["state"] == "open":
            open_heads.add(ref)
            open_bases.add((p.get("base") or {}).get("ref"))
        elif p.get("merged_at"):
            merged.setdefault(ref, set()).add(h.get("sha"))
        else:
            closed.add(ref)
    cutoff = now - timedelta(days=stale_days)
    out, needs = {}, []
    for b in branches:
        n, sha = b["name"], b["sha"]
        if n == default: out[n] = ("keep", "default branch")
        elif n.startswith("release/"): out[n] = ("keep", "release branch")
        elif b.get("protected"): out[n] = ("keep", "protected")
        elif n in ALWAYS_KEEP: out[n] = ("keep", "always-keep list")
        elif rx and rx.search(n): out[n] = ("keep", "EXCLUDE_BRANCH_REGEX")
        elif n in open_heads: out[n] = ("keep", "head of open PR")
        elif n in open_bases: out[n] = ("keep", "base of open PR (stacked work)")
        elif n in merged and sha in merged[n]: out[n] = ("delete", "head of merged PR")
        else:
            kind = "closed-unmerged PR" if n in closed else ("merged PR with newer commits" if n in merged else "no PR")
            d = commit_dates.get(sha)
            if d is None:
                needs.append(b)
            elif d < cutoff:
                out[n] = ("delete", f"{kind}, last commit older than {stale_days} days")
            else:
                out[n] = ("keep", f"{kind}, recent commit")
    return out, needs


def gh(*args, paginate=False, check=True):
    cmd = ["gh", "api"] + (["--paginate"] if paginate else []) + list(args)
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode and check:
        raise RuntimeError(f"{' '.join(cmd)}: {r.stderr.strip()}")
    return r


def pages(path):
    # --paginate concatenates JSON arrays; --slurp merges them into an array of pages.
    return [x for pg in json.loads(gh("--slurp", path, paginate=True).stdout) for x in pg]


def run():
    repo = os.environ["GITHUB_REPOSITORY"]
    stale_days = int(os.environ.get("STALE_DAYS") or 14)
    max_deletes = int(os.environ.get("MAX_DELETES") or 500)
    dry = (os.environ.get("DRY_RUN", "true") != "false")
    rx = os.environ.get("EXCLUDE_BRANCH_REGEX", "")
    default = json.loads(gh(f"repos/{repo}").stdout)["default_branch"]
    branches = [{"name": b["name"], "sha": b["commit"]["sha"], "protected": b.get("protected", False)}
                for b in pages(f"repos/{repo}/branches?per_page=100")]
    prs = pages(f"repos/{repo}/pulls?state=all&per_page=100")
    now = datetime.now(timezone.utc)
    dates = {}
    decisions, needs = plan(branches, prs, default, repo, stale_days, now, rx, dates)
    for b in needs:
        d = json.loads(gh(f"repos/{repo}/commits/{b['sha']}").stdout)["commit"]["committer"]["date"]
        dates[b["sha"]] = datetime.fromisoformat(d.replace("Z", "+00:00"))
    decisions, _ = plan(branches, prs, default, repo, stale_days, now, rx, dates)
    sha = {b["name"]: b["sha"] for b in branches}
    groups = {}
    for n, (a, why) in sorted(decisions.items()):
        groups.setdefault((a, why), []).append(n)
    todo = [n for n, (a, _) in sorted(decisions.items()) if a == "delete"]
    lines = [f"# Branch cleanup ({'DRY RUN' if dry else 'REAL RUN'})",
             f"{len(branches)} branches, {len(todo)} to delete (cap {max_deletes}), stale_days={stale_days}", "",
             "| Action | Reason | Count |", "|---|---|---|"]
    lines += [f"| {a} | {w} | {len(v)} |" for (a, w), v in sorted(groups.items())]
    report = ["branch\ttip_sha\taction\treason"] + [f"{n}\t{sha[n]}\t{a}\t{w}" for n, (a, w) in sorted(decisions.items())]
    lines += ["", "Restore a branch: `git push origin <sha>:refs/heads/<name>`", ""]
    for (a, w), v in sorted(groups.items()):
        lines += [f"<details><summary>{a}: {w} ({len(v)})</summary>", ""]
        lines += [f"- `{n}` `{sha[n]}`" for n in v] + ["", "</details>", ""]
    open("branch-cleanup-report.tsv", "w").write("\n".join(report) + "\n")
    summary = "\n".join(lines)
    print(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        open(os.environ["GITHUB_STEP_SUMMARY"], "a").write(summary + "\n")
    if dry:
        print("Dry run: nothing deleted.")
        return
    deleted = skipped = 0
    for i, n in enumerate(todo[:max_deletes]):
        r = gh("-X", "DELETE", f"repos/{repo}/git/refs/heads/{urllib.parse.quote(n, safe='')}", check=False)
        if r.returncode == 0:
            deleted += 1; print(f"deleted {n} {sha[n]}")
        elif "404" in r.stderr or "422" in r.stderr or "Not Found" in r.stderr or "Reference does not exist" in r.stderr:
            skipped += 1; print(f"skip (already gone) {n}")
        else:
            print(f"::warning::delete failed for {n}: {r.stderr.strip()}")
        if (i + 1) % 25 == 0: time.sleep(2)
    print(f"Deleted {deleted}, skipped {skipped}, remaining over cap {max(0, len(todo) - max_deletes)}")


def self_test():
    now = datetime(2026, 11, 19, tzinfo=timezone.utc)
    R = "o/r"
    def pr(ref, base, state, merged=False, sha=None, fork=False):
        return {"state": state, "merged_at": "x" if merged else None, "base": {"ref": base},
                "head": {"ref": ref, "sha": sha or ref, "repo": {"full_name": "f/r" if fork else R}}}
    br = lambda n, prot=False: {"name": n, "sha": n, "protected": prot}
    names = ["main", "release/1", "prot", "claude/project-thread-2xmtpj", "keepme-x", "openhead", "stack-base",
             "merged", "merged-newer", "closed-old", "closed-new", "nopr-old", "nopr-new", "forkhead"]
    prs = [pr("openhead", "stack-base", "open"), pr("merged", "main", "closed", True),
           pr("merged-newer", "main", "closed", True, sha="older"), pr("closed-old", "main", "closed"),
           pr("closed-new", "main", "closed"), pr("forkhead", "main", "open", fork=True)]
    old, new = now - timedelta(days=30), now - timedelta(days=1)
    dates = {"merged-newer": old, "closed-old": old, "closed-new": new, "nopr-old": old, "nopr-new": new, "forkhead": old}
    dec, needs = plan([br(n, n == "prot") for n in names], prs, "main", R, 14, now, "^keepme-", dates)
    want_del = {"merged", "merged-newer", "closed-old", "nopr-old", "forkhead"}
    got_del = {n for n, (a, _) in dec.items() if a == "delete"}
    assert not needs, needs
    assert got_del == want_del, (got_del ^ want_del)
    assert dec["stack-base"][0] == "keep" and dec["openhead"][0] == "keep" and dec["keepme-x"][0] == "keep"
    assert dec["closed-new"][0] == "keep" and dec["nopr-new"][0] == "keep"
    _, needs = plan([br("x")], [], "main", R, 14, now)
    assert [b["name"] for b in needs] == ["x"]
    print("self-test ok")


if __name__ == "__main__":
    if "--self-test" in sys.argv: self_test()
    elif sys.argv[1:] == ["run"]: run()
    else: sys.exit(__doc__)
