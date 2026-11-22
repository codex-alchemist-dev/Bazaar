# FreeLLMAPI fixtures

These are **synthetic**: written by hand from the shapes in FreeLLMAPI's REST API reference
(`docs/en/api/01-rest-api.md`, read 2026-10-26). No live FreeLLMAPI instance or provider key was used,
because the S17 live half is owner-run. Replace each file with a recording when that run happens, and
keep the same file names so the tests keep their meaning.

- `*.sse`: a streamed chat completion body. The routing headers for it are in the `*.headers` sidecar next to it.
- `*.headers`: one `Name: value` per line, the response headers the stub sends with the body.
- `*.json`: a non-stream body (`usage`, `models`, or an error body).

Every credential in these files is the placeholder `TEST-KEY-NOT-A-SECRET`.
