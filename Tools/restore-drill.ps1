<#
.SYNOPSIS
    Backup-and-restore drill for a Nebula entity store and control plane.

.DESCRIPTION
    Seeds a database with known records, takes a snapshot, backs it up, WIPES it, proves the wipe emptied it,
    restores from the backup and proves the restored database is the one that was backed up. It leaves an
    artifact under Logs/restore-drill/<timestamp>.json and prints PASS or FAIL with a non-zero exit code.

    The verification is done by `Services~/Nebula.RestoreDrill`, which reads every record through Nebula's own
    SqlPersistenceStore and SqlControlPlaneStorage rather than through hand-written SQL. A drill that compares
    tables proves the bytes came back; this one proves the mesh can read what came back.

    It needs no player build, no Unity and no running mesh. By default it runs against a scratch SQLite database
    under Logs/restore-drill/, so it never touches the mesh's own Library/Nebula/nebula.db unless you name it.

    Design of record: docs/control-plane-availability.md D7. User-facing page:
    website/content/docs/deploy/availability.mdx.

.PARAMETER Database
    Nebula database URL to drill: `sqlite:<path>` or `postgres://user:pass@host:port/db`. The default is a
    throwaway SQLite file under Logs/restore-drill/. Drilling a database you care about is safe in the sense that
    the drill restores what it wiped, but it does wipe it, so do not point it at a live mesh.

.PARAMETER Entities
    How many records to seed (default 250).

.PARAMETER Seed
    Random seed for the content, so two runs seed identical records (default 1234).

.PARAMETER Method
    `auto` (default) uses the engine's own tools when it can: SQLite's online VACUUM INTO, or pg_dump/pg_restore
    when they are on PATH. `nebula` forces the engine-independent JSON export/import through the store, which is
    the only route that is portable between backends. `engine` insists on the engine tools and fails if they are
    missing.

.PARAMETER SkipBuild
    Do not `dotnet build` the drill tool first (it must already be built).

.PARAMETER KeepBackup
    Keep the backup file instead of deleting it after a passing run.

.EXAMPLE
    pwsh Tools/restore-drill.ps1

.EXAMPLE
    pwsh Tools/restore-drill.ps1 -Database "postgres://nebula:nebula@localhost:5432/nebula" -Entities 2000
#>
[CmdletBinding()]
param(
    [string]$Database = "",
    [int]$Entities = 250,
    [int]$Seed = 1234,
    [ValidateSet("auto", "engine", "nebula")][string]$Method = "auto",
    [switch]$SkipBuild,
    [switch]$KeepBackup
)

$ErrorActionPreference = "Stop"

# The repository root is found by the package, not by .git: in a git worktree .git is a *file*, and a directory
# probe walks past the root and writes the artifacts where nobody looks (the same rule as ScaleReport).
function Get-RepositoryRoot {
    $dir = Get-Item -LiteralPath $PSScriptRoot
    while ($dir -and -not (Test-Path -LiteralPath (Join-Path $dir.FullName "Packages/com.1by3.nebula/package.json"))) { $dir = $dir.Parent }
    if (-not $dir) { throw "could not find the repository root from $PSScriptRoot" }
    return $dir.FullName
}

$root = Get-RepositoryRoot
$services = Join-Path $root "Packages/com.1by3.nebula/Services~"
$project = Join-Path $services "Nebula.RestoreDrill"
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$logs = Join-Path $root "Logs/restore-drill"
$work = Join-Path $logs $stamp
New-Item -ItemType Directory -Force -Path $work | Out-Null

if ($Database -eq "") { $Database = "sqlite:" + (Join-Path $work "drill.db") }

$scheme = ($Database -split ":")[0]
if ($scheme -notin @("sqlite", "postgres", "postgresql")) { throw "restore-drill only drills sqlite: and postgres: databases (got '$Database')" }
$isSqlite = $scheme -eq "sqlite"

# Which backup route this run will take, and why. Recorded in the artifact: a drill that silently fell back to a
# different mechanism than the one production uses would be reassuring about the wrong thing.
$pgDump = (Get-Command pg_dump -ErrorAction SilentlyContinue)
$pgRestore = (Get-Command pg_restore -ErrorAction SilentlyContinue)
$route = switch ($Method) {
    "nebula" { "nebula" }
    "engine" {
        if ($isSqlite) { "sqlite-vacuum" }
        elseif ($pgDump -and $pgRestore) { "pg_dump" }
        else { throw "-Method engine needs pg_dump and pg_restore on PATH for a PostgreSQL database" }
    }
    default {
        if ($isSqlite) { "sqlite-vacuum" }
        elseif ($pgDump -and $pgRestore) { "pg_dump" }
        else { "nebula" }
    }
}

Write-Host "[restore-drill] database : $Database"
Write-Host "[restore-drill] route    : $route"
Write-Host "[restore-drill] artifacts: $work"

if (-not $SkipBuild) {
    Write-Host "[restore-drill] building the verifier..."
    & dotnet build $project -c Debug -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build of Nebula.RestoreDrill failed" }
}

$steps = [System.Collections.Generic.List[object]]::new()
function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    try { & $Body } catch { $failure = $_.Exception.Message }
    $clock.Stop()
    $steps.Add([ordered]@{ step = $Name; seconds = [math]::Round($clock.Elapsed.TotalSeconds, 3); ok = ($null -eq $failure); error = $failure })
    $status = if ($failure) { "FAILED: $failure" } else { "ok" }
    Write-Host ("[restore-drill] {0,-22} {1,8:0.000}s  {2}" -f $Name, $clock.Elapsed.TotalSeconds, $status)
    if ($failure) { throw $failure }
}

function Invoke-Drill {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $output = & dotnet run --project $project -c Debug --no-build -- @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    $output | ForEach-Object { Write-Verbose $_ }
    return $output
}

$backup = Join-Path $work ($(if ($route -eq "nebula") { "backup.json" } elseif ($route -eq "pg_dump") { "backup.dump" } else { "backup.db" }))
$before = Join-Path $work "before.json"
$wiped = Join-Path $work "wiped.json"
$after = Join-Path $work "after.json"
$comparison = Join-Path $work "comparison.json"

$passed = $false
$reason = ""
try {
    Invoke-Step "seed" { Invoke-Drill seed --db $Database --entities $Entities --seed $Seed | Out-Null }
    Invoke-Step "snapshot (before)" { Invoke-Drill snapshot --db $Database --out $before | Out-Null }

    Invoke-Step "backup" {
        switch ($route) {
            "sqlite-vacuum" { Invoke-Drill backup --db $Database --out $backup | Out-Null }
            "nebula" { Invoke-Drill export --db $Database --out $backup | Out-Null }
            "pg_dump" {
                & pg_dump --format=custom --no-owner --dbname $Database --file $backup
                if ($LASTEXITCODE -ne 0) { throw "pg_dump exited $LASTEXITCODE" }
            }
        }
        if (-not (Test-Path -LiteralPath $backup)) { throw "the backup step produced no file at $backup" }
    }

    Invoke-Step "wipe" { Invoke-Drill wipe --db $Database | Out-Null }

    # A drill whose wipe left the data in place would "pass" on the leftovers. This is the step that makes the
    # rest of the run mean anything.
    Invoke-Step "snapshot (wiped)" {
        Invoke-Drill snapshot --db $Database --out $wiped | Out-Null
        $emptied = (Get-Content -Raw $wiped | ConvertFrom-Json).records
        if ($emptied -ne 0) { throw "the wipe left $emptied record(s) behind, so the restore would not have been proved" }
    }

    Invoke-Step "restore" {
        switch ($route) {
            "sqlite-vacuum" { Invoke-Drill restore --db $Database --in $backup | Out-Null }
            "nebula" { Invoke-Drill import --db $Database --in $backup | Out-Null }
            "pg_dump" {
                & pg_restore --clean --if-exists --no-owner --dbname $Database $backup
                if ($LASTEXITCODE -ne 0) { throw "pg_restore exited $LASTEXITCODE" }
            }
        }
    }

    Invoke-Step "snapshot (after)" { Invoke-Drill snapshot --db $Database --out $after | Out-Null }
    Invoke-Step "compare" { Invoke-Drill compare --before $before --after $after --out $comparison | Out-Null }
    $passed = $true
}
catch {
    $reason = $_.Exception.Message
}

$snapshot = if (Test-Path -LiteralPath $before) { Get-Content -Raw $before | ConvertFrom-Json } else { $null }
$result = if (Test-Path -LiteralPath $comparison) { Get-Content -Raw $comparison | ConvertFrom-Json } else { $null }
$total = ($steps | ForEach-Object { $_.seconds } | Measure-Object -Sum).Sum

$artifact = [ordered]@{
    drill = "nebula-restore-drill"
    takenAt = (Get-Date).ToUniversalTime().ToString("o")
    database = $Database
    backend = $snapshot.backend
    route = $route
    entitiesSeeded = $Entities
    seed = $Seed
    recordsBackedUp = $snapshot.records
    leasesBackedUp = $snapshot.controlPlane.leases
    totalSeconds = [math]::Round($total, 3)
    steps = $steps
    comparison = $result
    passed = $passed
    reason = $reason
    files = [ordered]@{ before = $before; wiped = $wiped; after = $after; comparison = $comparison; backup = $backup }
}
$artifactPath = Join-Path $logs "$stamp.json"
$artifact | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $artifactPath

$summary = @(
    "nebula restore drill $stamp",
    "database        : $Database ($($snapshot.backend))",
    "route           : $route",
    "records         : $($snapshot.records) seeded, $($result.recordsAfter) restored",
    "control plane   : $($snapshot.controlPlane.leases) lease(s), digest $(if ($result.controlPlaneMatches) { 'matches' } else { 'DIFFERS' })",
    "total           : $([math]::Round($total, 3)) s",
    "result          : $(if ($passed) { 'PASS' } else { "FAIL - $reason" })"
) -join [Environment]::NewLine
$summaryPath = Join-Path $logs "$stamp.txt"
Set-Content -Encoding utf8 $summaryPath $summary

Write-Host ""
Write-Host $summary
Write-Host ""
Write-Host "[restore-drill] artifact: $artifactPath"
Write-Host "[restore-drill] summary : $summaryPath"

if ($passed -and -not $KeepBackup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Force }

if ($passed) { Write-Host "[restore-drill] PASS" -ForegroundColor Green; exit 0 }
Write-Host "[restore-drill] FAIL" -ForegroundColor Red
exit 1
