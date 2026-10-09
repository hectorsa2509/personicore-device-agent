#!/usr/bin/env bash
#
# Publishes the Windows agent. Works the same from macOS, Linux or Windows: the
# .csproj sets EnableWindowsTargeting, so no Windows machine is needed to produce the
# executable — only to run it.
#
#   ./publish.sh                  → dist/win-x64 and dist/win-arm64, one self-contained .exe
#                                   each, plus the .zip that is handed to panelists
#   ./publish.sh --framework      → small .exe, requires .NET Desktop Runtime 9 installed
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$here/../src/Personicore.DeviceAgent/Personicore.DeviceAgent.csproj"
output="$here/../dist"

mode="${1:-selfcontained}"

# The icon is regenerated if Pillow is available; otherwise the existing resources are used.
if python3 -c 'import PIL' 2>/dev/null; then
  python3 "$here/make-icon.py"
else
  echo "Warning: no python3 + Pillow, using the icons already generated in Resources/." >&2
fi

# The credential is compiled into the executable, so it has to be in place before the
# first publish and gone again afterwards — including when a publish fails, or the next
# build would embed it without meaning to. The panelist gets one file with nothing beside
# it: nothing to lose, nothing to leave behind when the .exe is copied, nothing to edit.
resource="$here/../../core/Personicore.DeviceAgent.Core/Config"
trap 'rm -f "$resource/packaged.env"' EXIT
"$here/../../build/write-packaged-env.sh" "$resource" packaged.env

# x64 for the vast majority; native arm64 for Windows on ARM (Snapdragon, Surface
# Pro X). Windows 10 on ARM does not emulate x64, so the x64 .exe does not start there.
for rid in win-x64 win-arm64; do
  out="$output/$rid"
  if [[ "$mode" == "--framework" ]]; then
    dotnet publish "$project" -c Release -r "$rid" -o "$out" \
      -p:SelfContained=false -p:PublishSingleFile=true
  else
    dotnet publish "$project" -c Release -r "$rid" -o "$out"
  fi

  cp "$here/../INSTALL.txt" "$out/INSTALL.txt"
  rm -f "$out"/*.pdb

  # This directory is reused between builds, and earlier builds shipped the credential as
  # a loose file. Leaving one behind would hand a panelist a token this build never meant
  # to send, and an .env next to the executable wins over the one compiled into it.
  rm -f "$out/.env" "$out/.env.txt" "$out/.env.example"

  # The zip is what actually reaches a panelist, so it is built here and not by hand:
  # a zip older than the .exe next to it is a build nobody meant to ship.
  rm -f "$output/PersonicoreDeviceAgent-$rid.zip"
  (cd "$out" && zip -q -r "../PersonicoreDeviceAgent-$rid.zip" .)
done

echo
echo "Published to: $output"
ls -lhR "$output"
