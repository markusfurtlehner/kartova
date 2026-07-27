#!/usr/bin/env bash
# Publishes Kartova as a self-contained application for one or more platforms.
#
# Each build embeds the .NET runtime, so the result runs on a machine with nothing
# installed. Windows and Linux produce a single executable; macOS produces a .app
# bundle, because that is the only form Finder will launch.
#
#   ./publish.sh                 # host platform only
#   ./publish.sh all             # every supported platform
#   ./publish.sh linux-arm64
#
# Adding "package" produces the release archives and a SHA256SUMS.txt beside them:
#
#   ./publish.sh all package     # everything, archived and checksummed
#
# .NET cross-publishes, so one machine can emit all six targets. What it cannot do is
# run them: a build for a platform is not evidence it works there.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO/src/Kartova.App/Kartova.App.csproj"
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
  local bundle="$OUTPUT_ROOT/$rid-bundle/Kartova.app"

  rm -rf "$bundle"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$publish_dir/." "$bundle/Contents/MacOS/"

  [ -f "$REPO/src/Kartova.App/Assets/kartova.png" ] &&
    cp "$REPO/src/Kartova.App/Assets/kartova.png" "$bundle/Contents/Resources/kartova.png"

  cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Kartova</string>
  <key>CFBundleDisplayName</key><string>Kartova</string>
  <key>CFBundleIdentifier</key><string>org.kartova.app</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>Kartova</string>
  <key>CFBundleIconFile</key><string>kartova</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRequiresAquaSystemAppearance</key><false/>
</dict>
</plist>
PLIST

  chmod +x "$bundle/Contents/MacOS/Kartova"
  echo "    bundled Kartova.app"
}

package_target() {
  local rid="$1" out="$2"
  local archive

  mkdir -p "$OUTPUT_ROOT/dist"
  case "$rid" in
    win-*)
      archive="$OUTPUT_ROOT/dist/Kartova-$rid.zip"
      rm -f "$archive"
      if command -v zip >/dev/null 2>&1; then
        (cd "$out" && zip -qr "$archive" .)
      else
        # Windows ships bsdtar, which writes zip given the format explicitly.
        (cd "$out" && tar -a -cf "$archive" .)
      fi
      ;;
    osx-*)
      # Archive the bundle, not the flat publish directory: Finder will not launch the
      # latter. tar preserves the executable bit, which a zip round-trip can lose.
      archive="$OUTPUT_ROOT/dist/Kartova-$rid.tar.gz"
      rm -f "$archive"
      tar -czf "$archive" -C "$OUTPUT_ROOT/$rid-bundle" Kartova.app
      ;;
    *)
      archive="$OUTPUT_ROOT/dist/Kartova-$rid.tar.gz"
      rm -f "$archive"
      tar -czf "$archive" -C "$out" .
      ;;
  esac
  printf '    packaged      %s\n' "$(basename "$archive")"
}

write_checksums() {
  local dist="$OUTPUT_ROOT/dist"
  [ -d "$dist" ] || return 0

  # Names only, so the file verifies from inside the directory it ships with.
  if command -v sha256sum >/dev/null 2>&1; then
    (cd "$dist" && sha256sum Kartova-* > SHA256SUMS.txt)
  elif command -v shasum >/dev/null 2>&1; then
    (cd "$dist" && shasum -a 256 Kartova-* > SHA256SUMS.txt)
  else
    echo "    no sha256 tool found; skipping checksums" >&2
    return 0
  fi
  echo
  echo "Checksums:"
  sed 's/^/    /' "$dist/SHA256SUMS.txt"
}

PACKAGE=0
for arg in "$@"; do
  [ "$arg" = "package" ] && PACKAGE=1
done

case "${1:-host}" in
  all)     TARGETS=("${ALL_RUNTIMES[@]}") ;;
  host|package) TARGETS=("$(host_runtime)") ;;
  *)       TARGETS=("$1") ;;
esac

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO/Directory.Build.props" | head -n1)"

echo "Kartova $VERSION"
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
    linux-*) chmod +x "$out/Kartova" ;;
  esac

  size=$(du -sm "$out" | cut -f1)
  printf '    %-14s %5s MB\n' "$rid" "$size"

  [ "$PACKAGE" = "1" ] && package_target "$rid" "$out"
done

[ "$PACKAGE" = "1" ] && write_checksums

echo
echo "Artifacts in $OUTPUT_ROOT"
