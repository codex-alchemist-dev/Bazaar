# Client start-up and GC/tiering baseline, PC, 2026-11-12

Scope: `Bazaar.Client.Windows` from `origin/main` (includes PR #420, R2R and preloader work), win-x64, self-contained, Release. Supports doc 40. Measures three client variants and skips the server part (no load script exists, see below).

## Hardware and OS

| Item | Value |
|---|---|
| CPU | Intel(R) Core(TM) Ultra 9 285K, 24 cores |
| RAM | 95.3 GiB (97,560 MB) |
| OS | Microsoft Windows 11 Pro, build 26300 |
| .NET SDK | 11.0.100 (GA, from `global.json`), installed to `%USERPROFILE%\.dotnet` with dotnet-install |

## Variants

| Name | Build | Change from default |
|---|---|---|
| `r2r-on` | default publish | none. `System.GC.Concurrent=true`, `System.Runtime.TieredPGO=true` |
| `r2r-off` | `-p:PublishReadyToRun=false` | no R2R |
| `gc-off` | copy of `r2r-on` | runtimeconfig: `System.GC.Concurrent=false`, `System.Runtime.TieredPGO=false` |

`gc-off` changes both settings at once, so this run cannot say which one causes any difference.

## Publish

| Variant | Publish time | Notes |
|---|---|---|
| `r2r-on` | 22.7 s | clean worktree, first publish |
| `r2r-off` | 1.5 s | not comparable: reused the `r2r-on` obj/bin output |
| `gc-off` | n/a | copy, no publish |

Publish times are not a fair comparison for this run. Folder sizes were not recorded for this run.

## Startup

Metric: milliseconds from `Start-Process` to the first poll where `MainWindowHandle` is non-zero and `IsWindowVisible` returns true. This is a proxy, not first paint.

Each variant had 10 cold launches (10 s gap before each) and 10 warm launches, after one unrecorded warm-up launch. **The Windows file cache was not cleared**, so the "cold" rows are not true cold starts.

| Variant | Phase | n | Median (ms) | p90 (ms) | Min (ms) | Max (ms) |
|---|---|---|---|---|---|---|
| `r2r-on` | cold | 10 | 278.4 | 294.3 | 263.5 | 294.7 |
| `r2r-on` | warm | 10 | 279.9 | 293.4 | 267.1 | 296.0 |
| `r2r-off` | cold | 10 | 468.0 | 513.3 | 430.0 | 527.6 |
| `r2r-off` | warm | 10 | 422.6 | 445.4 | 416.5 | 447.1 |
| `gc-off` | cold | 10 | 264.3 | 280.6 | 248.4 | 295.2 |
| `gc-off` | warm | 10 | 264.5 | 266.4 | 250.1 | 278.7 |

Warm-up launch (not recorded): `r2r-on` 467.9 ms, `r2r-off` 2328.4 ms, `gc-off` 1293.7 ms. Warm-up cost is mostly first-run work on a fresh folder.

No launch failed and none exited early.

## Idle memory

One process, launched fresh, sampled 10 s after launch, then closed.

| Variant | Working set (MB) | Peak working set (MB) |
|---|---|---|
| `r2r-on` | 117.3 | 117.3 |
| `r2r-off` | 129.2 | 129.2 |
| `gc-off` | 116.9 | 116.9 |

## Server (not measured)

Skipped. Searched `scripts/` and `tests/` for a fixed synthetic load script for `Bazaar.Server.Host`; none exists. The host also needs managed PostgreSQL. So the server GC/DATAS comparison has no workload. Server GC on versus off is not measured.

## Reading the numbers

- `r2r-on` and `gc-off` differ by about 14 ms at the median, roughly 5%. Their ranges overlap only slightly. This is one PC and one run, so treat it as a lead, not a result.
- `r2r-off` is about 150 ms slower at the median than `r2r-on`, in line with the earlier baseline on `claude/project-thread-j5o2c2` (see `baseline-pc-2026-11-11.md`).

## Could not measure

- True cold start with the file cache cleared.
- First paint or first usable frame.
- Server GC with DATAS versus Server GC off. No load script.
- Any run with a live server or chat connection (not configured for these runs).
- Other machines.

## Commands

Publish (repo root, SDK 11.0.100 first on PATH):

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet publish src\Bazaar.Client.Windows\Bazaar.Client.Windows.csproj -c Release -r win-x64 --self-contained true -o <OUT>\r2r-on
dotnet publish src\Bazaar.Client.Windows\Bazaar.Client.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o <OUT>\r2r-off
Copy-Item <OUT>\r2r-on <OUT>\gc-off -Recurse
# then edit <OUT>\gc-off\Bazaar.Client.Windows.runtimeconfig.json:
#   "System.GC.Concurrent": false, "System.Runtime.TieredPGO": false
```

Harness, once per variant (defaults `-Cold 10 -Warm 10 -GapSeconds 10`):

```powershell
.\startup-harness.ps1 -Exe <OUT>\<variant>\Bazaar.Client.Windows.exe -Label <variant> -OutJson <OUT>\result-<variant>.json
```

`<OUT>` is a scratch folder outside the repo. The harness is reproduced below.

<details>
<summary>startup-harness.ps1</summary>

```powershell
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$Label,
  [string]$OutJson,
  [int]$Cold = 10,
  [int]$Warm = 10,
  [int]$GapSeconds = 10
)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Vis {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@

$name = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
$workDir = Split-Path $Exe -Parent

function Stop-AllBazaar {
  Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
    try { $null = $_.CloseMainWindow() } catch {}
  }
  Start-Sleep -Milliseconds 500
  Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
}

function Measure-Launch {
  Stop-AllBazaar
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Exe -WorkingDirectory $workDir -PassThru
  $ms = $null
  $deadline = 60000
  while ($sw.ElapsedMilliseconds -lt $deadline) {
    if ($p.HasExited) { break }
    $p.Refresh()
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero -and [Win32Vis]::IsWindowVisible($h)) { $ms = $sw.Elapsed.TotalMilliseconds; break }
    Start-Sleep -Milliseconds 5
  }
  $exited = $p.HasExited
  $result = [pscustomobject]@{ ms = $ms; exited_early = $exited; exit_code = $(if ($exited) { $p.ExitCode } else { $null }) }
  if (-not $exited) {
    # Settle, then sample memory of the warm process before closing.
    Start-Sleep -Seconds 5
    $p.Refresh()
    $result | Add-Member -NotePropertyName ws_mb -NotePropertyValue ([math]::Round($p.WorkingSet64/1MB,1))
    $result | Add-Member -NotePropertyName peak_ws_mb -NotePropertyValue ([math]::Round($p.PeakWorkingSet64/1MB,1))
    try { $null = $p.CloseMainWindow() } catch {}
    if (-not $p.WaitForExit(5000)) { $p.Kill(); $p.WaitForExit() }
  }
  return $result
}

function Pct($arr, $q) {
  $s = @($arr | Sort-Object)
  if ($s.Count -eq 0) { return $null }
  $idx = [math]::Ceiling($q * $s.Count) - 1
  if ($idx -lt 0) { $idx = 0 }
  return [math]::Round([double]$s[$idx], 1)
}

$hostInfo = [pscustomobject]@{
  cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
  ram_gb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,1)
  os = (Get-CimInstance Win32_OperatingSystem).Caption + ' build ' + (Get-CimInstance Win32_OperatingSystem).BuildNumber
}

Write-Host "[$Label] warm-up launch (not recorded)"
$warmup = Measure-Launch

$coldRuns = @()
for ($i = 1; $i -le $Cold; $i++) {
  Start-Sleep -Seconds $GapSeconds
  $r = Measure-Launch
  $coldRuns += $r
  Write-Host ("[$Label] cold {0}: {1} ms" -f $i, $(if ($r.ms) { [math]::Round($r.ms,1) } else { 'FAIL' }))
}

$warmRuns = @()
for ($i = 1; $i -le $Warm; $i++) {
  $r = Measure-Launch
  $warmRuns += $r
  Write-Host ("[$Label] warm {0}: {1} ms" -f $i, $(if ($r.ms) { [math]::Round($r.ms,1) } else { 'FAIL' }))
}

# One warm idle process: launch, let it settle 10 s, sample memory, close.
Stop-AllBazaar
$p = Start-Process -FilePath $Exe -WorkingDirectory $workDir -PassThru
Start-Sleep -Seconds 10
$p.Refresh()
$idle = [pscustomobject]@{ ws_mb = [math]::Round($p.WorkingSet64/1MB,1); peak_ws_mb = [math]::Round($p.PeakWorkingSet64/1MB,1) }
try { $null = $p.CloseMainWindow() } catch {}
if (-not $p.WaitForExit(5000)) { $p.Kill(); $p.WaitForExit() }

$coldOk = @($coldRuns | Where-Object { $_.ms -ne $null } | ForEach-Object { $_.ms })
$warmOk = @($warmRuns | Where-Object { $_.ms -ne $null } | ForEach-Object { $_.ms })

$summary = [pscustomobject]@{
  label = $Label
  exe = $Exe
  host = $hostInfo
  warmup_ms = $(if ($warmup.ms) { [math]::Round($warmup.ms,1) } else { $null })
  cold = [pscustomobject]@{
    n = $coldOk.Count; failures = $Cold - $coldOk.Count; values = $coldRuns.ms
    median = $(if ($coldOk) { Pct $coldOk 0.5 } else { $null }); p90 = $(if ($coldOk) { Pct $coldOk 0.9 } else { $null })
    min = $(if ($coldOk) { [math]::Round(($coldOk | Measure-Object -Minimum).Minimum,1) } else { $null })
    max = $(if ($coldOk) { [math]::Round(($coldOk | Measure-Object -Maximum).Maximum,1) } else { $null })
  }
  warm = [pscustomobject]@{
    n = $warmOk.Count; failures = $Warm - $warmOk.Count; values = $warmRuns.ms
    median = $(if ($warmOk) { Pct $warmOk 0.5 } else { $null }); p90 = $(if ($warmOk) { Pct $warmOk 0.9 } else { $null })
    min = $(if ($warmOk) { [math]::Round(($warmOk | Measure-Object -Minimum).Minimum,1) } else { $null })
    max = $(if ($warmOk) { [math]::Round(($warmOk | Measure-Object -Maximum).Maximum,1) } else { $null })
  }
  idle_memory = $idle
  launch_exits = @($coldRuns + $warmRuns | Where-Object { $_.exited_early } | ForEach-Object { $_.exit_code })
}

if ($OutJson) { $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $OutJson -Encoding UTF8 }
$summary | ConvertTo-Json -Depth 6
```

</details>
