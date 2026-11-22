#!/usr/bin/env python3
"""Writes a solution file (.slnx) that lists only the given projects (CI slice, see build-affected.sh).

Usage: make-slice.py OUT.slnx PROJECT.csproj [PROJECT.csproj ...]

Android projects are skipped (the Android job builds them). Duplicates are dropped and the order is stable.
"""
import sys

out, *paths = sys.argv[1:]
seen = []
for p in paths:
    p = p.replace("\\", "/")
    if "Android" in p or p in seen:
        continue
    seen.append(p)
if not seen:
    sys.exit("make-slice.py: no projects to put in the slice")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write("<Solution>\n")
    for p in sorted(seen):
        f.write(f'  <Project Path="{p}" />\n')
    f.write("</Solution>\n")
