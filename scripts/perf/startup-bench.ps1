<#
.SYNOPSIS
  Startup benchmark for the Bazaar Windows client (doc 40). Publishes R2R and IL variants and, only when asked,
  times cold and warm launches. Written to run later on the owner's PC; it has not been run yet.

.DESCRIPTION
  Safe by default: with no switches it only publishes both variants, with no visible console and no window.
  Launch timing shows a client window, so it needs -MeasureLaunches and -Desktop2, and it moves each window to
  Windows virtual desktop 2 with the VirtualDesktop module. It never activates or focuses a window, and it closes
  each process after its measurement. If the module is missing, launch timing is skipped, not forced.

  The VirtualDesktop cmdlet names below are from the module's documentation as remembered. Check them with
  Get-Command -Module VirtualDesktop before the first real run and fix the two calls marked VERIFY.

.PARAMETER Repo
  Path to the worktree that holds the repo. Never the shared checkout.

.PARAMETER Out
  Results file (Markdown). Appended per run.

.PARAMETER Launches
  Launches per variant for cold and for warm (after one warm-up).

.PARAMETER MeasureLaunches
  Enables window timing. Requires -Desktop2.

.PARAMETER Desktop2
  Confirms the owner accepts windows only on virtual desktop 2.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Repo,
    [string] $Out = (Join-Path $PWD 'startup-bench-results.md'),
    [int] $Launches = 10,
    [switch] $MeasureLaunches,
    [switch] $Desktop2
)

$ErrorActionPreference = 'Stop'
$client = Join-Path $Repo 'src\Bazaar.Client.Windows'
$publishRoot = Join-Path $Repo 'artifacts\startup-bench'
$variants = @{
    'R2R' = @()
    'IL'  = @('-p:PublishReadyToRun=false')
}

function Invoke-Quiet([string] $FilePath, [string[]] $ArgumentList) {
    # Runs a tool with no window and no console output on screen. Output goes to a log file.
    $log = Join-Path $publishRoot ("{0}.log" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
    $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if ($p.ExitCode -ne 0) { throw "$FilePath exited with $($p.ExitCode); see $log" }
}

# 1. Publish both variants. No window, no console.
foreach ($name in $variants.Keys) {
    $dest = Join-Path $publishRoot $name
    $args = @('publish', $client, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $dest) + $variants[$name]
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Quiet 'dotnet' $args
    $sw.Stop()
    $size = (Get-ChildItem $dest -Recurse | Measure-Object Length -Sum).Sum / 1MB
    "{0}: publish {1:N1} s, {2:N1} MB" -f $name, $sw.Elapsed.TotalSeconds, $size | Tee-Object -FilePath $Out -Append
}

if (-not $MeasureLaunches) {
    "Launch timing skipped (no -MeasureLaunches). Publish only." | Tee-Object -FilePath $Out -Append
    return
}
if (-not $Desktop2) {
    throw 'Launch timing shows windows. Add -Desktop2 to confirm windows go only to virtual desktop 2.'
}
if (-not (Get-Module -ListAvailable -Name VirtualDesktop)) {
    "Launch timing skipped: the VirtualDesktop module is not installed. Install it, then re-run." | Tee-Object -FilePath $Out -Append
    return
}
Import-Module VirtualDesktop

function Measure-Launch([string] $Exe) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $Exe -WindowStyle Minimized -PassThru
    $hwnd = $null
    while ($sw.Elapsed.TotalSeconds -lt 60 -and -not $hwnd) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { $hwnd = $proc.MainWindowHandle }
        else { Start-Sleep -Milliseconds 5 }
    }
    $elapsed = $sw.Elapsed.TotalMilliseconds
    if ($hwnd) {
        # VERIFY: cmdlet name and parameters for moving a window to a desktop.
        Move-Window -Desktop (Get-Desktop -Index 1) -Hwnd $hwnd | Out-Null
    }
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(5000) | Out-Null }
    if (-not $proc.HasExited) { $proc.Kill() }
    return $elapsed
}

# 2. Time cold launches (one launch per fresh start) and warm launches after one warm-up.
foreach ($name in $variants.Keys) {
    $exe = Join-Path $publishRoot "$name\Bazaar.Client.Windows.exe"
    $cold = 1..$Launches | ForEach-Object { Measure-Launch $exe; Start-Sleep -Seconds 2 }
    $null = Measure-Launch $exe
    $warm = 1..$Launches | ForEach-Object { Measure-Launch $exe; Start-Sleep -Seconds 2 }
    $stat = { param($xs) $s = $xs | Sort-Object; "median {0:N0} ms, min {1:N0}, max {2:N0}" -f $s[[int]($s.Count / 2)], $s[0], $s[-1] }
    "{0} cold: {1}" -f $name, (& $stat $cold) | Tee-Object -FilePath $Out -Append
    "{0} warm: {1}" -f $name, (& $stat $warm) | Tee-Object -FilePath $Out -Append
}
