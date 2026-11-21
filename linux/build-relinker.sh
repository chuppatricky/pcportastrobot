#!/usr/bin/env bash
# Builds the native Linux AnyPS5 relinker the Linux launcher runs as `relinker --windows ...`.
#
#   linux/build-relinker.sh <output-dir>
#
# Source: AnyPS5 at the pinned base commit plus this repository's Source/anyps5/*.patch, exactly as
# Source/README.md describes for the Windows build (the resulting tree hash is checked). Only the
# relinker target is built (ANYPS5_RELINKER_ONLY: no submodules needed), statically linked
# (AnyPS5's own default off macOS), -O2 -march=x86-64-v2.
#
# Environment:
#   ANYPS5_REPO   clone source (URL or local clone that contains the base commit);
#                 default https://github.com/boykopovar/AnyPS5.git
#   BUILD_DIR     where the source and the build go; default: a temporary directory, removed afterwards
#   CMAKE, CXX    tools to use; default: cmake and g++ from PATH
#
# The patches are applied with a fixed committer identity and date, so the commit ids, the
# `relinker --version` string and the binary itself are the same on every run.
set -euo pipefail

BASE_COMMIT=319621ba2e578bd053b0baba2877fe70520e0737
EXPECTED_TREE=5f146e8eecce1b21421e01cb4e82fd95377f96d2

if [ $# -ne 1 ]; then
    echo "usage: $0 <output-dir>" >&2
    exit 2
fi
out=$(realpath -m "$1")
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
patches="$here/../Source/anyps5"
repo=${ANYPS5_REPO:-https://github.com/boykopovar/AnyPS5.git}
cmake=${CMAKE:-cmake}
cxx=${CXX:-g++}

for tool in git "$cmake" ninja "$cxx"; do
    command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 1; }
done
compgen -G "$patches/*.patch" >/dev/null || { echo "no patches in $patches" >&2; exit 1; }

if [ -n "${BUILD_DIR:-}" ]; then
    work=$(realpath -m "$BUILD_DIR")
    mkdir -p "$work"
else
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
fi
src="$work/anyps5"
build="$work/build"

rm -rf "$src" "$build"
git clone --quiet --no-checkout "$repo" "$src"
git -C "$src" checkout --quiet --detach "$BASE_COMMIT"

# Fixed identity and dates: `git am` then makes the same commits everywhere.
export GIT_COMMITTER_NAME="AstroHybrid build" GIT_COMMITTER_EMAIL="build@astrohybrid.invalid"
git -C "$src" -c user.name="$GIT_COMMITTER_NAME" -c user.email="$GIT_COMMITTER_EMAIL" \
    am --quiet --keep-cr --committer-date-is-author-date "$patches"/*.patch
tree=$(git -C "$src" rev-parse 'HEAD^{tree}')
if [ "$tree" != "$EXPECTED_TREE" ]; then
    echo "patched tree is $tree, expected $EXPECTED_TREE" >&2
    exit 1
fi

# -ffile-prefix-map keeps the build directory out of the binary (assert strings, debug info).
flags="-O2 -march=x86-64-v2 -DNDEBUG -ffile-prefix-map=$src=. -ffile-prefix-map=$build=build"
"$cmake" -S "$src" -B "$build" -G Ninja -Wno-dev \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_CXX_COMPILER="$cxx" \
    -DCMAKE_CXX_FLAGS_RELEASE="$flags" \
    -DANYPS5_RELINKER_ONLY=ON \
    -DBUILD_TESTING=OFF >/dev/null
"$cmake" --build "$build" --target relinker >/dev/null

bin="$build/core/relinker/relinker"
if readelf -l "$bin" | grep -q INTERP; then
    echo "$bin is dynamically linked" >&2
    exit 1
fi
mkdir -p "$out"
install -m 755 "$bin" "$out/relinker"
"$out/relinker" --version
echo "tree $tree"
sha256sum "$out/relinker"
