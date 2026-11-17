#!/usr/bin/env bash
# Builds "Bytehawk Studio Launcher.app" for one architecture and packs it as .zip and .dmg.
#   packaging/desktop/build-mac.sh <version> <osx-arm64|osx-x64>
set -euo pipefail
V="$1"; RID="$2"; ARCH="${RID#osx-}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/dist-desktop"; PUB="$OUT/publish-$RID"; APP="$OUT/$RID/Bytehawk Studio Launcher.app"
rm -rf "$PUB" "$OUT/$RID"; mkdir -p "$OUT"

dotnet publish "$ROOT/src/BytehawkStudioLauncher.Desktop/BytehawkStudioLauncher.Desktop.csproj" -c Release -r "$RID" --self-contained true \
  -p:Version="$V" -p:AssemblyVersion="$V.0" -p:FileVersion="$V.0" -p:InformationalVersion="$V" -p:UseAppHost=true -o "$PUB"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$V/g" "$ROOT/packaging/desktop/Info.plist" > "$APP/Contents/Info.plist"

# App icon from the logo PNGs
ICONSET="$OUT/AppIcon-$RID.iconset"; rm -rf "$ICONSET"; mkdir -p "$ICONSET"
SRC="$ROOT/assets/bytehawk-appicon-1024.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# No Apple Developer ID yet: sign ad hoc (Apple Silicon won't run unsigned code at all).
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP" && echo "signature ok"

ZIP="$OUT/BytehawkStudioLauncher-$V-macOS-$ARCH.zip"
rm -f "$ZIP"; ditto -c -k --keepParent "$APP" "$ZIP"
DMGDIR="$OUT/dmg-$RID"; rm -rf "$DMGDIR"; mkdir -p "$DMGDIR"
cp -R "$APP" "$DMGDIR/"; ln -s /Applications "$DMGDIR/Applications"
hdiutil create -volname "Bytehawk Studio Launcher" -srcfolder "$DMGDIR" -ov -format UDZO "$OUT/BytehawkStudioLauncher-$V-macOS-$ARCH.dmg"
ls -la "$OUT"
