<#
.SYNOPSIS
  Runs the Nebula conformance suite: every test tagged [Category("Conformance")], in the .NET service tests and in
  the Unity EditMode tests, and prints one PASS/FAIL summary. Exit code 0 when everything passed, 1 otherwise.

.DESCRIPTION
  The conformance suite is the exit criterion for the scopes and interaction contracts (docs/conformance-suite.md):
  one deterministic scenario per contract, on a small in-process mesh, no game content, no live processes.

  Two places hold conformance tests:
    - Packages/com.1by3.nebula/Services~/Nebula.Services.Tests   pure C# (dotnet test --filter TestCategory=Conformance)
    - Packages/com.1by3.nebula/Tests/EditMode                     Unity (Editor in batchmode, -testCategory Conformance)

  The Unity run needs the Editor closed for this project: batchmode cannot open a project another Editor holds.
  The Unity result file is NUnit 3 XML; the script reads it for the counts and names the failures.

.PARAMETER DotnetOnly
  Skip the Unity run. Fast (seconds instead of minutes), and the only mode that works while the Editor is open.

.PARAMETER UnityVersion
  Editor version under C:\Program Files\Unity\Hub\Editor. Default 6000.6.0f1.

.PARAMETER UnityPath
  Full path to Unity.exe; overrides -UnityVersion.

.PARAMETER Repo
  The Unity project to run against (default: the repository this script lives in).

.EXAMPLE
  powershell -File Tools/conformance.ps1                 # both tiers (Windows PowerShell 5.1 or pwsh)
  powershell -File Tools/conformance.ps1 -DotnetOnly     # the pure C# tier only, Editor may stay open
#>
[CmdletBinding()]
param(
    [switch]$DotnetOnly,
    [string]$UnityVersion = '6000.6.0f1',
    [string]$UnityPath = '',
    [string]$Repo = ''
)

$ErrorActionPreference = 'Stop'
$repo = if ($Repo) { (Resolve-Path $Repo).Path } else { Split-Path -Parent $PSScriptRoot }
# Not under Temp/: Unity empties that folder when the Editor exits, which would take the result file with it.
$out  = Join-Path $repo 'Logs\conformance'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Remove-Item (Join-Path $out '*') -Recurse -Force -ErrorAction SilentlyContinue

# One row per tier; the summary at the end is built from these.
$tiers = New-Object System.Collections.Generic.List[object]
function Add-Tier($name, $ran, $passed, $failed, $skipped, $failures, $note) {
    $tiers.Add([pscustomobject]@{ Name = $name; Ran = $ran; Passed = $passed; Failed = $failed; Skipped = $skipped; Failures = @($failures); Note = $note })
}

# ---------------------------------------------------------------------------------------------- dotnet tier
Write-Host "[conformance] dotnet: Nebula.Services.Tests, TestCategory=Conformance" -ForegroundColor Cyan
$slnx = Join-Path $repo 'Packages\com.1by3.nebula\Services~\Nebula.Services.slnx'
$trxDir = Join-Path $out 'dotnet'
# stdout only: under Windows PowerShell 5.1 a `2>&1` on a native command turns every stderr line into an ErrorRecord,
# which $ErrorActionPreference = 'Stop' would then promote to a terminating error.
& dotnet test $slnx --filter "TestCategory=Conformance" --logger "trx;LogFileName=conformance.trx" --results-directory $trxDir --nologo -v quiet |
    Where-Object { $_ -notmatch 'warning CS' } | ForEach-Object { Write-Host "  $_" }
$dotnetExit = $LASTEXITCODE
$trx = Get-ChildItem $trxDir -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($trx) {
    [xml]$doc = Get-Content $trx.FullName -Raw
    $counters = $doc.TestRun.ResultSummary.Counters
    $failedNames = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
    $skipped = [int]$counters.total - [int]$counters.passed - [int]$counters.failed
    Add-Tier 'dotnet (Nebula.Services.Tests)' $true ([int]$counters.passed) ([int]$counters.failed) $skipped $failedNames ''
} else {
    Add-Tier 'dotnet (Nebula.Services.Tests)' $false 0 0 0 @() "no TRX result produced (dotnet test exit $dotnetExit)"
}

# ---------------------------------------------------------------------------------------------- unity tier
if ($DotnetOnly) {
    Add-Tier 'unity (Nebula.Tests.EditMode)' $false 0 0 0 @() 'skipped (-DotnetOnly)'
} else {
    $unity = if ($UnityPath) { $UnityPath } else { "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe" }
    $lock  = Join-Path $repo 'Temp\UnityLockfile'
    if (-not (Test-Path $unity)) {
        Add-Tier 'unity (Nebula.Tests.EditMode)' $false 0 0 0 @() "Unity not found at $unity"
    } elseif (Test-Path $lock) {
        Add-Tier 'unity (Nebula.Tests.EditMode)' $false 0 0 0 @() "the project is open in an Editor ($lock exists); close it or use -DotnetOnly"
    } else {
        Write-Host "[conformance] unity: Nebula.Tests.EditMode, -testCategory Conformance (batchmode; this takes a few minutes)" -ForegroundColor Cyan
        $results = Join-Path $out 'editmode.xml'
        $log     = Join-Path $out 'unity.log'
        $unityArgs = @('-batchmode', '-nographics', '-projectPath', $repo, '-runTests', '-testPlatform', 'EditMode',
                       '-assemblyNames', 'Nebula.Tests.EditMode', '-testCategory', 'Conformance',
                       '-testResults', $results, '-logFile', $log)
        $proc = Start-Process -Wait -PassThru -FilePath $unity -ArgumentList $unityArgs
        if (Test-Path $results) {
            [xml]$doc = Get-Content $results -Raw
            $run = $doc.'test-run'
            $failedNames = @($doc.SelectNodes("//test-case[@result='Failed']") | ForEach-Object { $_.fullname })
            Add-Tier 'unity (Nebula.Tests.EditMode)' $true ([int]$run.passed) ([int]$run.failed) ([int]$run.skipped + [int]$run.inconclusive) $failedNames ''
        } else {
            Add-Tier 'unity (Nebula.Tests.EditMode)' $false 0 0 0 @() "no result file; Unity exit $($proc.ExitCode), see $log"
        }
    }
}

# ---------------------------------------------------------------------------------------------- summary
Write-Host "`n[conformance] ===== summary =====" -ForegroundColor Cyan
$anyFailed = $false
$anyRan = $false
foreach ($t in $tiers) {
    if (-not $t.Ran) {
        Write-Host ("  {0,-34} not run: {1}" -f $t.Name, $t.Note) -ForegroundColor Yellow
        if ($t.Note -notmatch '^skipped') { $anyFailed = $true }
        continue
    }
    $anyRan = $true
    $status = if ($t.Failed -gt 0) { 'FAIL' } else { 'PASS' }
    if ($t.Failed -gt 0) { $anyFailed = $true }
    Write-Host ("  {0,-34} {1}  passed={2} failed={3} skipped={4}" -f $t.Name, $status, $t.Passed, $t.Failed, $t.Skipped) -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } else { 'Red' })
    foreach ($f in $t.Failures) { Write-Host "      FAILED $f" -ForegroundColor Red }
}
$totalPassed = ($tiers | Measure-Object -Property Passed -Sum).Sum
$totalFailed = ($tiers | Measure-Object -Property Failed -Sum).Sum
$verdict = if ($anyFailed -or -not $anyRan) { 'FAIL' } else { 'PASS' }
Write-Host ("  {0,-34} {1}  passed={2} failed={3}" -f 'TOTAL', $verdict, $totalPassed, $totalFailed) -ForegroundColor $(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' })
Write-Host "  results: $out"
exit $(if ($verdict -eq 'PASS') { 0 } else { 1 })
