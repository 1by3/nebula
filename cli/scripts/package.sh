#!/bin/sh
# Builds the release archives of the Nebula CLI for every supported platform into cli/dist (see package.ps1).
#   sh cli/scripts/package.sh [rid ...]
set -eu
cli="$(cd "$(dirname "$0")/.." && pwd)"
proj="$cli/Nebula.Cli/Nebula.Cli.csproj"
dist="$cli/dist"
version="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$proj" | head -n1)"
rids="${*:-win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64}"
mkdir -p "$dist"
for rid in $rids; do
  out="$dist/publish-$rid"
  printf '\033[36m[package]\033[0m %s\n' "$rid"
  dotnet publish "$proj" -c Release -r "$rid" -o "$out" --nologo -v quiet
  name="nebula-$version-$rid"
  case "$rid" in
    win-*) rm -f "$dist/$name.zip"; (cd "$out" && zip -q "$dist/$name.zip" nebula.exe) ;;
    *) rm -f "$dist/$name.tar.gz"; chmod 755 "$out/nebula"; tar -czf "$dist/$name.tar.gz" -C "$out" nebula ;;
  esac
  rm -rf "$out"
done
printf '\033[32m[package] done:\033[0m\n'
ls -la "$dist"
