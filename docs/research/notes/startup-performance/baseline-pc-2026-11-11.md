# Client cold/warm start baseline, PC, 2026-11-11

Scope: `Bazaar.Client.Windows`, win-x64, self-contained, Release. Two variants: ReadyToRun on (default on this branch) and ReadyToRun off (`-p:PublishReadyToRun=false`). Supports doc 40 (startup design). Nothing here is a Bazaar target; these are the first local numbers.

## Hardware and OS

| Item | Value |
|---|---|
| CPU | Intel(R) Core(TM) Ultra 9 285K, 24 cores |
| RAM | 95.3 GiB (97,560 MB) |
| OS | Microsoft Windows 11 Pro, build 26300 |
| .NET SDK | 11.0.100-rc.2.26504.105 (matches `global.json`) |

## Publish

Each variant was published after deleting `src/**/obj/Release` and `src/**/bin/Release`, so the time includes compiling the four Release projects. Times are wall clock from `Measure-Command`.

| Variant | Publish time | Folder size | Files |
|---|---|---|---|
| R2R on | 9.6 s | 226.8 MB | 241 |
| R2R off | 2.3 s | 211.1 MB | 241 |

R2R on makes `Avalonia.Base.dll` 7.7 MB, against 2.4 MB with R2R off.

## Startup

Metric: milliseconds from `Start-Process` to the first poll where `MainWindowHandle` is non-zero and `IsWindowVisible` returns true. This is a proxy for "window visible". It does not measure first paint or first usable frame.

Each variant had 10 cold launches and 10 warm launches, each after a warm-up launch that was not recorded.

- **Cold:** each launch followed a 10 s gap. The Windows file cache was **not** cleared, so these are not true cold starts. Treat them as "after idle, OS cache warm".
- **Warm:** back-to-back launches.

| Variant | Phase | n | Median (ms) | p90 (ms) | Min (ms) | Max (ms) |
|---|---|---|---|---|---|---|
| R2R on | cold | 10 | 279.6 | 281.2 | 265.8 | 282.1 |
| R2R on | warm | 10 | 279.4 | 293.5 | 265.0 | 296.9 |
| R2R off | cold | 10 | 436.0 | 450.2 | 432.9 | 498.3 |
| R2R off | warm | 10 | 436.3 | 441.7 | 431.6 | 450.5 |

Warm-up launch: R2R on 311 ms; R2R off 1894 ms. The R2R off warm-up is an outlier, probably first-run work on a freshly published folder. It was not recorded.

No launch failed and none exited early.

## Idle memory

One process, launched fresh, sampled 10 s after launch, then closed.

| Variant | Working set (MB) | Peak working set (MB) |
|---|---|---|
| R2R on | 121.4 | 121.4 |
| R2R off | 132.7 | 132.7 |

## Server

The client did not need a server for this run. No `BAZAAR_SERVER`, `BAZAAR_GC` or `BAZAAR_TOKEN` was set, so `Program.cs` builds the plain `MainWindow` with no chat connection. I did not check whether the Constellation tile source starts any network work in the background. No server was running.

## Could not measure

- True cold start (file cache cleared). Clearing needs elevation or a tool such as RAMMap; not done.
- First paint or first usable frame. The proxy only reports that a visible top-level window exists.
- Any behaviour with a real chat or server connection.
- Other machines. This is one PC.

## Commands

Publish script (run from the worktree root):

```bash
dotnet publish src/Bazaar.Client.Windows/Bazaar.Client.Windows.csproj -c Release -r win-x64 --self-contained true -o <OUT>/r2r-on
dotnet publish src/Bazaar.Client.Windows/Bazaar.Client.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o <OUT>/r2r-off
```

Each was preceded by deleting `src/**/obj/Release` and `src/**/bin/Release`, and timed with `Measure-Command`. `<OUT>` was a scratch folder outside the repo.

Harness, run once per variant:

```powershell
.\startup-harness.ps1 -Exe <OUT>\r2r-on\Bazaar.Client.Windows.exe -Label r2r-on -OutJson <OUT>\result-r2r-on.json
.\startup-harness.ps1 -Exe <OUT>\r2r-off\Bazaar.Client.Windows.exe -Label r2r-off -OutJson <OUT>\result-r2r-off.json
```

Defaults: `-Cold 10 -Warm 10 -GapSeconds 10`. The harness is reproduced below.

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
