#!/bin/sh
# Builds svc-truth as a native, ahead-of-time compiled program for macOS on Apple silicon and installs it
# as ~/.local/bin/svc-truth. Safe to run again: each run rebuilds and replaces the installed copy.
#
# Set SVC_TRUTH_INSTALL_DIR to install somewhere other than ~/.local/bin.
set -eu

repo_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
install_dir=${SVC_TRUTH_INSTALL_DIR:-"$HOME/.local/bin"}
runtime=osx-arm64

fail() {
  printf 'install.sh: %s\n' "$1" >&2
  exit 1
}

[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] ||
  fail "this installer builds for macOS on Apple silicon (arm64) only"
command -v dotnet >/dev/null 2>&1 ||
  fail "the .NET SDK was not found; install .NET 10 from https://dotnet.microsoft.com/download"
xcrun --find clang >/dev/null 2>&1 ||
  fail "native compilation needs the Xcode command line tools; install them with: xcode-select --install"

build_dir=$(mktemp -d "${TMPDIR:-/tmp}/svc-truth-build.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

printf 'Building svc-truth (%s, native AOT)...\n' "$runtime"
dotnet publish "$repo_dir/src/SvcTruth/SvcTruth.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --output "$build_dir" \
  --nologo \
  --verbosity quiet

binary="$build_dir/svc-truth"
[ -x "$binary" ] || fail "the build did not produce $binary"
version=$("$binary" --version) || fail "the built program did not run"

mkdir -p "$install_dir"
# Copy next to the destination and rename over it, so an existing copy is replaced in one step and a running
# copy is never overwritten in place.
staged="$install_dir/.svc-truth.$$"
cp "$binary" "$staged"
chmod 0755 "$staged"
mv -f "$staged" "$install_dir/svc-truth"

printf 'Installed %s to %s/svc-truth\n' "$version" "$install_dir"
# shellcheck disable=SC2016 # $PATH is meant literally in the suggested line.
case ":$PATH:" in
  *":$install_dir:"*) ;;
  *) printf 'Note: %s is not on your PATH; add it in your shell profile, for example:\n  export PATH="%s:$PATH"\n' "$install_dir" "$install_dir" ;;
esac
