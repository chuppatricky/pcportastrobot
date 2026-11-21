#!/usr/bin/env bash
# Builds AstroHybrid-Linux-<version>-x86_64.AppImage: the native launcher (self-contained .NET 10),
# the native relinker, and the Windows game runtime as the payload the launcher syncs into its data dir.
#
#   linux/packaging/build-appimage.sh [options]
#     --version <v>       e.g. 1.2.10-linux.2 (default: the csproj's InformationalVersion)
#     --payload-zip <zip> upstream release zip to take the runtime from (Build/RunHybrid, licenses, README.txt)
#     --payload-dir <dir> or a folder laid out the same way (CI: the runtime built by windows-runtime.yml)
#     --relinker <file>   native relinker (default: build it with linux/build-relinker.sh)
#     --out <dir>         output folder (default: linux/out)
#     --appimagetool <f>  appimagetool AppImage (default: ~/.cache/github-to-appimage/tools/appimagetool-x86_64.AppImage)
#
# The payload leaves out what only the Windows launcher uses: AstroHybrid.exe and its .NET Framework DLLs,
# the .bat files, Run-AstroHybrid-Linux.sh and tools/relinker.exe (the AppImage has the native relinker).
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
linux=$(cd "$here/.." && pwd)
version="" payload_zip="" payload_dir="" relinker="" out="$linux/out"
appimagetool="$HOME/.cache/github-to-appimage/tools/appimagetool-x86_64.AppImage"
while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --payload-zip) payload_zip=$(realpath "$2"); shift 2 ;;
        --payload-dir) payload_dir=$(realpath "$2"); shift 2 ;;
        --relinker) relinker=$(realpath "$2"); shift 2 ;;
        --out) out=$(realpath -m "$2"); shift 2 ;;
        --appimagetool) appimagetool=$(realpath "$2"); shift 2 ;;
        *) echo "unknown option $1" >&2; exit 2 ;;
    esac
done
[ -n "$payload_zip$payload_dir" ] || { echo "give --payload-zip or --payload-dir" >&2; exit 2; }
[ -x "$appimagetool" ] || { echo "appimagetool not found at $appimagetool" >&2; exit 1; }
if [ -z "$version" ]; then
    version=$(sed -nE 's#.*<InformationalVersion>([^<]+)</InformationalVersion>.*#\1#p' "$linux/AstroHybrid.Linux/AstroHybrid.Linux.csproj")
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/AppDir"
mkdir -p "$app/usr/bin" "$app/usr/lib/astrohybrid" "$app/usr/share/astrohybrid/payload" \
         "$app/usr/share/applications" "$app/usr/share/icons/hicolor/256x256/apps"

echo "== launcher $version"
dotnet publish "$linux/AstroHybrid.Linux/AstroHybrid.Linux.csproj" -c Release -r linux-x64 --self-contained \
    -p:Version="$version" -p:InformationalVersion="$version" -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:DebugType=none -o "$app/usr/lib/astrohybrid" --nologo -v quiet
ln -s ../lib/astrohybrid/astrohybrid "$app/usr/bin/astrohybrid"

echo "== relinker"
if [ -z "$relinker" ]; then
    "$linux/build-relinker.sh" "$work/relinker" >/dev/null
    relinker="$work/relinker/relinker"
fi
install -m 755 "$relinker" "$app/usr/bin/relinker"

echo "== payload"
p="$app/usr/share/astrohybrid/payload"
if [ -n "$payload_zip" ]; then
    unzip -q "$payload_zip" 'Build/RunHybrid/*' 'licenses/*' README.txt -d "$p"
else
    cp -a "$payload_dir/Build" "$p/"
    [ -d "$payload_dir/licenses" ] && cp -a "$payload_dir/licenses" "$p/"
    [ -f "$payload_dir/README.txt" ] && cp "$payload_dir/README.txt" "$p/"
fi
# The game exes are made per player by the setup; a payload must never carry one.
find "$p" -iname 'AstroBot*.exe' -delete
[ -f "$p/Build/RunHybrid/kyty_gpu.dll" ] || { echo "payload has no Build/RunHybrid/kyty_gpu.dll" >&2; exit 1; }

echo "== desktop entry"
cat > "$app/usr/share/applications/astrohybrid.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=AstroHybrid
Comment=Astro Bot PC port launcher for Linux
Exec=astrohybrid
Icon=astrohybrid
Categories=Game;
Terminal=false
DESKTOP
cp "$app/usr/share/applications/astrohybrid.desktop" "$app/"
install -m 644 "$linux/../AstroHybrid.App/Resources/astrohybrid.png" "$app/usr/share/icons/hicolor/256x256/apps/astrohybrid.png"
cp "$app/usr/share/icons/hicolor/256x256/apps/astrohybrid.png" "$app/astrohybrid.png"
ln -s astrohybrid.png "$app/.DirIcon"
cat > "$app/AppRun" <<'APPRUN'
#!/bin/sh
# The launcher is a self-contained .NET program; it finds its payload and relinker through $APPDIR.
HERE=$(dirname "$(readlink -f "$0")")
export APPDIR="${APPDIR:-$HERE}"
exec "$HERE/usr/lib/astrohybrid/astrohybrid" "$@"
APPRUN
chmod 755 "$app/AppRun"

echo "== appimage"
# dotnet publish leaves some files 0744 and mktemp's dir is 0700: an AppImage must be readable by everyone.
chmod -R a+rX "$app"
mkdir -p "$out"
name="AstroHybrid-Linux-$version-x86_64.AppImage"
(cd "$out" && ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" --comp zstd \
    -u "gh-releases-zsync|chuppatricky|pcportastrobot|latest|AstroHybrid-Linux-*x86_64.AppImage.zsync" \
    "$app" "$out/$name" >/dev/null)
ls -la "$out/$name"* | awk '{print $5, $9}'
sha256sum "$out/$name"
