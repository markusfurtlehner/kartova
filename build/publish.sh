#!/usr/bin/env bash
# Publishes DirStat as a self-contained application for one or more platforms.
#
# Each build embeds the .NET runtime, so the result runs on a machine with nothing
# installed. Windows and Linux produce a single executable; macOS produces a .app
# bundle, because that is the only form Finder will launch.
#
#   ./publish.sh                 # host platform only
#   ./publish.sh all             # every supported platform
#   ./publish.sh linux-arm64
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO/src/DirStat.App/DirStat.App.csproj"
OUTPUT_ROOT="${OUTPUT_ROOT:-$REPO/artifacts}"
CONFIGURATION="${CONFIGURATION:-Release}"

ALL_RUNTIMES=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

host_runtime() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    *)      os=win ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    *)             arch=x64 ;;
  esac
  echo "$os-$arch"
}

make_app_bundle() {
  local rid="$1" publish_dir="$2" version="$3"
  local bundle="$OUTPUT_ROOT/$rid-bundle/DirStat.app"

  rm -rf "$bundle"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$publish_dir/." "$bundle/Contents/MacOS/"

  [ -f "$REPO/src/DirStat.App/Assets/dirstat.png" ] &&
    cp "$REPO/src/DirStat.App/Assets/dirstat.png" "$bundle/Contents/Resources/dirstat.png"

  cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DirStat</string>
  <key>CFBundleDisplayName</key><string>DirStat</string>
  <key>CFBundleIdentifier</key><string>org.dirstat.app</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>DirStat</string>
  <key>CFBundleIconFile</key><string>dirstat</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRequiresAquaSystemAppearance</key><false/>
</dict>
</plist>
PLIST

  chmod +x "$bundle/Contents/MacOS/DirStat"
  echo "    bundled DirStat.app"
}

case "${1:-host}" in
  all)  TARGETS=("${ALL_RUNTIMES[@]}") ;;
  host) TARGETS=("$(host_runtime)") ;;
  *)    TARGETS=("$1") ;;
esac

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO/Directory.Build.props" | head -n1)"

echo "DirStat $VERSION"
echo "Publishing: ${TARGETS[*]}"
echo

for rid in "${TARGETS[@]}"; do
  out="$OUTPUT_ROOT/$rid"
  echo "==> $rid"
  rm -rf "$out"

  # PublishSelfContained switches on the single-file, trimmed profile in the csproj.
  dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$rid" \
    -o "$out" \
    -p:PublishSelfContained=true \
    --nologo -v quiet

  case "$rid" in
    osx-*) make_app_bundle "$rid" "$out" "$VERSION" ;;
    linux-*) chmod +x "$out/DirStat" ;;
  esac

  size=$(du -sm "$out" | cut -f1)
  printf '    %-14s %5s MB\n' "$rid" "$size"
done

echo
echo "Artifacts in $OUTPUT_ROOT"
