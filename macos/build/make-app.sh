#!/usr/bin/env bash
#
# Builds "Personicore Device Agent.app" for macOS, one per architecture:
#
#   dist/arm64/Personicore Device Agent.app   Apple silicon (M1 and later)
#   dist/x64/Personicore Device Agent.app     Intel Macs
#
# Self-contained: no .NET needed on the panelist's Mac.
#
# How the bundle is signed depends on what this Mac has, and the build says which on
# stderr. With a Developer ID Application certificate installed it is signed with the
# hardened runtime, and with PERSONICORE_NOTARY_PROFILE also set it is notarized and
# stapled — that combination is what a panelist opens with a double click. With neither
# it is signed ad hoc, which runs on the Mac that built it and nowhere else. See
# ../README.md for the one-time setup.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$here/../src/Personicore.DeviceAgent.Mac/Personicore.DeviceAgent.Mac.csproj"
output="$here/../dist"
app_name="Personicore Device Agent"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project")"

# The signing settings live alongside the packaged credential in packaging.env, so
# reading it here is what lets the notarization check below see a profile the developer
# put in that file rather than in their shell.
source "$here/../../build/read-env-file.sh"
read_env_file "$here/../../packaging.env"

rm -rf "$output"
mkdir -p "$output"

# ── Icon: .icns from the executable icon the Windows build already generates ──
iconset="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$iconset"
python3 - "$here/../../windows/src/Personicore.DeviceAgent/Resources/app.ico" "$iconset" <<'PY'
import sys
from PIL import Image
ico, out = sys.argv[1], sys.argv[2]
source = Image.open(ico)
source.size = max(source.info.get("sizes", {source.size}))
source = source.convert("RGBA")
for size in (16, 32, 128, 256, 512):
    for scale in (1, 2):
        px = size * scale
        name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
        source.resize((px, px), Image.LANCZOS).save(f"{out}/{name}")
PY
iconutil -c icns "$iconset" -o "$output/AppIcon.icns"

for arch in arm64 x64; do
  publish="$(mktemp -d)"
  dotnet publish "$project" -c Release -r "osx-$arch" --self-contained true -o "$publish" \
    -p:UseAppHost=true -p:DebugType=none -v q

  bundle="$output/$arch/$app_name.app"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$publish/." "$bundle/Contents/MacOS/"
  # In-process UserNotifications bridge: the permission belongs to this app, not osascript.
  native_arch="$arch"
  [[ "$arch" != "x64" ]] || native_arch="x86_64"
  xcrun clang -arch "$native_arch" -mmacosx-version-min=12.0 -fobjc-arc -fblocks -dynamiclib \
    -framework Foundation -framework UserNotifications \
    -install_name @rpath/libPersonicoreNotifications.dylib \
    "$here/../native/notifications.m" -o "$bundle/Contents/MacOS/libPersonicoreNotifications.dylib"
  cp "$output/AppIcon.icns" "$bundle/Contents/Resources/AppIcon.icns"

  cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>                 <string>$app_name</string>
  <key>CFBundleDisplayName</key>          <string>$app_name</string>
  <key>CFBundleIdentifier</key>           <string>com.personicore.personicore.deviceagent</string>
  <key>CFBundleExecutable</key>           <string>PersonicoreDeviceAgent</string>
  <key>CFBundleIconFile</key>             <string>AppIcon</string>
  <key>CFBundlePackageType</key>          <string>APPL</string>
  <key>CFBundleShortVersionString</key>   <string>$version</string>
  <key>CFBundleVersion</key>              <string>$version</string>
  <key>LSMinimumSystemVersion</key>       <string>12.0</string>
  <!-- Menu bar agent: no Dock icon, no app menu. -->
  <key>LSUIElement</key>                  <true/>
  <key>NSHighResolutionCapable</key>      <true/>
  <!-- macOS 15+ asks before any LAN access; this is the text the panelist reads.
       Denying it means the Personicore router is never found. -->
  <key>NSLocalNetworkUsageDescription</key>
  <string>Personicore needs to find the Personicore router on your home network to register this computer.</string>
</dict>
</plist>
PLIST

  cp "$here/../../windows/.env.example" "$output/$arch/.env.example"
  cp "$here/../INSTALL.txt" "$output/$arch/INSTALL.txt"

  # The real .env goes next to the executable, which inside a bundle means
  # Contents/MacOS — that is where EnvSettings looks first. It has to be written before
  # codesign: the signature seals the bundle's contents, so a file added afterwards
  # invalidates it and the Mac kills the app on launch.
  "$here/../../build/write-packaged-env.sh" "$bundle/Contents/MacOS"

  # Signature over the whole bundle; Apple silicon refuses unsigned code either way.
  # Developer ID with hardened runtime when an identity is installed, ad hoc otherwise —
  # sign-app.sh decides and says which on stderr.
  echo "── $arch ──" >&2
  "$here/sign-app.sh" "$bundle"

  zip="$output/PersonicoreDeviceAgent-macos-$arch.zip"
  (cd "$output/$arch" && ditto -c -k --keepParent "$app_name.app" "$zip")

  # Notarization is opt-in on the credential being present, because it costs minutes per
  # architecture and talks to Apple: a development build must not pay for it. With no
  # profile the package is still legitimate — it is simply the one that needs
  # Privacy & Security → Open Anyway on the panelist's Mac.
  #
  # notarize-app.sh rebuilds the zip from the stapled bundle: the ticket attaches to the
  # .app, so the archive made two lines above is not the one that ships.
  if [[ -n "${PERSONICORE_NOTARY_PROFILE:-}" ]]; then
    "$here/notarize-app.sh" "$bundle" "$zip"
  else
    echo "  not notarized: PERSONICORE_NOTARY_PROFILE is unset. Panelists will need" >&2
    echo "  System Settings → Privacy & Security → Open Anyway on first launch." >&2
  fi

  rm -rf "$publish"
done

rm -f "$output/AppIcon.icns"
echo
echo "Built:"
find "$output" -maxdepth 2 -name "*.app" -o -maxdepth 1 -name "*.zip" | sed "s|$output/|  dist/|"
