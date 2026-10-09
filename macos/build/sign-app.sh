#!/usr/bin/env bash
#
# Signs one "Personicore Device Agent.app" bundle.
#
#   sign-app.sh "<path>/Personicore Device Agent.app"
#
# Two outcomes, and the build says which one happened on stderr:
#
#   Developer ID   a real identity was found: hardened runtime + entitlements +
#                  secure timestamp. This is the only signature notarization accepts,
#                  and therefore the only one a panelist's Mac opens without
#                  Privacy & Security → Open Anyway.
#   ad hoc         no identity: `--sign -`. Enough to run on the Mac that built it,
#                  which is what a development build needs. Gatekeeper refuses it
#                  anywhere else.
#
# Configuration, from the environment or from `packaging.env` in the repository root
# (gitignored), the environment winning:
#
#   PERSONICORE_SIGN_IDENTITY   The identity to use: the full name
#                               ("Developer ID Application: Acme Inc (TEAMID1234)") or
#                               its SHA-1 hash. Unset and exactly one Developer ID
#                               Application certificate is installed → that one is used.
#   PERSONICORE_SIGN_ADHOC      true forces the ad hoc signature even when an identity
#                               exists. For a local build that is never leaving the Mac.
#
set -euo pipefail

bundle="${1:?usage: sign-app.sh <path to .app>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
entitlements="$here/agent.entitlements"

[[ -d "$bundle" ]] || { echo "No bundle at: $bundle" >&2; exit 1; }

source "$root/build/read-env-file.sh"
read_env_file "$root/packaging.env"

# ── Which identity ───────────────────────────────────────────────────────────────────
identity="${PERSONICORE_SIGN_IDENTITY:-}"

adhoc_reason="no Developer ID identity"

if [[ "${PERSONICORE_SIGN_ADHOC:-}" == "true" ]]; then
  identity="-"
  adhoc_reason="PERSONICORE_SIGN_ADHOC=true"
elif [[ -z "$identity" ]]; then
  # Auto-discovery is a convenience, never a guess: with two Developer ID certificates
  # installed (an expired one and its replacement is the common case) picking either
  # would be arbitrary, and the wrong one produces a package that notarizes and then
  # fails to open. Ask instead.
  #
  # The hash, not the name, is what gets passed to codesign: certificate common names
  # contain parentheses and spaces, and quoting them through two scripts is one escaping
  # bug away from signing with nothing.
  #
  # Newline-separated text rather than an array, because macOS ships bash 3.2 and these
  # scripts run under it: no `mapfile`, and `${#array[@]}` on an empty array trips
  # `set -u`.
  # `|| true` because no certificate installed is the normal case on a development Mac,
  # and a grep that matches nothing exits 1 — which under `pipefail` ended the build with
  # no message at all.
  candidates="$(
    security find-identity -v -p codesigning 2>/dev/null \
      | grep "Developer ID Application" \
      | sed -n 's/^[[:space:]]*[0-9]*)[[:space:]]*\([0-9A-F]\{40\}\).*/\1/p' || true
  )"
  found="$(printf '%s' "$candidates" | grep -c . || true)"

  case "$found" in
    0) identity="-" ;;
    1) identity="$candidates" ;;
    *)
      echo "ERROR: $found Developer ID Application certificates are installed." >&2
      echo "       Set PERSONICORE_SIGN_IDENTITY to the one to use. Installed:" >&2
      security find-identity -v -p codesigning | grep "Developer ID Application" >&2
      exit 1 ;;
  esac
fi

if [[ "$identity" == "-" ]]; then
  echo "  signing ad hoc: $adhoc_reason. This build runs on THIS Mac only." >&2
else
  [[ -f "$entitlements" ]] || { echo "Missing entitlements: $entitlements" >&2; exit 1; }
fi

# ── One --deep signature over the bundle ─────────────────────────────────────────────
#
# `--deep` is normally the wrong tool — Apple documents it as a fallback, because for a
# bundle with nested bundles it signs each one with the OUTER bundle's entitlements. For
# this bundle it is the only tool, and the reason is the .NET apphost: it requires its
# assemblies, `PersonicoreDeviceAgent.deps.json` and `.runtimeconfig.json` to sit in its
# own directory, so all 218 files live in Contents/MacOS. codesign treats everything in
# there as nested code — not only the 19 Mach-O binaries but every managed `.dll` and
# even the `.runtimeconfig.json` — and signing the bundle without --deep fails with
# "code object is not signed at all / In subcomponent: …". Measured, not assumed.
#
# Apple's caveat does not bite here: there are no nested bundles for --deep to mis-sign.
# One signing pass, the same options reaching every binary inside, which for Developer ID
# means every one of the 19 gets the hardened runtime.
#
# Two consequences worth knowing. The main executable must NOT be signed separately
# first: codesign resolves `Contents/MacOS/PersonicoreDeviceAgent` to the bundle that
# encloses it and signs that instead, which fails for the reason above. And `--timestamp`
# asks Apple's timestamp service once per file, so a Developer ID signature over this
# bundle takes minutes rather than the second the ad hoc one takes.
#
# --force is required: libcoreclr.dylib and friends arrive already signed by Microsoft,
# and without it codesign leaves those signatures in place.
#
# This is also where the "._.env" inside the zip comes from: codesign records seal data in
# extended attributes on bundle files that are not Mach-O, and ditto archives those as an
# AppleDouble companion. Normal for any signed bundle, harmless — the agent looks for
# ".env" by name — and not removable afterwards without invalidating the signature.
#
# Quiet on success: codesign reports "replacing existing signature" for every file it
# touches, on stderr, and stderr is the one channel build.sh does not swallow. Captured
# rather than discarded, so a real failure still says why.
machos="$(find "$bundle/Contents" -type f -exec sh -c 'file -b "$1" | grep -q "Mach-O" && echo "$1"' _ {} \;)"
count="$(printf '%s' "$machos" | grep -c . || true)"

if [[ "$identity" == "-" ]]; then
  output="$(codesign --force --deep --sign - "$bundle" 2>&1)" \
    || { echo "$output" >&2; echo "ERROR: ad hoc signing failed." >&2; exit 1; }
else
  echo "  signing the bundle and $count Mach-O binaries · --deep asks Apple's timestamp" >&2
  echo "  service once per file in Contents/MacOS, so this takes minutes." >&2
  output="$(codesign --force --deep --timestamp --options runtime \
                     --entitlements "$entitlements" --sign "$identity" "$bundle" 2>&1)" \
    || { echo "$output" >&2; echo "ERROR: Developer ID signing failed." >&2; exit 1; }
fi

# ── Verify before claiming success ───────────────────────────────────────────────────
#
# --deep on verification walks into the nested binaries and catches the one that was
# missed. Only the failure is printed; a valid bundle has nothing to say.
if ! verification="$(codesign --verify --deep --strict --verbose=2 "$bundle" 2>&1)"; then
  echo "$verification" | sed 's/^/  /' >&2
  echo "ERROR: the bundle does not verify after signing." >&2
  exit 1
fi

if [[ "$identity" == "-" ]]; then
  echo "  signed ad hoc · the bundle and $count Mach-O binaries inside it" >&2
  exit 0
fi

# Every Mach-O must carry the hardened runtime, and this is the check that earns the
# minutes spent above. The notary service refuses a bundle with one binary without it,
# twenty minutes later and by email; `--deep` propagating the option to all 19 is a
# promise worth verifying rather than trusting.
missing=0
while IFS= read -r file; do
  [[ -n "$file" ]] || continue
  if ! codesign -d --verbose=2 "$file" 2>&1 | grep -q "flags=.*runtime"; then
    echo "  no hardened runtime: ${file#"$bundle"/}" >&2
    missing=$((missing + 1))
  fi
done <<< "$machos"

if (( missing > 0 )); then
  echo "ERROR: $missing of $count binaries lack the hardened runtime; notarization" >&2
  echo "       would refuse this bundle." >&2
  exit 1
fi

team="$(codesign -dvv "$bundle" 2>&1 | sed -n 's/^TeamIdentifier=//p')"
echo "  signed Developer ID · team=$team · hardened runtime on all $count binaries" >&2
echo "  entitlements from $(basename "$entitlements")" >&2
