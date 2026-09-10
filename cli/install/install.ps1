<#
.SYNOPSIS
  Installs the Nebula CLI on Windows into ~/.nebula-cli/bin and adds it to the user's PATH.

.DESCRIPTION
  Hosted at https://windows.nebula.1by3.co and run with:
      iwr https://windows.nebula.1by3.co -useb | iex

  Three ways to get the binary, in this order of precedence:
    1. -Source <path> / $env:NEBULA_SOURCE   build from a checkout of the Nebula repository (needs the .NET 10 SDK)
    2. -Archive <path> / $env:NEBULA_ARCHIVE  install a local release archive (nebula-<version>-win-x64.zip)
    3. otherwise                              download the release from $env:NEBULA_RELEASE_BASE
                                              (default: the Nebula repository's releases; $env:NEBULA_VERSION picks one)

  When the script is piped into iex there are no parameters, so use the environment variables.

.EXAMPLE
  iwr https://windows.nebula.1by3.co -useb | iex
  $env:NEBULA_SOURCE = 'C:\Dev\nebula'; iwr https://windows.nebula.1by3.co -useb | iex
  powershell -File cli\install\install.ps1 -Source .          # from inside a checkout
#>
param(
    [string]$Source = $env:NEBULA_SOURCE,
    [string]$Archive = $env:NEBULA_ARCHIVE,
    [string]$Version = $env:NEBULA_VERSION,
    [string]$ReleaseBase = $env:NEBULA_RELEASE_BASE,
    [switch]$NoPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoApi = 'https://api.github.com/repos/1by3/nebula'
if (-not $ReleaseBase) { $ReleaseBase = 'https://github.com/1by3/nebula/releases/download' }
$cliHome = if ($env:NEBULA_CLI_HOME) { $env:NEBULA_CLI_HOME } else { Join-Path $HOME '.nebula-cli' }
$binDir = Join-Path $cliHome 'bin'
$exe = Join-Path $binDir 'nebula.exe'
$arch = if ([Environment]::Is64BitOperatingSystem -and $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$rid = "win-$arch"

function Say([string]$m) { Write-Host "[nebula-install] $m" -ForegroundColor Cyan }
function Fail([string]$m) { Write-Host "[nebula-install] $m" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ("nebula-install-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    if ($Source) {
        # --- from a checkout ---------------------------------------------------------------------------
        $Source = (Resolve-Path $Source).Path
        $proj = Join-Path $Source 'cli\Nebula.Cli\Nebula.Cli.csproj'
        if (-not (Test-Path $proj)) { Fail "$Source is not a Nebula checkout (no cli\Nebula.Cli\Nebula.Cli.csproj)" }
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { $dotnet = Get-Command (Join-Path $HOME '.dotnet\dotnet.exe') -ErrorAction SilentlyContinue }
        if (-not $dotnet) { Fail 'the .NET SDK (10.0 or newer) is required to build from source: https://dotnet.microsoft.com/download' }
        Say "building the CLI from $Source ($rid)"
        & $dotnet.Source publish $proj -c Release -r $rid -o $staging --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed (exit $LASTEXITCODE)" }
    } elseif ($Archive) {
        # --- from a local release archive ---------------------------------------------------------------
        Say "extracting $Archive"
        Expand-Archive -Path $Archive -DestinationPath $staging -Force
    } else {
        # --- download a release ------------------------------------------------------------------------------
        if (-not $Version) {
            try {
                $Version = (Invoke-RestMethod -UseBasicParsing -Uri "$repoApi/releases/latest").tag_name
            } catch {
                Fail "could not look up the latest release ($($_.Exception.Message)). Set NEBULA_VERSION, or install from a checkout with NEBULA_SOURCE."
            }
        }
        $tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        $asset = "nebula-$($tag.TrimStart('v'))-$rid.zip"
        $url = "$ReleaseBase/$tag/$asset"
        $zip = Join-Path $staging $asset
        Say "downloading $url"
        try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip } catch { Fail "download failed: $($_.Exception.Message)" }
        Expand-Archive -Path $zip -DestinationPath $staging -Force
    }

    $built = Get-ChildItem -Path $staging -Recurse -Filter 'nebula.exe' | Select-Object -First 1
    if (-not $built) { Fail 'no nebula.exe in the build output / archive' }
    if (Test-Path $exe) { Remove-Item $exe -Force -ErrorAction SilentlyContinue }
    Copy-Item $built.FullName $exe -Force
    Say "installed $exe"

    # Remember the checkout so `nebula init --embed` copies the package from it instead of cloning.
    if ($Source) { & $exe config source --path $Source }
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# --- PATH ----------------------------------------------------------------------------------------------
if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (-not ($userPath -split ';' | Where-Object { $_.TrimEnd('\') -ieq $binDir.TrimEnd('\') })) {
        [Environment]::SetEnvironmentVariable('PATH', "$binDir;$userPath", 'User')
        Say "added $binDir to your user PATH (open a new terminal to pick it up)"
    }
    if (-not ($env:PATH -split ';' | Where-Object { $_.TrimEnd('\') -ieq $binDir.TrimEnd('\') })) { $env:PATH = "$binDir;$env:PATH" }
}

& $exe version
Write-Host ''
Write-Host 'next: nebula setup   (installs SpacetimeDB and checks Unity), then cd into a Unity project and nebula init' -ForegroundColor Green
