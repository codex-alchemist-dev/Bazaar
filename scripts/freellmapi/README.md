# FreeLLMAPI sidecar

Bazaar runs FreeLLMAPI v0.16.13 as a managed sidecar when `Bazaar:Gateway:FreeLlmApi:Sidecar:Managed` is true.

1. Build the pinned release: `scripts/freellmapi/install.sh` (needs git, Node 20 to 24, npm).
2. Copy `provider-keys.example.json` to `<state>/freellmapi/provider-keys.json` (the default state directory is `/var/lib/bazaar` on Linux and `%ProgramData%\Bazaar` on Windows). Paste the free-tier keys into the `key` fields, and delete entries you don't have a key for. Entries with an empty `key` are refused at start.
3. Start the host. It provisions the encryption key, admin login and unified key by itself.

Platform ids are FreeLLMAPI's own (see its `docs/en/providers/01-supported-platforms.md`). Keys are never logged. Do not commit a filled-in keys file.
