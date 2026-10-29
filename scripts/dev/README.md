# Local dev run

Starts Postgres (or uses `BAZAAR_DB`), runs `migrate`, then the server host with dev auth.
Needs .NET 11 SDK and openssl. Local Postgres needs passwordless `sudo -u postgres` (or root).

**Linux / macOS**

    scripts/dev/up.sh
    # or against your own database:
    BAZAAR_DB='Host=...;Database=...;Username=...;Password=...' scripts/dev/up.sh

Status: `http://127.0.0.1:7390/status`, readiness: `http://127.0.0.1:7390/health/ready`.

**Windows PowerShell** (uses your own Postgres; no script)

    $env:BAZAAR_DB = 'Host=127.0.0.1;Database=bazaar_dev;Username=bazaar;Password=...'
    $env:Bazaar__DevAuth__Enabled = 'true'
    $env:Bazaar__DevAuth__Token = [guid]::NewGuid().ToString('N')   # keep this value
    dotnet run --project src\Bazaar.Server.Host -- migrate --Bazaar:Host:DataRoot=$env:LOCALAPPDATA\bazaar-dev --Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB
    dotnet run --project src\Bazaar.Server.Host -- --Bazaar:Host:DataRoot=$env:LOCALAPPDATA\bazaar-dev --Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB

The token and password are never written to git. The dev token is saved by `up.sh` under `~/.local/share/bazaar-dev` (outside the repo).

## FreeLLMAPI smoke test

Checks your FreeLLMAPI endpoint, unified key and fallback trail without the Bazaar server. Needs only `curl` and `python3`. Flags: `--stream` (SSE chunks), `--tools` (tool-call round trip). Exit 0 = pass, 1 = fail, 3 = rate limited (429).

**Linux**

    export FREELLMAPI_KEY='freellmapi-...'        # never printed
    scripts/dev/freellmapi-smoke.sh --stream --tools
    # other endpoint or model:
    FREELLMAPI_URL=http://127.0.0.1:3001/v1 FREELLMAPI_MODEL=auto scripts/dev/freellmapi-smoke.sh

**Windows PowerShell** (from the repo root, through Git Bash or WSL `bash`)

    $env:FREELLMAPI_KEY = "freellmapi-..."
    bash scripts/dev/freellmapi-smoke.sh --stream --tools
