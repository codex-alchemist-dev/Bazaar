#!/usr/bin/env python3
# Fake FreeLLMAPI for testing scripts/dev/freellmapi-smoke.sh. Not a gateway; no real provider calls.
#   python3 -I scripts/dev/fake-freellmapi/server.py [port]    (default 3001, binds 127.0.0.1)
#   Accepts the key FAKE_KEY (default freellmapi-fake-test-key).
#   FAKE_MODE=429 makes chat return 429 with Retry-After: 7.
import json, os, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY = "Bearer " + os.environ.get("FAKE_KEY", "freellmapi-fake-test-key")
MODELS = ["auto", "auto:smart", "auto:fast", "groq/llama-3.3-70b", "cerebras/llama3.1-8b",
          "mistral/mistral-small", "openrouter/free-model-a", "google/gemini-flash",
          "nvidia/nemotron", "cohere/command-r", "github/gpt-4o-mini", "zhipu/glm-4"]
SERVED = "groq/llama-3.3-70b"
TRAIL = [("X-Fallback-Trail", "openrouter/free-model-a->groq/llama-3.3-70b")]

class Handler(BaseHTTPRequestHandler):
    def send_json(self, code, obj, headers=()):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for k, v in headers:
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.headers.get("Authorization") != KEY:
            return self.send_json(401, {"error": "bad key"})
        if self.path != "/v1/models":
            return self.send_json(404, {"error": "not found"})
        self.send_json(200, {"object": "list", "data": [{"id": m, "object": "model"} for m in MODELS]})

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if self.headers.get("Authorization") != KEY:
            return self.send_json(401, {"error": "bad key"})
        if self.path != "/v1/chat/completions":
            return self.send_json(404, {"error": "not found"})
        if os.environ.get("FAKE_MODE") == "429":
            return self.send_json(429, {"error": "all keys exhausted"}, [("Retry-After", "7")])
        if req.get("tools") and not any(m.get("role") == "tool" for m in req["messages"]):
            call = {"id": "call_1", "type": "function", "function": {"name": "get_time", "arguments": "{}"}}
            msg = {"role": "assistant", "content": None, "tool_calls": [call]}
            return self.send_json(200, {"model": SERVED, "choices": [{"index": 0, "message": msg}]}, TRAIL)
        text = "It is 12:00 UTC." if any(m.get("role") == "tool" for m in req["messages"]) else "pong"
        if req.get("stream"):
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            for k, v in TRAIL:
                self.send_header(k, v)
            self.end_headers()
            for chunk in [{"delta": {"content": text}}, {"delta": {}, "finish_reason": "stop"}]:
                line = {"model": SERVED, "choices": [dict(index=0, **chunk)]}
                self.wfile.write(("data: " + json.dumps(line) + "\n\n").encode())
            self.wfile.write(b"data: [DONE]\n\n")
            return
        msg = {"role": "assistant", "content": text}
        self.send_json(200, {"model": SERVED, "choices": [{"index": 0, "message": msg}]}, TRAIL)

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 3001
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
