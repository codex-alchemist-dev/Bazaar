#!/usr/bin/env bash
# Validates the Flatpak packaging metadata without flatpak-builder (doc 30 section 3, doc 37).
# Checks: manifest parses, app-id matches the .desktop and metainfo ids, the metainfo release matches
# VersionPrefix in Directory.Build.props, and finish-args stay within the reviewed minimal set.
# Uses python3 -I with the standard library only; PyYAML is used when present, otherwise a line-based parse.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
command -v python3 >/dev/null 2>&1 || { echo "test-flatpak-metadata: python3 is required" >&2; exit 1; }

exec python3 -I - "$here" "$repo" <<'PY'
import re, sys, os
import xml.etree.ElementTree as ET

here, repo = sys.argv[1], sys.argv[2]
errors = []
def err(m): errors.append(m)

def read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()

text = read(os.path.join(here, "TheBazaarAuthors.BazaarClient.yml"))

# Parse: PyYAML when available, else a line-based reader for the few keys we check.
data = None
try:
    if os.environ.get("BAZAAR_FLATPAK_NO_YAML"):  # force the line-based path (used to test it)
        raise ImportError
    import yaml  # not stdlib; optional
    data = yaml.safe_load(text)
except ImportError:
    pass
except Exception as e:
    err("manifest does not parse as YAML: %s" % e)

if data is None and not errors:
    data = {"finish-args": [], "sdk-extensions": [], "modules": []}
    cur = None
    for ln in text.splitlines():
        if not ln.strip() or ln.lstrip().startswith("#"):
            continue
        if re.match(r"^\S", ln):
            m = re.match(r"^([A-Za-z0-9_-]+):\s*(.*?)\s*(#.*)?$", ln)
            if not m:
                err("manifest line is not 'key: value': %r" % ln); continue
            cur = m.group(1); val = m.group(2).strip("'\"")
            if val: data[cur] = val
            elif cur not in data: data[cur] = None
            if cur == "modules": data["modules"] = ["(line-based: present)"]
        elif cur in ("finish-args", "sdk-extensions"):
            m = re.match(r"^\s+-\s+(\S+)", ln)
            if m: data[cur].append(m.group(1).strip("'\""))

if not isinstance(data, dict):
    err("manifest top level is not a mapping"); data = {}

app_id = str(data.get("app-id", ""))
for key in ("app-id", "runtime", "runtime-version", "sdk", "command", "finish-args", "modules"):
    if not data.get(key):
        err("manifest is missing '%s'" % key)

if app_id and not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_-]*)+", app_id):
    err("app-id is not a valid Flatpak id: %s" % app_id)

desktop_path = os.path.join(here, app_id + ".desktop")
if not os.path.exists(desktop_path):
    err("no %s.desktop next to the manifest (file name must equal app-id)" % app_id)
else:
    desktop = read(desktop_path)
    m = re.search(r"^Icon=(.+)$", desktop, re.M)
    if not m or m.group(1).strip() != app_id: err(".desktop Icon must equal app-id")
    m = re.search(r"^Exec=(\S+)", desktop, re.M)
    if not m or m.group(1) != str(data.get("command")): err(".desktop Exec must equal manifest command")

meta_path = os.path.join(here, app_id + ".metainfo.xml")
if not os.path.exists(meta_path):
    err("no %s.metainfo.xml next to the manifest" % app_id)
else:
    try:
        root = ET.parse(meta_path).getroot()
        mid = (root.findtext("id") or "").strip()
        if mid != app_id: err("metainfo <id> %r != app-id %r" % (mid, app_id))
        lid = (root.findtext("launchable") or "").strip()
        if lid != app_id + ".desktop": err("metainfo <launchable> %r != %s.desktop" % (lid, app_id))
        rels = root.findall("./releases/release")
        if not rels:
            err("metainfo has no <release>")
        else:
            props = read(os.path.join(repo, "Directory.Build.props"))
            m = re.search(r"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>", props)
            if not m:
                err("VersionPrefix not found in Directory.Build.props")
            else:
                versions = [r.get("version") for r in rels]
                if m.group(1) not in versions:
                    err("metainfo release versions %s do not include VersionPrefix %s" % (versions, m.group(1)))
    except ET.ParseError as e:
        err("metainfo is not well-formed XML: %s" % e)

# finish-args: the reviewed minimal set. Each entry is a deliberate sandbox hole (doc 30 section 3:
# "Terminal reach into the host needs sandbox holes"). The terminal gets none by default, so anything
# beyond this list fails until a doc change and an owner decision add it. Each must also be explained
# by a comment in the manifest (checked below by its keyword).
ALLOWED = {
    "--socket=wayland":      "Wayland",
    "--socket=fallback-x11": "X11",
    "--share=ipc":           "ipc",
    "--device=dri":          "GPU",
    "--share=network":       "network",
}
args = data.get("finish-args") or []
for a in args:
    if a not in ALLOWED:
        err("finish-arg %r is not in the reviewed minimal set (doc 30 section 3); host reach for the terminal needs an owner decision" % a)
    if re.match(r"--(filesystem=(host|home|/)|socket=(session-bus|system-bus|ssh-auth|pcsc|cups|gpg-agent)|device=all|talk-name=|system-talk-name=|allow=devel)", a):
        err("forbidden host-escape finish-arg: %s" % a)
if len(args) != len(set(args)):
    err("duplicate finish-args")
if "--socket=wayland" in args and "--socket=fallback-x11" not in args:
    err("fallback-x11 missing: Wayland-only would break X11 desktops")
# Comment check: the block of comments directly above each hole must mention its keyword.
lines = text.splitlines()
for a, kw in ALLOWED.items():
    idx = [i for i, l in enumerate(lines) if l.strip() == "- " + a]
    if not idx: continue
    i = idx[0] - 1; found = False
    while i >= 0 and lines[i].strip().startswith("#"):
        if kw.lower() in lines[i].lower(): found = True
        i -= 1
    if not found:
        err("finish-arg %s has no explanatory comment mentioning '%s'" % (a, kw))

# Icon: the manifest must install a scalable SVG named after the app-id, from a file that exists.
m = re.search(r"install\s+-Dm644\s+(\S+\.svg)\s+/app/share/icons/hicolor/scalable/apps/(\S+)\.svg", text)
if not m:
    err("manifest does not install a hicolor scalable SVG icon")
else:
    if m.group(2) != app_id: err("icon install name %r != app-id %r" % (m.group(2), app_id))
    if not os.path.isfile(os.path.join(repo, m.group(1))): err("icon source missing: %s" % m.group(1))

# generated-sources.json: offline NuGet sources (regenerate with generate-sources.sh).
import json, shutil, subprocess, tempfile
gs_path = os.path.join(here, "generated-sources.json")
try:
    gs = json.loads(read(gs_path))
except (OSError, ValueError) as e:
    gs = None; err("generated-sources.json is not valid JSON: %s" % e)
if gs is not None:
    if not isinstance(gs, list) or not gs:
        err("generated-sources.json is empty (run generate-sources.sh)")
    else:
        names = [e.get("dest-filename") for e in gs if isinstance(e, dict)]
        if len(names) != len(gs) or not all(isinstance(n, str) and n.endswith(".nupkg") for n in names):
            err("generated-sources.json entries need a .nupkg dest-filename")
        if len(names) != len(set(names)):
            err("generated-sources.json has duplicate dest-filename values")
        if names != sorted(names): err("generated-sources.json is not sorted by dest-filename")
        for e in gs:
            if not isinstance(e, dict): continue
            n = e.get("dest-filename")
            if e.get("type") != "file": err("%s: type must be 'file'" % n)
            if not str(e.get("url", "")).startswith("https://"): err("%s: url must be https" % n)
            if not re.fullmatch(r"[0-9a-f]{128}", str(e.get("sha512", ""))): err("%s: sha512 must be 128 hex chars" % n)
            if e.get("dest") != "nuget-sources": err("%s: dest must be nuget-sources (the manifest's publish --source)" % n)
    # Determinism: regenerate and compare, only when dotnet is available (needs network for restore).
    if shutil.which("dotnet") and not os.environ.get("BAZAAR_FLATPAK_SKIP_REGEN"):
        tmpd = tempfile.mkdtemp()
        try:
            regen = os.path.join(tmpd, "regen.json")
            r = subprocess.run([os.path.join(here, "generate-sources.sh"), regen], capture_output=True, text=True)
            if r.returncode != 0:
                err("generate-sources.sh failed: %s" % r.stderr.strip()[-300:])
            elif read(regen) != read(gs_path):
                err("generated-sources.json is stale: regenerate it with generate-sources.sh")
        finally:
            shutil.rmtree(tmpd, ignore_errors=True)
    else:
        print("note: dotnet not available (or regeneration skipped); determinism check skipped")

if errors:
    for e in errors: print("FAIL: " + e, file=sys.stderr)
    sys.exit(1)
print("ok: app-id %s, %d finish-args, metadata consistent" % (app_id, len(args)))
PY
