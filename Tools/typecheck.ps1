<#
.SYNOPSIS
  Fast, Unity-free typecheck of the Nebula and ShooterGame assemblies.

.DESCRIPTION
  The Unity Editor holds an exclusive lock on the project, so a batchmode compile cannot run
  while it is open. This script reads every .asmdef under Assets/ and the embedded packages, generates a matching csproj
  that references Unity's managed assemblies directly, and builds the lot with `dotnet build`.

  Package assemblies referenced from an asmdef are resolved in this order:
    1. Library/ScriptAssemblies/<name>.dll            (already compiled by the Editor)
    2. an .asmdef with that name under Library/PackageCache or -ExtraSourceRoots (compiled from source)

  It reproduces the asmdef reference graph, so it catches assembly-boundary violations as well as
  ordinary type errors, in a few seconds and without touching the Editor. It is a typecheck, not a
  substitute for compiling inside Unity.

.PARAMETER ExtraSourceRoots
  Additional directories to search for package .asmdef files (e.g. a git clone of the SpacetimeDB
  SDK when the Editor has not resolved the package yet).

.EXAMPLE
  pwsh Tools/typecheck.ps1
  powershell -File Tools/typecheck.ps1 -ExtraSourceRoots C:\path\to\spacetimedbsdk
#>
[CmdletBinding()]
param(
    [string[]]$ExtraSourceRoots = @(),
    [string]$UnityVersion = '6000.6.0f1',
    [switch]$ShowWarnings
)

$ErrorActionPreference = 'Stop'
$repo      = Split-Path -Parent $PSScriptRoot
$assets    = Join-Path $repo 'Assets'
$unityData = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Data"
$genRoot   = Join-Path $repo "Temp\typecheck\$PID"

if (-not (Test-Path $unityData)) { throw "Unity install not found at $unityData" }

Remove-Item $genRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $genRoot | Out-Null
Get-ChildItem (Join-Path $repo 'Temp\typecheck') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+$' -and $_.Name -ne "$PID" -and -not (Get-Process -Id ([int]$_.Name) -ErrorAction SilentlyContinue) } |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# --- Unity engine + editor reference assemblies -------------------------------------------
$engineDlls = Get-ChildItem (Join-Path $unityData 'Managed\UnityEngine') -Filter 'UnityEngine*.dll' | Select-Object -ExpandProperty FullName
$editorDll  = Join-Path $unityData 'Managed\UnityEditor.dll'
$editorDlls = @($editorDll) + (Get-ChildItem (Join-Path $unityData 'Managed\UnityEngine') -Filter 'UnityEditor*.dll' | Select-Object -ExpandProperty FullName)

# --- index every precompiled DLL we might need ----------------------------------------------
$dllIndex = @{}
$dllRoots = @((Join-Path $repo 'Library\ScriptAssemblies'), (Join-Path $assets 'Plugins'), (Join-Path $repo 'Library\PackageCache')) + $ExtraSourceRoots
foreach ($d in $dllRoots) {
    if (Test-Path $d) {
        Get-ChildItem $d -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.FullName -notmatch '[\\/](bin~|obj~|bin|obj)[\\/]' -and -not $dllIndex.ContainsKey($_.Name)) { $dllIndex[$_.Name] = $_.FullName }
        }
    }
}

# --- discover asmdefs: project ones are always built; package ones only when referenced -------
function Read-Asmdef($file) {
    $json = Get-Content $file.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{ Name = $json.name; Dir = $file.DirectoryName; Json = $json; File = $file.FullName }
}
$projectAsmdefs = @{}
foreach ($root in @($assets) + @(Get-ChildItem (Join-Path $repo 'Packages') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'package.json') } | Select-Object -ExpandProperty FullName)) {
    Get-ChildItem $root -Recurse -Filter '*.asmdef' | ForEach-Object { $a = Read-Asmdef $_; $projectAsmdefs[$a.Name] = $a }
}
if ($projectAsmdefs.Count -eq 0) { Write-Host '[typecheck] no asmdefs found'; exit 0 }

$packageAsmdefs = @{}
$pkgRoots = @((Join-Path $repo 'Library\PackageCache')) + $ExtraSourceRoots
foreach ($root in $pkgRoots) {
    if (Test-Path $root) {
        Get-ChildItem $root -Recurse -Filter '*.asmdef' -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.FullName -match '[\\/](Tests|Samples~|examples~|tests~)[\\/]') { return }
            $a = Read-Asmdef $_
            if (-not $packageAsmdefs.ContainsKey($a.Name)) { $packageAsmdefs[$a.Name] = $a }
        }
    }
}

# Which asmdefs turn into generated projects. Start with project asmdefs, pull in package asmdefs
# only when no compiled DLL exists for them.
$build = @{}
$queue = New-Object System.Collections.Generic.Queue[object]
foreach ($a in $projectAsmdefs.Values) { $build[$a.Name] = $a; $queue.Enqueue($a) }
function Resolve-Reference($name) {
    # asmdef references may be by name or by GUID:xxxx; we only support names.
    if ($name -like 'GUID:*') { return $null }
    if ($build.ContainsKey($name)) { return 'project' }
    if ($dllIndex.ContainsKey("$name.dll")) { return 'dll' }
    if ($packageAsmdefs.ContainsKey($name)) { return 'source' }
    return $null
}
while ($queue.Count -gt 0) {
    $a = $queue.Dequeue()
    foreach ($r in @($a.Json.references)) {
        if (-not $r) { continue }
        $kind = Resolve-Reference $r
        if ($kind -eq 'source') { $p = $packageAsmdefs[$r]; $build[$r] = $p; $queue.Enqueue($p) }
        elseif ($null -eq $kind) { Write-Host "[typecheck] WARNING: reference '$r' from $($a.Name) could not be resolved" -ForegroundColor Yellow }
    }
}

function New-Csproj($a) {
    $j        = $a.Json
    $isEditor = $j.includePlatforms -and ($j.includePlatforms -contains 'Editor')
    $unsafe   = if ($j.allowUnsafeCode) { 'true' } else { 'false' }
    $nullable = 'disable'
    $rsp = Join-Path $a.Dir 'csc.rsp'
    if ((Test-Path $rsp) -and ((Get-Content $rsp -Raw) -match 'nullable')) { $nullable = 'enable' }

    $refs = New-Object System.Text.StringBuilder
    foreach ($dll in $engineDlls) {
        [void]$refs.AppendLine("    <Reference Include=`"$([System.IO.Path]::GetFileNameWithoutExtension($dll))`"><HintPath>$dll</HintPath><Private>false</Private></Reference>")
    }
    if ($isEditor) {
        foreach ($dll in $editorDlls) {
            [void]$refs.AppendLine("    <Reference Include=`"$([System.IO.Path]::GetFileNameWithoutExtension($dll))`"><HintPath>$dll</HintPath><Private>false</Private></Reference>")
        }
    }
    $wanted = @()
    if ($j.precompiledReferences) { $wanted += $j.precompiledReferences }
    foreach ($r in @($j.references)) { if ($r -and -not $build.ContainsKey($r) -and $dllIndex.ContainsKey("$r.dll")) { $wanted += "$r.dll" } }
    # A source package may ship DLLs of its own (e.g. the SpacetimeDB SDK's BSATN runtime). Those are needed both by
    # the package itself and by every assembly that references it (Unity makes them visible project-wide).
    # Referenced packages count whether we compile them from source or take the Editor's compiled DLL.
    $dllSources = @($a) + @(foreach ($r in @($j.references)) { if ($r -and $build.ContainsKey($r)) { $build[$r] } elseif ($r -and $packageAsmdefs.ContainsKey($r)) { $packageAsmdefs[$r] } })
    foreach ($src in $dllSources) {
        Get-ChildItem $src.Dir -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.FullName -notmatch '[\\/](bin~|obj~|bin|obj|analyzers)[\\/]') { $wanted += $_.Name; if (-not $dllIndex.ContainsKey($_.Name)) { $dllIndex[$_.Name] = $_.FullName } }
        }
        $parent = Split-Path -Parent $src.Dir
        if (Test-Path (Join-Path $parent 'packages')) {
            Get-ChildItem (Join-Path $parent 'packages') -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.FullName -match 'netstandard2\.1' -and $_.FullName -notmatch 'analyzers') { $wanted += $_.Name; if (-not $dllIndex.ContainsKey($_.Name)) { $dllIndex[$_.Name] = $_.FullName } }
            }
        }
    }
    foreach ($name in ($wanted | Select-Object -Unique)) {
        if ($dllIndex.ContainsKey($name)) {
            [void]$refs.AppendLine("    <Reference Include=`"$([System.IO.Path]::GetFileNameWithoutExtension($name))`"><HintPath>$($dllIndex[$name])</HintPath><Private>false</Private></Reference>")
        }
    }

    $projRefs = New-Object System.Text.StringBuilder
    foreach ($r in @($j.references)) {
        if ($r -and $build.ContainsKey($r)) {
            [void]$projRefs.AppendLine("    <ProjectReference Include=`"$genRoot\$r\$r.csproj`" />")
        }
    }

    # Roslyn analyzers / source generators shipped by a package (SpacetimeDB's BSATN codegen) apply to
    # every assembly that references that package, exactly as Unity's RoslynAnalyzer label does.
    $analyzers = New-Object System.Text.StringBuilder
    $analyzerSources = $dllSources
    foreach ($src in $analyzerSources) {
        $root = Split-Path -Parent $src.Dir
        foreach ($cand in @($src.Dir, $root)) {
            Get-ChildItem $cand -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.FullName -match '[\\/]analyzers[\\/]') { [void]$analyzers.AppendLine("    <Analyzer Include=`"$($_.FullName)`" />") }
            }
        }
    }

    $srcDir = $a.Dir
    $excludes = New-Object System.Text.StringBuilder
    foreach ($other in ($projectAsmdefs.Values + $packageAsmdefs.Values)) {
        if ($other.Name -eq $a.Name) { continue }
        $childDir = $other.Dir.TrimEnd('\')
        if ($childDir.StartsWith($srcDir.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$excludes.AppendLine("    <Compile Remove=`"$childDir\**\*.cs`" />")
        }
    }
    [void]$excludes.AppendLine("    <Compile Remove=`"$srcDir\**\*~\**\*.cs`" />")
    [void]$excludes.AppendLine("    <Compile Remove=`"$srcDir\**\bin~\**\*.cs`" />")
    [void]$excludes.AppendLine("    <Compile Remove=`"$srcDir\**\obj~\**\*.cs`" />")

    $defines = 'UNITY_5_3_OR_NEWER;UNITY_2017_1_OR_NEWER;UNITY_2019_1_OR_NEWER;UNITY_2020_1_OR_NEWER;UNITY_2021_1_OR_NEWER;UNITY_2022_1_OR_NEWER;UNITY_2023_1_OR_NEWER;UNITY_6000_0_OR_NEWER;UNITY_6000_5_OR_NEWER;UNITY_6000_6_OR_NEWER;UNITY_EDITOR;UNITY_EDITOR_WIN;UNITY_INCLUDE_TESTS;UNITY_STANDALONE;UNITY_STANDALONE_WIN;ENABLE_INPUT_SYSTEM;ENABLE_MONO;NET_STANDARD_2_1;NETSTANDARD2_1;NEBULA_TYPECHECK'
    if ($j.defineConstraints) { $defines += ';' + (($j.defineConstraints | Where-Object { $_ -notmatch '^!' }) -join ';') }

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>$($a.Name)</AssemblyName>
    <RootNamespace>$($j.rootNamespace)</RootNamespace>
    <AllowUnsafeBlocks>$unsafe</AllowUnsafeBlocks>
    <Nullable>$nullable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DisableImplicitNamespaceImports>true</DisableImplicitNamespaceImports>
    <ImplicitUsings>disable</ImplicitUsings>
    <NoWarn>CS0649;CS0414;CS0169;CS0067;CS1591;CS8632;CS0618</NoWarn>
    <DefineConstants>$defines</DefineConstants>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$srcDir\**\*.cs" />
$($excludes.ToString())  </ItemGroup>
  <ItemGroup>
$($refs.ToString())  </ItemGroup>
  <ItemGroup>
$($projRefs.ToString())  </ItemGroup>
  <ItemGroup>
$($analyzers.ToString())  </ItemGroup>
</Project>
"@
}

foreach ($a in $build.Values) {
    $dir = Join-Path $genRoot $a.Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    New-Csproj $a | Out-File -Encoding utf8 (Join-Path $dir "$($a.Name).csproj")
}

& dotnet new sln -n Nebula.Typecheck -o $genRoot --force | Out-Null
$sln = Get-ChildItem $genRoot -File | Where-Object { $_.Extension -in '.sln', '.slnx' } | Select-Object -First 1 -ExpandProperty FullName
if (-not $sln) { throw "dotnet new sln produced no solution file in $genRoot" }
foreach ($a in $build.Values) { & dotnet sln $sln add (Join-Path $genRoot "$($a.Name)\$($a.Name).csproj") | Out-Null }

Write-Host "[typecheck] building $($build.Count) assemblies: $($build.Keys -join ', ')" -ForegroundColor Cyan
$out = & dotnet build $sln -v quiet --nologo 2>&1 | Out-String
$errors = $out -split "`r?`n" | Where-Object { $_ -match '\): error ' } | ForEach-Object { ($_ -replace ' \[.*$', '').Trim() } | Select-Object -Unique
$warnings = $out -split "`r?`n" | Where-Object { $_ -match '\): warning ' -and $_ -notmatch 'PackageCache|scratchpad' } | ForEach-Object { ($_ -replace ' \[.*$', '').Trim() } | Select-Object -Unique

if ($errors) {
    Write-Host "[typecheck] FAILED ($($errors.Count) errors)" -ForegroundColor Red
    $errors | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "[typecheck] build failed with no parsed errors; raw output:" -ForegroundColor Red
    Write-Host $out
    exit 1
}
if ($ShowWarnings -and $warnings) {
    Write-Host "[typecheck] $($warnings.Count) warnings" -ForegroundColor Yellow
    $warnings | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
Write-Host '[typecheck] OK' -ForegroundColor Green
exit 0
