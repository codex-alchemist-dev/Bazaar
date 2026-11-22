#!/usr/bin/env python3
"""Works out which parts of the CI a pull request can affect, from the project reference graph.

Usage: affected.py CHANGED_FILES_FILE [--repo DIR]

CHANGED_FILES_FILE lists one repository-relative path per line. Prints GITHUB_OUTPUT-style lines:

  all=true|false        true: run everything (a shared file changed, or a changed file is outside every project)
  android=true|false    the Android head or its gate tests can be affected
  linux_client=true|false  the Linux client head can be affected
  projects=<space separated project paths, every affected project (tests included)>
  tests=<space separated test project paths>   test projects whose own project or a referenced project changed

Tests that read the repository layout always run.

Rules (conservative on purpose; anything unknown means "all"):
  * docs, Markdown, issue templates and other workflows cannot affect a build.
  * a changed file inside a project directory marks that project (longest directory match).
  * a changed file outside every project directory (Directory.*, global.json, build scripts, slnx, ...) means all.
  * a project is affected when it, or anything it references (transitively), changed.
"""
import os
import re
import sys

IGNORED = (
    re.compile(r"^docs/"),
    re.compile(r"^\.github/ISSUE_TEMPLATE/"),
    re.compile(r"\.md$"),
    re.compile(r"^\.github/(?!workflows/ci\.yml$|scripts/)"),  # other workflows, CODEOWNERS, dependabot
    re.compile(r"^renovate\.json$"),
    re.compile(r"^(AUTHORS|LICENSE|NOTICE)$"),
)
REF = re.compile(r'<ProjectReference\s+[^>]*Include="([^"]+)"', re.IGNORECASE)
ANDROID_TARGETS = ("src/Bazaar.Client.Android", "tests/Bazaar.Client.Android.Gate.Tests")
LINUX_TARGETS = ("src/Bazaar.Client.Linux",)


def main() -> int:
    args = sys.argv[1:]
    repo = "."
    if "--repo" in args:
        i = args.index("--repo")
        repo = args[i + 1]
        del args[i:i + 2]
    changed = [l.strip() for l in open(args[0], encoding="utf-8") if l.strip()]

    projects = {}  # dir -> list of referenced dirs
    is_test = {}
    for top in ("src", "tests"):
        for root, _dirs, files in os.walk(os.path.join(repo, top)):
            for f in files:
                if not f.endswith(".csproj"):
                    continue
                path = os.path.join(root, f)
                d = os.path.relpath(root, repo).replace(os.sep, "/")
                text = open(path, encoding="utf-8").read()
                refs = []
                for r in REF.findall(text):
                    target = os.path.normpath(os.path.join(root, r.replace("\\", "/")))
                    refs.append(os.path.relpath(os.path.dirname(target), repo).replace(os.sep, "/"))
                projects[d] = refs
                is_test[d] = bool(re.search(r'Include="(xunit|TUnit)', text)) and top == "tests"

    # Tests that read the repository layout (solution file, source tree) can break on any change, so they always run.
    readers = {"tests/Bazaar.Architecture.Tests"}
    layout = re.compile(r"Bazaar\.slnx|RepoRoot\(|FindRepoRoot")
    for d in projects:
        if is_test[d] and d not in readers:
            for root, _dirs, files in os.walk(os.path.join(repo, d)):
                if any(f.endswith(".cs") and layout.search(open(os.path.join(root, f), encoding="utf-8", errors="ignore").read())
                       for f in files):
                    readers.add(d)
                    break

    # Only projects in the solution are built and tested by the main CI job (ExitCheck, benches and others sit outside it).
    in_sln = set(re.findall(r'Path="([^"]+)/[^/"]+\.csproj"', open(os.path.join(repo, "Bazaar.slnx"), encoding="utf-8").read()))
    for d in list(projects):
        if d not in in_sln:
            is_test[d] = False

    dirs_longest_first = sorted(projects, key=len, reverse=True)
    all_ = False
    hit = set()
    for f in changed:
        if any(p.search(f) for p in IGNORED):
            continue
        owner = next((d for d in dirs_longest_first if f.startswith(d + "/")), None)
        if owner is None:
            all_ = True
            break
        hit.add(owner)

    if all_:
        print("all=true\nandroid=true\nlinux_client=true\nprojects=\ntests=")
        return 0

    closure_cache = {}

    def closure(d):
        if d in closure_cache:
            return closure_cache[d]
        seen, stack = set(), [d]
        while stack:
            cur = stack.pop()
            if cur in seen:
                continue
            seen.add(cur)
            stack.extend(projects.get(cur, []))
        closure_cache[d] = seen
        return seen

    def affected(d):
        return bool(closure(d) & hit)

    android = any(affected(t) for t in ANDROID_TARGETS if t in projects)
    linux = any(affected(t) for t in LINUX_TARGETS if t in projects)
    tests = sorted({d for d in projects if is_test[d] and affected(d)} | {d for d in readers if d in in_sln})
    every = sorted({d for d in projects if d in in_sln and affected(d)} | {d for d in readers if d in in_sln})
    print("all=false")
    print("projects=" + " ".join(f"{t}/{os.path.basename(t)}.csproj" for t in every))
    print(f"android={'true' if android else 'false'}")
    print(f"linux_client={'true' if linux else 'false'}")
    print("tests=" + " ".join(f"{t}/{os.path.basename(t)}.csproj" for t in tests))
    return 0


if __name__ == "__main__":
    sys.exit(main())
