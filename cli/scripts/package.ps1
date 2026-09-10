<#
.SYNOPSIS
  Builds the release archives of the Nebula CLI for every supported platform into cli/dist:
  nebula-<version>-win-x64.zip, -linux-x64.tar.gz, -linux-arm64.tar.gz, -osx-x64.tar.gz, -osx-arm64.tar.gz.
  These are what cli/install/install.{ps1,sh} download; attach them to a release tagged v<version>.

.EXAMPLE
  powershell -File cli/scripts/package.ps1
  powershell -File cli/scripts/package.ps1 -Rids win-x64,linux-x64
#>
param(
    [string[]]$Rids = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
)
$ErrorActionPreference = 'Stop'
$cli = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $cli 'Nebula.Cli\Nebula.Cli.csproj'
$dist = Join-Path $cli 'dist'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force -Path $dist | Out-Null

foreach ($rid in $Rids) {
    $out = Join-Path $dist "publish-$rid"
    Write-Host "[package] $rid" -ForegroundColor Cyan
    & dotnet publish $proj -c Release -r $rid -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid failed" }
    $name = "nebula-$version-$rid"
    if ($rid.StartsWith('win')) {
        $zip = Join-Path $dist "$name.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $out 'nebula.exe') -DestinationPath $zip
    } else {
        $tgz = Join-Path $dist "$name.tar.gz"
        Remove-Item $tgz -Force -ErrorAction SilentlyContinue
        # bsdtar (Windows 10+) keeps the mode bits we need; the install script chmods anyway.
        & tar -czf $tgz -C $out nebula
        if ($LASTEXITCODE -ne 0) { throw "tar $rid failed" }
    }
    Remove-Item $out -Recurse -Force
}
Write-Host "[package] done:" -ForegroundColor Green
Get-ChildItem $dist -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize | Out-Host
