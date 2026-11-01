#!/usr/bin/env bash
# FreeLLMAPI smoke test: checks the endpoint, unified key, fallback trail, streaming and tool calls
# before the Bazaar server is involved. Needs curl and python3 (jq not required).
#   export FREELLMAPI_KEY='freellmapi-...'          (required, never printed)
#   FREELLMAPI_URL=http://127.0.0.1:3001/v1         (default)
#   FREELLMAPI_MODEL=auto                           (default)
#   scripts/dev/freellmapi-smoke.sh [--stream] [--tools]
# Exit: 0 every requested step PASSed, 1 a step FAILed, 2 usage or missing key, 3 rate limited (429).
set -euo pipefail

BASE="${FREELLMAPI_URL:-http://127.0.0.1:3001/v1}"
BASE="${BASE%/}"
MODEL="${FREELLMAPI_MODEL:-auto}"
RUN_STREAM=0
RUN_TOOLS=0
for arg in "$@"; do
  case "$arg" in
    --stream) RUN_STREAM=1 ;;
    --tools) RUN_TOOLS=1 ;;
    -h | --help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

command -v curl >/dev/null || { echo "curl is required" >&2; exit 2; }
command -v python3 >/dev/null || { echo "python3 is required" >&2; exit 2; }

if [ -z "${FREELLMAPI_KEY:-}" ]; then
  echo "FREELLMAPI_KEY is not set. Export your unified key, then re-run:" >&2
  echo "  export FREELLMAPI_KEY='freellmapi-...'" >&2
  exit 2
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# The key lives in a 0600 header file, so it never appears on a command line (ps).
(umask 077 && printf 'Authorization: Bearer %s\n' "$FREELLMAPI_KEY" > "$WORK/auth.hdr")

FINAL=0 # 0 ok, 1 failed, 3 rate limited

pass() { echo "[PASS] $1"; }
fail() { echo "[FAIL] $1"; FINAL=1; }
limited() {
  echo "[RATE LIMITED] $1: FreeLLMAPI returned 429 (keys exhausted for now; not a crash). Retry-After: ${2:-not sent}"
  if [ "$FINAL" -eq 0 ]; then FINAL=3; fi
}

# scrub TEXT: keep error output short, single-line, and free of the key.
scrub() {
  local s="$1"
  s="${s//"$FREELLMAPI_KEY"/<redacted>}"
  s="${s:0:300}"
  printf '%s' "${s//$'\n'/ }"
}

# jget FILE EXPR: evaluate a Python expression over the parsed JSON as `d`; prints "" on any error.
jget() {
  python3 -I -c '
import json, sys
try:
    d = json.load(open(sys.argv[1]))
    r = eval(sys.argv[2])
    print("" if r is None else r)
except Exception:
    print("")
' "$1" "$2"
}

# hdr FILE NAME: one response header value (case-insensitive), CR stripped.
hdr() {
  { grep -i "^$2:" "$1" 2>/dev/null || true; } | tail -n 1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//; s/ *$//'
}

# call METHOD PATH BODYFILE NAME: sets STATUS, and the BODY, HEAD and ERR files under $WORK/NAME.*
call() {
  local method="$1" path="$2" body="$3" name="$4"
  local -a args=()
  if [ -n "$body" ]; then args=(-H 'Content-Type: application/json' --data-binary "@$body"); fi
  STATUS="$(curl -sS -X "$method" -H "@$WORK/auth.hdr" -D "$WORK/$name.head" -o "$WORK/$name.body" \
    -w '%{http_code}' --max-time 120 ${args[@]+"${args[@]}"} "$BASE$path" 2>"$WORK/$name.err")" || STATUS=000
  BODY="$WORK/$name.body"
  HEAD="$WORK/$name.head"
  ERR="$WORK/$name.err"
}

# problem: short, key-free description of the last failed call.
problem() {
  if [ "$STATUS" = 000 ]; then scrub "$(cat "$ERR" 2>/dev/null)"; else scrub "$(cat "$BODY" 2>/dev/null)"; fi
}

# expect_200 NAME: 0 when the last call returned 200; otherwise reports FAIL or RATE LIMITED.
expect_200() {
  if [ "$STATUS" = 200 ]; then return 0; fi
  if [ "$STATUS" = 429 ]; then
    limited "$1" "$(hdr "$HEAD" Retry-After)"
  else
    fail "$1: HTTP $STATUS $(problem)"
  fi
  return 1
}

step_models() {
  echo "== models: GET /models"
  call GET /models "" models
  if ! expect_200 models; then return; fi
  local n
  n="$(jget "$BODY" 'len(d["data"])')"
  if [ -z "$n" ] || [ "$n" -eq 0 ]; then fail "models: response listed no models"; return; fi
  echo "  model count: $n (first 10 ids:)"
  jget "$BODY" 'chr(10).join("    " + m["id"] for m in d["data"][:10])'
  pass "models"
}

step_chat() {
  echo "== chat: POST /chat/completions (model $MODEL, max_tokens 32)"
  python3 -I -c 'import json, sys
print(json.dumps({"model": sys.argv[1], "messages": [{"role": "user", "content": "Reply with one word: pong"}], "max_tokens": 32}))' "$MODEL" > "$WORK/chat.json"
  call POST /chat/completions "$WORK/chat.json" chat
  if ! expect_200 chat; then return; fi
  local served trail content
  served="$(jget "$BODY" 'd["model"]')"
  trail="$(hdr "$HEAD" X-Fallback-Trail)"
  echo "  served model: ${served:-unknown}"
  if [ -n "$trail" ]; then echo "  X-Fallback-Trail: $trail"; fi
  content="$(jget "$BODY" 'str(d["choices"][0]["message"].get("content"))[:80]')"
  if [ -z "$content" ]; then fail "chat: response had no choices[0].message"; return; fi
  echo "  reply: $content"
  pass "chat"
}

step_stream() {
  echo "== stream: POST /chat/completions with stream: true"
  python3 -I -c 'import json, sys
print(json.dumps({"model": sys.argv[1], "messages": [{"role": "user", "content": "Reply with one word: pong"}], "max_tokens": 32, "stream": True}))' "$MODEL" > "$WORK/stream.json"
  call POST /chat/completions "$WORK/stream.json" stream
  if ! expect_200 stream; then return; fi
  local deltas done_seen
  deltas="$(grep -c '"delta"' "$BODY" || true)"
  done_seen="no"
  if grep -q '^data: \[DONE\]' "$BODY"; then done_seen="yes"; fi
  echo "  delta chunks: $deltas (data: [DONE] seen: $done_seen)"
  if [ "$deltas" -lt 1 ]; then fail "stream: no delta chunks"; return; fi
  pass "stream"
}

step_tools() {
  echo "== tools: tool-call round trip with get_time"
  local tools='[{"type":"function","function":{"name":"get_time","description":"Returns the current UTC time.","parameters":{"type":"object","properties":{},"required":[]}}}]'
  python3 -I -c 'import json, sys
print(json.dumps({"model": sys.argv[1], "messages": [{"role": "user", "content": "Call the get_time tool now. Do not answer in text."}], "tools": json.loads(sys.argv[2]), "max_tokens": 64}))' "$MODEL" "$tools" > "$WORK/tools1.json"
  call POST /chat/completions "$WORK/tools1.json" tools1
  if ! expect_200 tools; then return; fi
  local calls name
  calls="$(jget "$BODY" 'len(d["choices"][0]["message"].get("tool_calls") or [])')"
  if [ -z "$calls" ] || [ "$calls" -eq 0 ]; then
    fail "tools: no tool call came back (model replied: $(jget "$BODY" 'str(d["choices"][0]["message"].get("content"))[:80]'))"
    return
  fi
  name="$(jget "$BODY" 'd["choices"][0]["message"]["tool_calls"][0]["function"]["name"]')"
  echo "  tool call came back: $name (x$calls)"
  # Send the tool result back and expect a final answer.
  if ! python3 -I -c 'import json, sys
r = json.load(open(sys.argv[1]))
m = r["choices"][0]["message"]
c = m["tool_calls"][0]
msgs = [{"role": "user", "content": "Call the get_time tool now. Do not answer in text."},
        {"role": "assistant", "content": m.get("content"), "tool_calls": m["tool_calls"]},
        {"role": "tool", "tool_call_id": c["id"], "content": "12:00 UTC"}]
print(json.dumps({"model": sys.argv[2], "messages": msgs, "tools": json.loads(sys.argv[3]), "max_tokens": 64}))' \
    "$BODY" "$MODEL" "$tools" > "$WORK/tools2.json" 2>/dev/null; then
    fail "tools: could not build the tool-result follow-up from the response"
    return
  fi
  call POST /chat/completions "$WORK/tools2.json" tools2
  if ! expect_200 "tools (follow-up)"; then return; fi
  echo "  follow-up reply: $(jget "$BODY" 'str(d["choices"][0]["message"].get("content"))[:80]')"
  pass "tools"
}

step_models
echo
step_chat
echo
if [ "$RUN_STREAM" -eq 1 ]; then echo; step_stream; fi
if [ "$RUN_TOOLS" -eq 1 ]; then echo; step_tools; fi

echo
case "$FINAL" in
  0) echo "RESULT: all requested steps PASSED" ;;
  3) echo "RESULT: RATE LIMITED (429). Not a crash; retry after the Retry-After delay." ;;
  *) echo "RESULT: FAILED" ;;
esac
exit "$FINAL"
