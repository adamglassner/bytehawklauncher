#!/usr/bin/env bash
# Builds the Linux launcher as an AppImage and a .tar.gz.
#   packaging/desktop/build-linux.sh <version> <linux-x64|linux-arm64>
set -euo pipefail
V="$1"; RID="$2"; ARCH="${RID#linux-}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/dist-desktop"; PUB="$OUT/publish-$RID"
rm -rf "$PUB"; mkdir -p "$OUT"

dotnet publish "$ROOT/src/BytehawkStudioLauncher.Desktop/BytehawkStudioLauncher.Desktop.csproj" -c Release -r "$RID" --self-contained true \
  -p:Version="$V" -p:AssemblyVersion="$V.0" -p:FileVersion="$V.0" -p:InformationalVersion="$V" -o "$PUB"
chmod +x "$PUB/BytehawkStudioLauncher"

# .tar.gz: one folder, plus the menu entry and icon for anyone who wants them
STAGE="$OUT/tar-$RID/BytehawkStudioLauncher"; rm -rf "$OUT/tar-$RID"; mkdir -p "$STAGE"
cp -R "$PUB/." "$STAGE/"
cp "$ROOT/packaging/desktop/bytehawk-studio-launcher.desktop" "$STAGE/"
cp "$ROOT/assets/bytehawk-appicon-256.png" "$STAGE/bytehawk-studio-launcher.png"
tar -C "$OUT/tar-$RID" -czf "$OUT/BytehawkStudioLauncher-$V-linux-$ARCH.tar.gz" BytehawkStudioLauncher

# AppImage
APPDIR="$OUT/AppDir-$RID"; rm -rf "$APPDIR"; mkdir -p "$APPDIR/usr/bin"
cp -R "$PUB/." "$APPDIR/usr/bin/"
cp "$ROOT/packaging/desktop/bytehawk-studio-launcher.desktop" "$APPDIR/"
cp "$ROOT/assets/bytehawk-appicon-256.png" "$APPDIR/bytehawk-studio-launcher.png"
cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/BytehawkStudioLauncher" "$@"
RUN
chmod +x "$APPDIR/AppRun"
TOOL="$OUT/appimagetool"
if [ ! -x "$TOOL" ]; then
  curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$( [ "$ARCH" = arm64 ] && echo aarch64 || echo x86_64 ).AppImage"
  chmod +x "$TOOL"
fi
ARCH=$( [ "$ARCH" = arm64 ] && echo aarch64 || echo x86_64 ) "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT/BytehawkStudioLauncher-$V-linux-$ARCH.AppImage"
ls -la "$OUT"
