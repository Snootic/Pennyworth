#!/usr/bin/env bash
# Builds the Linux x64 release artefacts from this checkout:
#
#   pennyworth-<version>-linux-x64.tar.gz     self-contained tarball (bin/, lib/pennyworth/, share/)
#   Pennyworth-<version>-x86_64.AppImage      the same tree as an AppImage
#   SHA256SUMS
#
# Usage: tools/package/build-linux.sh [--version V] [--out DIR] [--aot] [--no-appimage]
#
#   --version V     goes into the file names (default: dev-<short git sha>)
#   --out DIR       where everything is written (default: <repo>/artifacts/linux-x64)
#   --aot           Native AOT publish instead of the self-contained runtime (needs clang, zlib headers, several GB of RAM)
#   --no-appimage   stop after the tarball and the AppDir (no download, no appimagetool)
#
# Needs: the .NET SDK from global.json, tar, gzip, sha256sum; and for the AppImage curl (or wget) and a Linux x64 host.
# PENNYWORTH_MAXCPU limits the MSBuild node count (default: MSBuild's own choice).
# The result does not depend on the date: file times come from the commit and the tarball is sorted and owned by root.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
here="$repo/tools/package/linux"
rid=linux-x64
version=""
out="$repo/artifacts/linux-x64"
aot=false
appimage=true

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version="${2:?--version needs a value}"; shift 2 ;;
        --out) out="${2:?--out needs a value}"; shift 2 ;;
        --aot) aot=true; shift ;;
        --no-appimage) appimage=false; shift ;;
        -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

# Pinned: a build next year must fetch the same bytes (hashes are the release assets' SHA-256).
appimagetool_url=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
appimagetool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

[ "$(uname -s)" = Linux ] || { echo "build-linux.sh runs on Linux" >&2; exit 1; }
if [ -z "$version" ]; then
    version="dev-$(git -C "$repo" rev-parse --short HEAD 2>/dev/null || echo unknown)"
fi
epoch="$(git -C "$repo" log -1 --format=%ct 2>/dev/null || date +%s)"
name="pennyworth-$version-$rid"

fetch() { # url sha256 destination
    if [ -f "$3" ] && [ "$(sha256sum "$3" | cut -d' ' -f1)" = "$2" ]; then return; fi
    if command -v curl >/dev/null; then curl -fsSL -o "$3.part" "$1"; else wget -q -O "$3.part" "$1"; fi
    echo "$2  $3.part" | sha256sum -c --quiet - || { rm -f "$3.part"; echo "checksum mismatch for $1" >&2; exit 1; }
    mv "$3.part" "$3"
}

stage="$out/stage"
prefix="$stage/$name"
rm -rf "$stage" "$out/publish"
mkdir -p "$prefix/bin" "$prefix/lib/pennyworth" "$prefix/share/applications" "$prefix/share/icons"

# --- publish -----------------------------------------------------------------------------------------------------
publish=(dotnet publish "$repo/src/Pennyworth.App" -c Release -r "$rid" --self-contained
    -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -o "$out/publish")
if $aot; then publish+=(-p:PublishAot=true); fi
if [ -n "${PENNYWORTH_MAXCPU:-}" ]; then publish+=("-maxcpucount:$PENNYWORTH_MAXCPU"); fi
echo "==> ${publish[*]}"
"${publish[@]}"
cp -a "$out/publish/." "$prefix/lib/pennyworth/"
rm -f "$prefix"/lib/pennyworth/*.dbg   # Native AOT's detached symbols: 85 MB nobody needs in a release
[ -x "$prefix/lib/pennyworth/Pennyworth" ] || { echo "publish did not produce lib/pennyworth/Pennyworth" >&2; exit 1; }

# --- the tree: the same prefix layout for the tarball and for the AppImage's usr/ ---------------------------------
ln -s ../lib/pennyworth/Pennyworth "$prefix/bin/pennyworth"
install -m 644 "$here/pennyworth.desktop" "$prefix/share/applications/pennyworth.desktop"
cp -a "$here/icons/hicolor" "$prefix/share/icons/hicolor"
install -m 644 "$repo/LICENSE" "$prefix/LICENSE"
install -m 644 "$here/README.txt" "$prefix/README.txt"

# Modes and times that do not depend on the build machine (NuGet's cache hands out odd ones).
find "$prefix" -type d -exec chmod 755 {} +
find "$prefix" -type f -exec chmod 644 {} +
find "$prefix/lib/pennyworth" -type f \( -name 'Pennyworth' -o -name createdump -o -name '*.so' \) -exec chmod 755 {} +
find "$prefix" -exec touch -h -d "@$epoch" {} +

# --- tarball ------------------------------------------------------------------------------------------------------
tarball="$out/$name.tar.gz"
tar --format=gnu --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" -cf - -C "$stage" "$name" | gzip -n -9 > "$tarball"

# --- AppDir and AppImage ------------------------------------------------------------------------------------------
appdir="$out/Pennyworth.AppDir"
rm -rf "$appdir"
mkdir -p "$appdir"
cp -a "$prefix" "$appdir/usr"
rm -f "$appdir/usr/LICENSE" "$appdir/usr/README.txt"
install -m 755 "$here/AppRun" "$appdir/AppRun"
install -m 644 "$here/pennyworth.desktop" "$appdir/pennyworth.desktop"
install -m 644 "$here/icons/hicolor/256x256/apps/pennyworth.png" "$appdir/pennyworth.png"
ln -s pennyworth.png "$appdir/.DirIcon"
find "$appdir" -exec touch -h -d "@$epoch" {} +

if $appimage; then
    cache="$out/tools"
    mkdir -p "$cache"
    fetch "$appimagetool_url" "$appimagetool_sha" "$cache/appimagetool"
    fetch "$runtime_url" "$runtime_sha" "$cache/runtime"
    chmod +x "$cache/appimagetool"
    image="$out/Pennyworth-$version-x86_64.AppImage"
    rm -f "$image"
    # Extract-and-run: appimagetool is itself an AppImage and this must work where FUSE is not available (CI, containers).
    ARCH=x86_64 SOURCE_DATE_EPOCH="$epoch" "$cache/appimagetool" --appimage-extract-and-run \
        --runtime-file "$cache/runtime" "$appdir" "$image"
fi

# --- result -------------------------------------------------------------------------------------------------------
files=("$name.tar.gz")
if $appimage; then files+=("Pennyworth-$version-x86_64.AppImage"); fi
(cd "$out" && sha256sum "${files[@]}" > SHA256SUMS)
echo
echo "==> $(if $aot; then echo 'Native AOT'; else echo 'self-contained'; fi) build in $out"
for file in "${files[@]}"; do stat -c '    %10s  %n' "$out/$file"; done
du -sh "$prefix" | awk '{ print "    unpacked tree: " $1 }'
