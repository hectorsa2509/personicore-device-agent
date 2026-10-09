#!/usr/bin/env bash
#
# Notarizes an already Developer ID-signed bundle and staples the ticket to it.
#
#   notarize-app.sh "<path>/Personicore Device Agent.app" [<path>/package.zip]
#
# With a zip given, that zip is the one submitted and it is rebuilt from the stapled
# bundle afterwards. Without one, a temporary zip is used and only the .app ends up
# stapled.
#
# Needs a notarytool credential stored in the keychain once, on each Mac that builds:
#
#   xcrun notarytool store-credentials personicore-notary \
#       --apple-id "you@example.com" --team-id TEAMID1234
#
# (the password it asks for is an app-specific password from appleid.apple.com, not the
# Apple ID's own), and then:
#
#   PERSONICORE_NOTARY_PROFILE   the profile name, e.g. personicore-notary. Unset means
#                                "do not notarize", which is what make-app.sh checks.
#
# Read from the environment or `packaging.env`, the environment winning.
#
# Why staple at all: without a stapled ticket the Mac asks Apple at first launch, so a
# panelist opening the app on a network that cannot reach Apple — or offline — gets the
# Gatekeeper refusal the notarization was meant to remove. The ticket makes it local.
#
set -euo pipefail

bundle="${1:?usage: notarize-app.sh <path to .app> [<zip to rebuild>]}"
zip="${2:-}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

[[ -d "$bundle" ]] || { echo "No bundle at: $bundle" >&2; exit 1; }

source "$root/build/read-env-file.sh"
read_env_file "$root/packaging.env"

profile="${PERSONICORE_NOTARY_PROFILE:-}"
if [[ -z "$profile" ]]; then
  echo "ERROR: PERSONICORE_NOTARY_PROFILE is not set; nothing to notarize with." >&2
  exit 1
fi

# An ad hoc signature is accepted by `notarytool submit` and then rejected by the
# service, several minutes later, with a message about the signing certificate. Checking
# here turns that into one line now.
if ! codesign -dvv "$bundle" 2>&1 | grep -q "^Authority=Developer ID Application"; then
  echo "ERROR: $(basename "$bundle") is not signed with a Developer ID Application" >&2
  echo "       certificate, so notarization would be refused. Sign it first:" >&2
  echo "         macos/build/sign-app.sh \"$bundle\"" >&2
  exit 1
fi

# ── Submit ───────────────────────────────────────────────────────────────────────────
#
# ditto, not `zip`: the archive has to preserve the bundle's symlinks and the extended
# attributes codesign wrote, and `zip` flattens both — the service then reports the
# signature as invalid on a bundle that verifies locally.
submission="$zip"
scratch=""
if [[ -z "$submission" ]]; then
  scratch="$(mktemp -d)"
  submission="$scratch/$(basename "$bundle").zip"
  ditto -c -k --keepParent "$bundle" "$submission"
fi

echo "  notarizing $(basename "$bundle") · profile=$profile · this takes a few minutes" >&2

# --wait blocks until Apple answers. Without it the build "succeeds" with a package that
# is still in the queue, and whether it was accepted is discovered by a panelist.
log="$(mktemp)"
if ! xcrun notarytool submit "$submission" --keychain-profile "$profile" --wait \
       --output-format json > "$log" 2>&1; then
  cat "$log" >&2
  echo "ERROR: notarytool submit failed." >&2
  rm -f "$log"
  [[ -z "$scratch" ]] || rm -rf "$scratch"
  exit 1
fi

status="$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$log" | tail -1)"
id="$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$log" | head -1)"
rm -f "$log"

if [[ "$status" != "Accepted" ]]; then
  echo "ERROR: notarization came back \"$status\" (submission $id)." >&2
  echo "       What Apple objected to:" >&2
  xcrun notarytool log "$id" --keychain-profile "$profile" 2>&1 | sed 's/^/       /' >&2
  [[ -z "$scratch" ]] || rm -rf "$scratch"
  exit 1
fi

# ── Staple, then rebuild the zip ─────────────────────────────────────────────────────
#
# The ticket attaches to the .app, never to the zip — which is why the zip that was
# submitted is not the zip that ships. Rebuild it from the stapled bundle or panelists
# get the unstapled one.
xcrun stapler staple "$bundle" >/dev/null
xcrun stapler validate "$bundle" >/dev/null

if [[ -n "$zip" ]]; then
  rm -f "$zip"
  (cd "$(dirname "$bundle")" && ditto -c -k --keepParent "$(basename "$bundle")" "$zip")
fi

[[ -z "$scratch" ]] || rm -rf "$scratch"

# The last word on whether a panelist's Mac will open this without complaint. "Notarized
# Developer ID" is the string to look for; "Developer ID" alone means the staple did not
# take.
assessment="$(spctl -a -t exec -vvv "$bundle" 2>&1 | sed -n 's/^source=//p')"
echo "  notarized and stapled · submission=$id · Gatekeeper source=\"$assessment\"" >&2
