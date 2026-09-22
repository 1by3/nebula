<#
.SYNOPSIS
  The real-worker (tier D) layer of the Nebula scale and failure suite — docs/scale-suite.md.

.DESCRIPTION
  Starts a real mesh from a player build through the `nebula` CLI, drives it with the load-test client
  (Services~/Nebula.LoadGen), injects the failure the scenario asks for by stopping real processes, scrapes the
  orchestrator HTTP API and the workers' `[nebula] profile` lines into CSV, and asserts the thresholds in
  docs/scale-suite.md.

  It never reimplements mesh startup: `nebula build` / `nebula start` / `nebula stop` do that (docs/cli.md).

  Every artifact and every line of output is stamped `unity`, and the CSVs land in Logs/scale/unity-*.csv beside
  the synthetic layer's Logs/scale/synthetic-*.csv. The two layers measure different things and must never be
  read as one series.

  Modes:
    -DryRun      print the plan and check the preconditions (CLI on PATH, player build, Editor lock, ports); run
                 nothing. Safe anywhere.
    -Synthetic   run the synthetic layer instead (dotnet test --filter TestCategory=Scale) and report its timings.
                 This is what runs where no player build can be produced.
    (neither)    the real run.

.EXAMPLE
  pwsh Tools/scale-suite.ps1 -DryRun
  pwsh Tools/scale-suite.ps1 -Synthetic
  pwsh Tools/scale-suite.ps1 -Scenario sustained -Clients 300 -Seconds 120
  pwsh Tools/scale-suite.ps1 -Scenario worker-kill -Workers 4 -Clients 200
  pwsh Tools/scale-suite.ps1 -Scenario all -Build
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'sustained', 'burst', 'worker-kill', 'gateway-kill', 'mesh-restart')]
    [string]$Scenario = 'all',
    [int]$Workers = 4,
    [int]$Clients = 100,
    [int]$Seconds = 60,
    [int]$RampSeconds = 10,
    [int]$DashboardPort = 7080,
    [int]$GatewayPort = 7000,
    [string]$KillWorker = 'w2',
    [int]$KillAfterSeconds = 30,
    [switch]$Build,
    [switch]$DryRun,
    [switch]$Synthetic,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot
# Not $build: PowerShell variable names are case-insensitive and that is the -Build switch.
$buildDir = Join-Path $repo 'Builds\Win64'
$meshLog = Join-Path $buildDir 'Logs'
$out     = Join-Path $repo 'Logs\scale'
$layer   = 'unity'
$loadGen = Join-Path $repo 'Packages\com.1by3.nebula\Services~\Nebula.LoadGen'

New-Item -ItemType Directory -Force -Path $out | Out-Null

function Say($text, $color = 'Gray') { Write-Host "[scale:$layer] $text" -ForegroundColor $color }
function Fail($text) { Write-Host "[scale:$layer] FAIL $text" -ForegroundColor Red; $script:failures += $text }
function Pass($text) { Write-Host "[scale:$layer] ok   $text" -ForegroundColor Green }
$script:failures = @()

# ---------------------------------------------------------------------------------------------- thresholds
# Kept in step with ScaleThresholds in Services~/Nebula.Services.Tests/Fixtures/ScaleHarness.cs and with the
# table in docs/scale-suite.md. Where a number is provisional it says so there, not here.
$T = @{
    RestoreSecondsPerContainer = 2.0
    GatewayReclaimSeconds      = 10.0
    ControlPlaneStallSeconds   = 5.0
    WorkerTickMsMax            = 16.7
    BytesPerClientPerSecond    = 512 * 1024
}

# -------------------------------------------------------------------------------------------- preconditions

function Test-Preconditions {
    $problems = @()
    if (-not (Get-Command nebula -ErrorAction SilentlyContinue)) {
        $problems += "the 'nebula' CLI is not on PATH (see docs/cli.md; `dotnet tool` install or cli/install)"
    }
    $exe = Join-Path $buildDir 'Nebula.exe'
    if (-not (Test-Path $exe)) {
        $problems += "there is no player build at $exe. Run 'nebula build' (or this script with -Build) first"
    }
    $lock = Join-Path $repo 'Temp\UnityLockfile'
    if (Test-Path $lock) {
        # Not fatal: the CLI mirrors the project to a scratch directory and builds there (cli/Nebula.Cli/Core/Unity.cs).
        Say "the Unity Editor has this project open; 'nebula build' will build from a mirrored copy, which is slower" 'Yellow'
    }
    if (-not (Test-Path (Join-Path $loadGen 'Nebula.LoadGen.csproj'))) {
        $problems += "the load-test client is missing at $loadGen"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $problems += "dotnet is not on PATH" }
    return $problems
}

function Test-Port($port) {
    try { (Test-NetConnection -ComputerName localhost -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue) } catch { $false }
}

# ------------------------------------------------------------------------------------------------ scraping

function Get-State {
    try { Invoke-RestMethod -Uri "http://localhost:$DashboardPort/api/state" -TimeoutSec 3 } catch { $null }
}
function Get-Cost {
    try { Invoke-RestMethod -Uri "http://localhost:$DashboardPort/api/cost" -TimeoutSec 3 } catch { $null }
}

<#
  One row per sample of /api/state and /api/cost. The columns are the orchestrator's own field names so a reader
  can go straight from the CSV to the API (docs/cli.md, the orchestrator dashboard guide).
#>
function New-Csv($name, $header) {
    $path = Join-Path $out "$layer-$name.csv"
    Set-Content -Path $path -Value $header -Encoding utf8
    return $path
}
function Add-Csv($path, $row) { Add-Content -Path $path -Value $row -Encoding utf8 }

function Sample-State($path, $scenario, $t) {
    $s = Get-State
    if (-not $s) { Add-Csv $path "$layer,$scenario,$t,,,,,,,,,,unreachable"; return $null }
    $cost = Get-Cost
    $hottest = if ($cost -and $cost.containers) { $cost.containers[0] } else { $null }
    $tick = if ($s.workers) { ($s.workers | Measure-Object -Property tickMs -Maximum).Maximum } else { 0 }
    $dirty = if ($s.workers) { ($s.workers | Measure-Object -Property oldestDirtySeconds -Maximum).Maximum } else { 0 }
    $util = if ($s.workers) { ($s.workers | Measure-Object -Property utilization -Maximum).Maximum } else { 0 }
    Add-Csv $path ("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}" -f $layer, $scenario, $t,
        $s.desiredWorkers, $s.totals.liveWorkers, $s.totals.players, $s.totals.bots, $s.totals.containers,
        $tick, $util, $dirty,
        $(if ($hottest) { "$($hottest.id):$($hottest.dominant):$([math]::Round($hottest.saturation,3))" } else { '' }),
        "$($s.scale.action)/$($s.scale.blockedBy)")
    return $s
}

<#
  The `[nebula] profile` line each worker prints every 5 s (Runtime/Worker/NebulaWorker.cs, ReportProfile):
  `profile <ticks> ticks/5s <frames> frames avg <x>ms max <y>ms dup .. skip .. gc .. auth <n> ghosts <n> ...`
#>
function Export-Profiles($scenario) {
    $path = New-Csv "$scenario-profile" 'layer,scenario,worker,sample,avgMs,maxMs,ticks,authoritative,ghosts'
    if (-not (Test-Path $meshLog)) { return $path }
    Get-ChildItem $meshLog -Filter 'w*.log' | Sort-Object Name | ForEach-Object {
        $worker = $_.BaseName
        $n = 0
        Select-String -Path $_.FullName -Pattern 'profile (\d+) ticks/\d+s .* avg ([\d.]+)ms max ([\d.]+)ms .* auth (\d+) ghosts (\d+)' |
            ForEach-Object {
                $m = $_.Matches[0].Groups
                $n++
                Add-Csv $path ("{0},{1},{2},{3},{4},{5},{6},{7},{8}" -f $layer, $scenario, $worker, $n,
                    $m[2].Value, $m[3].Value, $m[1].Value, $m[4].Value, $m[5].Value)
            }
        Say "${worker}: $n profile samples"
    }
    return $path
}

# ------------------------------------------------------------------------------------------- mesh lifecycle

function Start-Mesh($workers) {
    Say "nebula stop (clearing any mesh already running)"
    & nebula stop --yes 2>&1 | Out-Null
    if (Test-Path $meshLog) { Remove-Item (Join-Path $meshLog '*.log') -ErrorAction SilentlyContinue }
    Say "nebula start --workers $workers --bots 0"
    & nebula start --workers $workers --bots 0 --yes
    if ($LASTEXITCODE -ne 0) { throw "nebula start failed with exit code $LASTEXITCODE" }
    for ($i = 0; $i -lt 60; $i++) {
        $s = Get-State
        if ($s -and $null -ne $s.desiredWorkers -and $s.workers) { Say "mesh up: $($s.totals.liveWorkers) worker(s)"; return $s }
        Start-Sleep -Seconds 1
    }
    throw "the orchestrator dashboard on $DashboardPort never became ready"
}

function Stop-Mesh {
    if ($KeepRunning) { Say "leaving the mesh running (-KeepRunning)"; return }
    & nebula stop --yes 2>&1 | Out-Null
}

<#
  The load-test client, in the background, writing its own per-second CSV. Its columns are its own
  (Services~/Nebula.LoadGen/Program.cs); this script copies the file into Logs/scale under the unity- prefix
  rather than reformatting it, so the two are diffable.
#>
function Start-Load($scenario, $clients, $seconds, $ramp) {
    $csv = Join-Path $out "$layer-$scenario-loadgen.csv"
    $args = @('run', '--project', $loadGen, '-c', 'Release', '--',
        '--gateway', "127.0.0.1:$GatewayPort", '--clients', $clients, '--seconds', $seconds,
        '--ramp', $ramp, '--bot', '--name-prefix', "scale-$scenario", '--csv', $csv)
    Say "load: $clients clients for $seconds s (ramp $ramp s) -> $csv"
    return @{ Process = (Start-Process dotnet -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $out "$layer-$scenario-loadgen.log")); Csv = $csv }
}

function Stop-Load($load) {
    if (-not $load.Process.HasExited) { $load.Process.WaitForExit(30000) | Out-Null }
    if (-not $load.Process.HasExited) { $load.Process.Kill() }
    Say "load finished with exit code $($load.Process.ExitCode) (0 = no session changes and no pawn losses)"
    return $load.Process.ExitCode
}

function Stop-OneWorker($workerId) {
    $target = Get-CimInstance Win32_Process -Filter "name='Nebula.exe'" |
        Where-Object { $_.CommandLine -match "-nebula-worker-id $workerId\b" } | Select-Object -First 1
    if (-not $target) { Fail "could not find the process of worker $workerId to kill"; return $false }
    Stop-Process -Id $target.ProcessId -Force
    Say "killed worker $workerId (pid $($target.ProcessId))" 'Yellow'
    return $true
}

# ------------------------------------------------------------------------------------------------ scenarios

$StateHeader = 'layer,scenario,t,desiredWorkers,liveWorkers,players,bots,containers,tickMsMax,utilizationMax,oldestDirtySecondsMax,hottestContainer,scale'

function Invoke-Sustained {
    $name = if ($Scenario -eq 'burst') { 'burst' } else { 'sustained' }
    $ramp = if ($name -eq 'burst') { 0 } else { $RampSeconds }
    Start-Mesh $Workers | Out-Null
    $csv = New-Csv "$name-state" $StateHeader
    $load = Start-Load $name $Clients $Seconds $ramp
    for ($t = 0; $t -lt $Seconds; $t += 5) { Start-Sleep -Seconds 5; Sample-State $csv $name $t | Out-Null }
    $code = Stop-Load $load
    Export-Profiles $name | Out-Null
    $rows = Import-Csv $csv | Where-Object { $_.tickMsMax -as [double] }
    $tick = if ($rows) { ($rows | Measure-Object -Property tickMsMax -Maximum).Maximum } else { 0 }
    if ($code -ne 0) { Fail "${name}: the load client reported session changes or pawn losses" } else { Pass "${name}: no session changes, no pawn losses" }
    if ([double]$tick -gt $T.WorkerTickMsMax) { Fail "${name}: worst worker tick $tick ms is over the $($T.WorkerTickMsMax) ms budget" }
    else { Pass "${name}: worst worker tick $tick ms" }
    if (Test-Path $load.Csv) {
        $last = Import-Csv $load.Csv | Select-Object -Last 1
        Say "${name}: $($last.joined) joined, $($last.replicasAvg) replicas avg, $($last.bytesPerClientMax) B/s max per client, rtt p95 $($last.rttP95) ms"
        if ([double]$last.bytesPerClientMax -gt $T.BytesPerClientPerSecond) { Fail "${name}: a client was sent more than the per-client bandwidth bound" }
        else { Pass "${name}: per-client bandwidth inside the bound" }
    }
    Stop-Mesh
}

function Invoke-WorkerKill {
    Start-Mesh $Workers | Out-Null
    $csv = New-Csv 'worker-kill-state' $StateHeader
    $load = Start-Load 'worker-kill' $Clients $Seconds $RampSeconds
    $killed = $false; $killedAt = 0; $containersBefore = 0
    for ($t = 0; $t -lt $Seconds; $t += 5) {
        Start-Sleep -Seconds 5
        $s = Sample-State $csv 'worker-kill' $t
        if (-not $killed -and $t -ge $KillAfterSeconds) {
            $containersBefore = if ($s) { $s.totals.containers } else { 0 }
            $killed = Stop-OneWorker $KillWorker
            $killedAt = $t
        }
    }
    $code = Stop-Load $load
    Export-Profiles 'worker-kill' | Out-Null
    $after = Get-State
    if ($after) {
        $restored = @($after.containers | Where-Object { $_.worker }).Count
        Say "containers owned before the kill: $containersBefore, after the run: $restored"
        if ($restored -lt $containersBefore) { Fail "worker-kill: $($containersBefore - $restored) container(s) never found an owner again" }
        else { Pass "worker-kill: every container is owned again" }
        $dirty = ($after.workers | Measure-Object -Property oldestDirtySeconds -Maximum).Maximum
        Say "oldest unsaved change after the restore: $dirty s (the durability window is docs/persistence-durability.md D2)"
    }
    if ($code -ne 0) { Say "the load client reported session changes or pawn losses: a worker kill despawns its pawns, so read the CSV rather than the exit code" 'Yellow' }
    Stop-Mesh
}

function Invoke-MeshRestart {
    Start-Mesh $Workers | Out-Null
    $before = Get-State
    $expected = @($before.containers).Count
    $csv = New-Csv 'mesh-restart-curve' 'layer,scenario,t,containersOwned,liveWorkers'
    Say "stopping the whole mesh ($expected containers owned)"
    & nebula stop --yes 2>&1 | Out-Null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Mesh $Workers | Out-Null
    $full = $null
    while ($clock.Elapsed.TotalSeconds -lt ($expected * $T.RestoreSecondsPerContainer + 120)) {
        $s = Get-State
        $owned = if ($s) { @($s.containers | Where-Object { $_.worker }).Count } else { 0 }
        Add-Csv $csv ("{0},mesh-restart,{1:0.00},{2},{3}" -f $layer, $clock.Elapsed.TotalSeconds, $owned, $(if ($s) { $s.totals.liveWorkers } else { 0 }))
        if ($owned -ge $expected) { $full = $clock.Elapsed.TotalSeconds; break }
        Start-Sleep -Seconds 2
    }
    if ($null -eq $full) { Fail "mesh-restart: the mesh never owned all $expected containers again" }
    else {
        $per = $full / [math]::Max(1, $expected)
        Say "whole mesh back in $([math]::Round($full,2)) s ($([math]::Round($per,2)) s per container); curve in $csv"
        if ($per -gt $T.RestoreSecondsPerContainer) { Fail "mesh-restart: $([math]::Round($per,2)) s per container is over the $($T.RestoreSecondsPerContainer) s budget" }
        else { Pass "mesh-restart: inside the per-container budget" }
    }
    Stop-Mesh
}

function Invoke-GatewayKill {
    Say "gateway kill needs a second gateway process to reclaim onto, which the local CLI mesh does not start" 'Yellow'
    Say "status: NOT RUN in tier D. The synthetic layer measures it (ScaleFailureTests, Logs/scale/synthetic-gateway-*.csv)." 'Yellow'
    Say "A real multi-gateway fleet is the gateway audit (NEB-229) and the control-plane availability work (NEB-227); see docs/scale-suite.md D9a." 'Yellow'
}

# ---------------------------------------------------------------------------------------------------- main

if ($Synthetic) {
    $layer = 'synthetic'
    Say "running the synthetic layer: dotnet test --filter TestCategory=Scale"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & dotnet test (Join-Path $repo 'Packages\com.1by3.nebula\Services~\Nebula.Services.slnx') --filter 'TestCategory=Scale' --nologo
    $code = $LASTEXITCODE
    Say "finished in $([math]::Round($clock.Elapsed.TotalSeconds,1)) s with exit code $code; artifacts in $out"
    exit $code
}

Say "plan: scenario=$Scenario workers=$Workers clients=$Clients seconds=$Seconds killWorker=$KillWorker"
Say "artifacts: $out\$layer-*.csv   mesh logs: $meshLog"
$problems = Test-Preconditions
foreach ($p in $problems) { Write-Host "[scale:$layer] precondition: $p" -ForegroundColor Yellow }

if ($DryRun) {
    Say "dashboard port $DashboardPort in use: $(Test-Port $DashboardPort); gateway port $GatewayPort in use: $(Test-Port $GatewayPort)"
    if ($problems) {
        Say "DRY RUN: this machine cannot run the tier-D layer as it stands. Use -Synthetic for the measurable part." 'Yellow'
        exit 0
    }
    Say "DRY RUN: preconditions are met; a real run would start the mesh and drive it." 'Green'
    exit 0
}

if ($problems -and -not $Build) {
    foreach ($p in $problems) { Write-Host "[scale:$layer] $p" -ForegroundColor Red }
    Write-Host "[scale:$layer] cannot run the real layer on this machine. Run with -Build to produce a player build, or -Synthetic for the in-process layer." -ForegroundColor Red
    exit 2
}
if ($Build) {
    Say "nebula build --stop-mesh"
    & nebula build --stop-mesh
    if ($LASTEXITCODE -ne 0) { throw "nebula build failed with exit code $LASTEXITCODE" }
}

try {
    switch ($Scenario) {
        'sustained'    { Invoke-Sustained }
        'burst'        { Invoke-Sustained }
        'worker-kill'  { Invoke-WorkerKill }
        'gateway-kill' { Invoke-GatewayKill }
        'mesh-restart' { Invoke-MeshRestart }
        'all'          { Invoke-Sustained; Invoke-WorkerKill; Invoke-MeshRestart; Invoke-GatewayKill }
    }
} finally {
    if (-not $KeepRunning) { & nebula stop --yes 2>&1 | Out-Null }
}

Write-Host ""
if ($script:failures.Count -eq 0) { Write-Host "[scale:$layer] PASS" -ForegroundColor Green; exit 0 }
Write-Host "[scale:$layer] FAIL ($($script:failures.Count))" -ForegroundColor Red
foreach ($f in $script:failures) { Write-Host "  - $f" -ForegroundColor Red }
exit 1
