#!/bin/sh
# Installs the Nebula CLI on Linux/macOS into ~/.nebula-cli/bin and adds it to PATH.
#
# Hosted at https://install.nebula.1by3.co and run with:
#     curl -sSf https://install.nebula.1by3.co | sh
#
# Three ways to get the binary, in this order of precedence:
#   1. --source <path> / $NEBULA_SOURCE    build from a checkout of the Nebula repository (needs the .NET 10 SDK)
#   2. --archive <path> / $NEBULA_ARCHIVE  install a local release archive (nebula-<version>-<rid>.tar.gz)
#   3. otherwise                           download the release from $NEBULA_RELEASE_BASE
#                                          (default: the Nebula repository's releases; $NEBULA_VERSION picks one)
#
# When piped into sh there are no arguments, so use the environment variables:
#     NEBULA_SOURCE=~/dev/nebula curl -sSf https://install.nebula.1by3.co | sh
#     sh cli/install/install.sh --source .        # from inside a checkout
set -eu

SOURCE="${NEBULA_SOURCE:-}"
ARCHIVE="${NEBULA_ARCHIVE:-}"
VERSION="${NEBULA_VERSION:-}"
RELEASE_BASE="${NEBULA_RELEASE_BASE:-https://github.com/1by3/nebula/releases/download}"
REPO_API="https://api.github.com/repos/1by3/nebula"
NO_PATH=0
while [ $# -gt 0 ]; do
  case "$1" in
    --source) SOURCE="$2"; shift 2 ;;
    --archive) ARCHIVE="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --release-base) RELEASE_BASE="$2"; shift 2 ;;
    --no-path) NO_PATH=1; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[36m[nebula-install]\033[0m %s\n' "$1"; }
fail() { printf '\033[31m[nebula-install]\033[0m %s\n' "$1" >&2; exit 1; }

case "$(uname -s)" in
  Linux)  os=linux ;;
  Darwin) os=osx ;;
  *) fail "unsupported OS: $(uname -s) (use install.ps1 on Windows)" ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) fail "unsupported architecture: $(uname -m)" ;;
esac
rid="$os-$arch"

CLI_HOME="${NEBULA_CLI_HOME:-$HOME/.nebula-cli}"
BIN_DIR="$CLI_HOME/bin"
EXE="$BIN_DIR/nebula"
mkdir -p "$BIN_DIR"
staging="$(mktemp -d 2>/dev/null || mktemp -d -t nebula-install)"
trap 'rm -rf "$staging"' EXIT

if [ -n "$SOURCE" ]; then
  # --- from a checkout ---------------------------------------------------------------------------------
  SOURCE="$(cd "$SOURCE" && pwd)"
  proj="$SOURCE/cli/Nebula.Cli/Nebula.Cli.csproj"
  [ -f "$proj" ] || fail "$SOURCE is not a Nebula checkout (no cli/Nebula.Cli/Nebula.Cli.csproj)"
  dotnet="$(command -v dotnet || true)"
  [ -n "$dotnet" ] || [ ! -x "$HOME/.dotnet/dotnet" ] || dotnet="$HOME/.dotnet/dotnet"
  [ -n "$dotnet" ] || fail "the .NET SDK (10.0 or newer) is required to build from source: https://dotnet.microsoft.com/download"
  say "building the CLI from $SOURCE ($rid)"
  "$dotnet" publish "$proj" -c Release -r "$rid" -o "$staging" --nologo -v quiet
elif [ -n "$ARCHIVE" ]; then
  # --- from a local release archive -----------------------------------------------------------------------
  say "extracting $ARCHIVE"
  tar -xzf "$ARCHIVE" -C "$staging"
else
  # --- download a release -------------------------------------------------------------------------------------
  command -v curl >/dev/null 2>&1 || fail "curl is required"
  if [ -z "$VERSION" ]; then
    VERSION="$(curl -sSf "$REPO_API/releases/latest" | sed -n 's/.*"tag_name":"\([^"]*\)".*/\1/p' | head -n1)" \
      || fail "could not look up the latest release. Set NEBULA_VERSION, or install from a checkout with NEBULA_SOURCE."
    [ -n "$VERSION" ] || fail "could not determine the latest release; set NEBULA_VERSION"
  fi
  case "$VERSION" in v*) tag="$VERSION" ;; *) tag="v$VERSION" ;; esac
  asset="nebula-${tag#v}-$rid.tar.gz"
  url="$RELEASE_BASE/$tag/$asset"
  say "downloading $url"
  curl -sSfL "$url" -o "$staging/$asset" || fail "download failed"
  tar -xzf "$staging/$asset" -C "$staging"
fi

built="$(find "$staging" -type f -name nebula | head -n1)"
[ -n "$built" ] || fail "no nebula binary in the build output / archive"
rm -f "$EXE"
cp "$built" "$EXE"
chmod 755 "$EXE"
say "installed $EXE"

# Remember the checkout so `nebula init --embed` copies the package from it instead of cloning.
[ -z "$SOURCE" ] || "$EXE" config source --path "$SOURCE"

# --- PATH ------------------------------------------------------------------------------------------------
if [ "$NO_PATH" -eq 0 ]; then
  line="export PATH=\"\$HOME/.nebula-cli/bin:\$PATH\""
  [ "$CLI_HOME" = "$HOME/.nebula-cli" ] || line="export PATH=\"$BIN_DIR:\$PATH\""
  added=""
  for rc in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
    [ -f "$rc" ] || continue
    grep -q 'nebula-cli/bin' "$rc" 2>/dev/null && continue
    printf '\n# Nebula CLI\n%s\n' "$line" >> "$rc"
    added="$added $rc"
  done
  [ -n "$added" ] && say "added $BIN_DIR to PATH in:$added (open a new shell, or run: $line)"
  case ":$PATH:" in *":$BIN_DIR:"*) ;; *) export PATH="$BIN_DIR:$PATH" ;; esac
fi

"$EXE" version
echo
printf '\033[32mnext: nebula setup   (installs SpacetimeDB and checks Unity), then cd into a Unity project and nebula init\033[0m\n'
