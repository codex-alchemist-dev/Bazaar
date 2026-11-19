# Spike S2 driver: one full run (steps 2-10). Usage: run.ps1 -Run 1 [-Persist aof|none]
# Password is generated per run and held only in env S2_GARNET_PASSWORD; never written to disk.
param([int]$Run = 1, [string]$Persist = 'aof')
$ErrorActionPreference = 'Stop'
$root = 'V:\TOOL\garnet'
$exe  = "$root\2.2.13\bin\net10.0\GarnetServer.exe"
$data = "$root\scratch\run$Run"
$port = '16379'
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'results') | Out-Null
$out  = Join-Path $PSScriptRoot "results\run$Run$(if($Persist -ne 'aof'){'-control-' + $Persist}).json"
$env:NUGET_PACKAGES = 'V:\TOOL\nuget'; $env:DOTNET_CLI_HOME = 'V:\TOOL\dotnet-home'
$env:S2_GARNET_PORT = $port
$bytes = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:S2_GARNET_PASSWORD = [Convert]::ToBase64String($bytes).Replace('+','a').Replace('/','b').Replace('=','')

if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory $data | Out-Null
$app = Join-Path $PSScriptRoot 'bin\Release\net11.0\S2.dll'

$flags = @('--bind','127.0.0.1','--port',$port,'--auth','Password','--password',$env:S2_GARNET_PASSWORD,'-c',"$data\ckpt")
if ($Persist -eq 'aof') { $flags += @('--aof','--aof-commit-wait') }
$display = (($flags + @('-r')) -join ' ').Replace($env:S2_GARNET_PASSWORD,'<redacted>')

function Start-Garnet($withRecover) {
  $a = $flags; if ($withRecover) { $a = $a + @('-r') }
  Start-Process -FilePath $exe -ArgumentList $a -PassThru -WindowStyle Hidden -RedirectStandardOutput "$data\garnet.out.txt" -RedirectStandardError "$data\garnet.err.txt"
}
$rec = [ordered]@{ run = $Run; persistence = $Persist; flagsRedacted = $display; startedUtc = (Get-Date).ToUniversalTime().ToString('o') }
$rec.garnetExeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash

# step 3
$t0 = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$p1 = Start-Garnet $false
$rec.step3 = [ordered]@{ pid = $p1.Id; connect = (dotnet $app connect $t0 | Out-String | ConvertFrom-Json) }
# steps 4-6
$rec.write = (dotnet $app write | Out-String | ConvertFrom-Json)
# step 7 hard kill
$killUtc = (Get-Date).ToUniversalTime().ToString('o')
taskkill /F /PID $p1.Id | Out-Null
Start-Sleep -Milliseconds 500
$rec.step7 = [ordered]@{ pid = $p1.Id; killedUtc = $killUtc; method = 'taskkill /F /PID'; stillRunning = [bool](Get-Process -Id $p1.Id -ErrorAction SilentlyContinue) }
$rec.dataFiles = @(Get-ChildItem $data -Recurse -File | ForEach-Object { "$($_.FullName.Substring($data.Length)) $($_.Length)" })
# step 8 restart with same command line (+ -r) and data folder
$t0 = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$p2 = Start-Garnet $true
$rec.step8 = [ordered]@{ pid = $p2.Id; connect = (dotnet $app connect $t0 | Out-String | ConvertFrom-Json) }
# steps 9-10
$rec.verify = (dotnet $app verify | Out-String | ConvertFrom-Json)
$rec.endedUtc = (Get-Date).ToUniversalTime().ToString('o')
Stop-Process -Id $p2.Id -Force
$rec | ConvertTo-Json -Depth 12 | Out-File $out -Encoding utf8
Write-Host "wrote $out"
