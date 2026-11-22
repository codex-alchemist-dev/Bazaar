<#
Real-CLI check for the Claude Code provider (docs/design/91 section 15, "Unverified until a real binary is run").

Runs the unmodified `claude` program once, with the same arguments the provider uses (ClaudeCodeArguments.Build, login mode),
one short prompt, one turn, no tools. It answers three questions:
  1. Does `--safe-mode` work together with `--strict-mcp-config --mcp-config`?
  2. Which tools does the init event list? (It must list no CLI tool, only mcp__bazaar__*.)
  3. Does the stream carry a rate-limit event, and what does a usage-limit message say? (Only seen if the plan is limited now.)

Safety: it never reads the login folder (it only points CLAUDE_CONFIG_DIR at it), never prints environment values, and writes
only inside -WorkDir. -WorkDir is required and must not be on C: (Windows). Nothing is installed or changed.

Usage (PowerShell 7):
  ./scripts/dev/claude-code-real-cli-check.ps1 -WorkDir V:\TOOL\claude-check -ConfigDir <the folder you signed in to> [-Binary <path to claude>] [-Model claude-haiku-5-5]
#>
param(
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [Parameter(Mandatory = $true)][string]$ConfigDir,
    [string]$Binary = 'claude',
    [string]$Model = 'claude-haiku-5-5'
)

$ErrorActionPreference = 'Stop'

if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    if ($WorkDir -match '^[cC]:') {
        throw 'WorkDir must not be on C:.'
    }
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$work = (Resolve-Path $WorkDir).Path
if (($IsWindows -or $env:OS -eq 'Windows_NT') -and $work -match '^[cC]:') { throw 'WorkDir must not be on C:.' }
if (-not (Test-Path -LiteralPath $ConfigDir -PathType Container)) { throw "ConfigDir does not exist: $ConfigDir" }

$systemPrompt = Join-Path $work 'system.txt'
$mcpConfig = Join-Path $work 'mcp.json'
$out = Join-Path $work 'stdout.jsonl'
$err = Join-Path $work 'stderr.txt'
Set-Content -LiteralPath $systemPrompt -Value 'You are a test. Answer in one short sentence.' -Encoding utf8NoBOM
# A definition-only server entry; the check does not start it. Strict mode must accept the file.
Set-Content -LiteralPath $mcpConfig -Value '{"mcpServers":{"bazaar":{"type":"http","url":"http://127.0.0.1:9/mcp"}}}' -Encoding utf8NoBOM

Write-Host "== version"
& $Binary --version

$args = @(
    '-p', '--output-format', 'stream-json', '--verbose', '--include-partial-messages', '--no-session-persistence',
    "--model=$Model", '--system-prompt-file', $systemPrompt, '--tools', '', '--strict-mcp-config', '--mcp-config', $mcpConfig,
    '--max-turns', '1', '--permission-mode', 'dontAsk', '--permission-prompts', 'none', '--safe-mode'
)

$env:CLAUDE_CONFIG_DIR = $ConfigDir
# Keep temp files off C: too.
$env:TEMP = $work
$env:TMP = $work
$env:TMPDIR = $work
Write-Host "== run (one prompt, one turn)"
'Say hello.' | & $Binary @args 1> $out 2> $err
$code = $LASTEXITCODE
Write-Host "exit code: $code"

Write-Host "== event types seen"
$events = Get-Content -LiteralPath $out | Where-Object { $_.Trim().StartsWith('{') } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $_ }
$events | Group-Object { "$($_.type)/$($_.subtype)" } | ForEach-Object { "{0,4}  {1}" -f $_.Count, $_.Name }

Write-Host "== tools listed by the init event (expect only mcp__bazaar__*, or none)"
$init = $events | Where-Object { $_.type -eq 'system' -and $_.subtype -eq 'init' } | Select-Object -First 1
if ($init) { ($init.tools | ForEach-Object { $_ }) -join ', ' } else { '(no init event)' }

Write-Host "== rate-limit or usage-limit text (only present if limited now)"
$limit = $events | Where-Object { $_.type -match 'rate' -or ($_ | ConvertTo-Json -Depth 6 -Compress) -match 'usage limit|rate.?limit' } | Select-Object -First 3
if ($limit) { $limit | ConvertTo-Json -Depth 6 -Compress } else { '(none)' }

Write-Host "== result"
$result = $events | Where-Object { $_.type -eq 'result' } | Select-Object -First 1
if ($result) { "is_error=$($result.is_error) subtype=$($result.subtype)" } else { '(no result event)' }

Write-Host "== stderr (first 20 lines)"
Get-Content -LiteralPath $err -TotalCount 20

Write-Host "Files kept in $work : stdout.jsonl and stderr.txt (they hold no login data). Paste the output above back to the thread."
