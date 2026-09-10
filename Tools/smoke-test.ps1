<#
.SYNOPSIS
  End-to-end smoke test of the mesh with no human in the loop: starts SpacetimeDB + orchestrator (gateway, N workers)
  + M bot clients from the last build, lets them roam across the container quadrants for a while, then reports what
  happened from the logs: workers registered, leases assigned, players spawned, authority handovers, cross-worker hits.

.EXAMPLE
  pwsh Tools/smoke-test.ps1                       # 4 workers, 2 bots, 60 s
  pwsh Tools/smoke-test.ps1 -Workers 3 -Bots 3 -Seconds 90
  pwsh Tools/smoke-test.ps1 -KillWorker w2 -KillAfter 30   # also kill a worker mid-run to watch the rebalance
  pwsh Tools/smoke-test.ps1 -ScaleTo 2 -ScaleAfter 25 -Seconds 75   # shrink the mesh through the dashboard API mid-run
  pwsh Tools/smoke-test.ps1 -Workers 2 -ScaleTo 4 -ScaleAfter 25    # grow it
#>
[CmdletBinding()]
param(
    [int]$Workers = 4,
    [int]$Bots = 2,
    [int]$Npcs = 0,
    [int]$Seconds = 60,
    [string]$KillWorker = '',
    [int]$KillAfter = 30,
    [int]$ScaleTo = -1,
    [int]$ScaleAfter = 30,
    [int]$DashboardPort = 7080,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$logs = Join-Path $repo 'Builds\Win64\Logs'

# The mesh itself is the CLI's job (nebula start/stop); this script only drives and measures it.
& nebula stop | Out-Null
Remove-Item (Join-Path $logs '*.log') -ErrorAction SilentlyContinue
& nebula start --workers $Workers --bots $Bots --npcs $Npcs
if ($LASTEXITCODE -ne 0) { throw 'nebula start failed' }

function Dashboard($path) {
    try { (Invoke-RestMethod -Uri "http://localhost:$DashboardPort$path" -TimeoutSec 3) } catch { $null }
}

$elapsed = 0
$killed = $false
$scaled = $false
while ($elapsed -lt $Seconds) {
    Start-Sleep -Seconds 5
    $elapsed += 5
    if ($ScaleTo -ge 0 -and -not $scaled -and $elapsed -ge $ScaleAfter) {
        $scaled = $true
        try {
            $r = Invoke-RestMethod -Method Post -Uri "http://localhost:$DashboardPort/api/desired" -ContentType 'application/json' -Body (@{ desired = $ScaleTo } | ConvertTo-Json) -TimeoutSec 5
            Write-Host "[smoke] asked the dashboard for $ScaleTo workers at ${elapsed}s (desired now $($r.desired))" -ForegroundColor Yellow
        } catch { Write-Host "[smoke] dashboard scale request failed: $_" -ForegroundColor Red }
    }
    if ($KillWorker -and -not $killed -and $elapsed -ge $KillAfter) {
        $killed = $true
        $target = Get-CimInstance Win32_Process -Filter "name='Nebula.exe'" | Where-Object { $_.CommandLine -match "-nebula-worker-id $KillWorker\b" } | Select-Object -First 1
        if ($target) { Stop-Process -Id $target.ProcessId -Force; Write-Host "[smoke] killed worker $KillWorker (pid $($target.ProcessId)) at ${elapsed}s" -ForegroundColor Yellow }
        else { Write-Host "[smoke] could not find worker $KillWorker to kill" -ForegroundColor Yellow }
    }
    $state = Dashboard '/api/state'
    if ($state) {
        $ws = ($state.workers | ForEach-Object { "$($_.id)[$($_.state) c=$($_.containers.Count) p=$($_.players) b=$($_.bots) n=$($_.serverDriven)]" }) -join ' '
        Write-Host "[smoke] ${elapsed}s desired=$($state.desiredWorkers) live=$($state.totals.liveWorkers) players=$($state.totals.players) bots=$($state.totals.bots) npcs=$($state.totals.serverDriven) $ws"
    } else {
        Write-Host "[smoke] ${elapsed}s (dashboard unreachable)"
    }
}

function Count($pattern, $file) { if (Test-Path $file) { @(Select-String -Path $file -Pattern $pattern).Count } else { -1 } }

Write-Host "`n[smoke] ===== results =====" -ForegroundColor Cyan
$orch = Join-Path $logs 'orchestrator.log'
$gw   = Join-Path $logs 'gateway.log'
Write-Host ("orchestrator: assignments={0} dead-declared={1} relaunched={2} scaled-up={3} retired={4} drain-timeouts={5}" -f (Count 'assign ' $orch), (Count 'declaring dead' $orch), (Count 'relaunching worker' $orch), (Count 'scaling up' $orch), (Count 'drained; shutting it down|retired \(process gone\)' $orch), (Count 'drain timeout' $orch))
Write-Host ("gateway: workers-connected={0} clients-connected={1} spawn-requests={2}" -f (Count 'connected' $gw), (Count "client \d+ '" $gw), (Count 'asked ' $gw))
$totalOut = 0; $totalIn = 0
Get-ChildItem $logs -Filter 'w*.log' | Sort-Object Name | ForEach-Object {
    $out = Count 'handover OUT' $_.FullName; $in = Count 'handover IN' $_.FullName
    $totalOut += $out; $totalIn += $in
    Write-Host ("{0}: registered={1} peers={2} spawned={3} handover-out={4} handover-in={5} cross-worker-hits={6} kills={7} warnings={8} errors={9}" -f $_.BaseName, (Count 'registered with control plane' $_.FullName), (Count 'peer worker .* connected' $_.FullName), (Count 'spawned player' $_.FullName), $out, $in, (Count 'cross-worker hit' $_.FullName), (Count 'kill:' $_.FullName), (Count 'LogWarning|\[nebula\].*warn' $_.FullName), (Count 'LogError|Exception' $_.FullName))
}
Get-ChildItem $logs -Filter 'bot*.log' | Sort-Object Name | ForEach-Object {
    Write-Host ("{0}: welcome={1} local-player={2} errors={3}" -f $_.BaseName, (Count 'welcome:' $_.FullName), (Count 'local player spawned' $_.FullName), (Count 'Exception' $_.FullName))
}
Write-Host ("TOTAL handovers: out={0} in={1}" -f $totalOut, $totalIn) -ForegroundColor $(if ($totalOut -gt 0) { 'Green' } else { 'Red' })

if (-not $KeepRunning) { & nebula stop | Out-Null }
