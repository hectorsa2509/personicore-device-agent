#!/usr/bin/env bash
#
# Builds the packages that are handed to panelists and leaves them in this folder.
#
#   ./build.sh            both platforms · 4 zips
#   ./build.sh windows    win-x64 + win-arm64
#   ./build.sh mac        macos-arm64 + macos-x64
#
# The zips are copies: the originals stay in windows/dist and macos/dist, and the
# copies here are gitignored, so this never dirties the repository.
#
# Windows builds from any OS (the .csproj sets EnableWindowsTargeting). macOS does not:
# the bundle needs codesign and iconutil, which only exist on a Mac.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
target="${1:-all}"

case "$target" in
    all|windows|win|mac|macos) ;;
    -h|--help|help)
        sed -n '3,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
        exit 0 ;;
    *)
        echo "Unknown target: $target" >&2
        echo "Use: ./build.sh [all|windows|mac]" >&2
        exit 2 ;;
esac

build_windows() {
    echo "── Windows · win-x64 and win-arm64 ────────────────────────────────"
    "$here/windows/build/publish.sh" > /dev/null
    cp "$here/windows/dist/PersonicoreDeviceAgent-win-x64.zip" "$here/"
    cp "$here/windows/dist/PersonicoreDeviceAgent-win-arm64.zip" "$here/"
}

build_mac() {
    if [[ "$(uname -s)" != "Darwin" ]]; then
        echo "The macOS bundle can only be built on a Mac: it needs codesign and iconutil." >&2
        exit 1
    fi

    echo "── macOS · arm64 and x64 ──────────────────────────────────────────"
    "$here/macos/build/make-app.sh" > /dev/null
    cp "$here/macos/dist/PersonicoreDeviceAgent-macos-arm64.zip" "$here/"
    cp "$here/macos/dist/PersonicoreDeviceAgent-macos-x64.zip" "$here/"
}

case "$target" in
    windows|win) build_windows ;;
    mac|macos)   build_mac ;;
    all)         build_windows; build_mac ;;
esac

echo
echo "Ready to share:"
for zip in "$here"/PersonicoreDeviceAgent-*.zip; do
    [[ -e "$zip" ]] || continue

    # A zip that cannot be opened is worse than no zip: it is found by the panelist.
    if unzip -tq "$zip" > /dev/null 2>&1; then status="ok"; else status="CORRUPT"; fi
    printf '  %-42s %6s  %s\n' "$(basename "$zip")" "$(du -h "$zip" | cut -f1)" "$status"
done
