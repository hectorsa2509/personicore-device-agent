#!/usr/bin/env bash
#
# Writes the credential that travels inside the package, so the panelist never creates
# one and never has a file to keep next to the executable.
#
#   write-packaged-env.sh <directory> [filename]
#
# Windows calls it with the Core project's Config/ and `packaged.env`, just before
# publish: the file is compiled into the .exe as a resource and deleted again, so the
# package is one file. macOS calls it with the bundle's Contents/MacOS and the default
# `.env`, which must be written BEFORE codesign — the signature seals the bundle.
#
# The credential is NOT in this repository and must never be. It comes from, in order:
#
#   1. $PERSONICORE_PACKAGED_TOKEN / $PERSONICORE_PACKAGED_ENDPOINT in the environment
#   2. packaging.env in the repository root (gitignored) — the convenient one locally:
#          PERSONICORE_PACKAGED_ENDPOINT=https://graphql.example.invalid/graphql
#          PERSONICORE_PACKAGED_TOKEN=eyJ...
#
# With neither, NOTHING is written and the build says so. That is the old behaviour — the
# agent falls back to the jwt and endpoint the node serves from get_config.sh — and it is
# a legitimate package, so this does not fail the build. What it must never do is pass
# silently: a package nobody meant to ship without a token looks exactly like one that has
# it until it reaches a panelist.
#
set -euo pipefail

target="${1:?usage: write-packaged-env.sh <directory> [filename]}"
name="${2:-.env}"
file="$target/$name"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The environment wins over the file, so CI can inject a secret without writing it to
# disk. Parsed rather than sourced, for the reason read-env-file.sh explains.
source "$root/build/read-env-file.sh"
read_env_file "$root/packaging.env"

token="${PERSONICORE_PACKAGED_TOKEN:-}"
endpoint="${PERSONICORE_PACKAGED_ENDPOINT:-}"

if [[ -z "$token" && -z "$endpoint" ]]; then
  # Delete, not just skip. Both outputs are reused between builds — the Windows publish
  # directory and the Core source tree — so a build with no credential would otherwise
  # ship the one the PREVIOUS build left behind. The build output would say "NOT
  # packaged" while the package held a token nobody meant to send.
  if [[ -f "$file" ]]; then
    rm -f "$file"
    echo "  $name removed: a previous build left one and this build has no credential." >&2
  fi

  echo "  $name NOT packaged: no PERSONICORE_PACKAGED_TOKEN. The agent will use the" >&2
  echo "  credential and endpoint the meter node serves (get_config.sh)." >&2
  exit 0
fi

if [[ -z "$token" || -z "$endpoint" ]]; then
  # Half a configuration is the worst outcome: an endpoint with no credential reaches a
  # panelist and fails on every cycle, and a credential with no endpoint silently sends
  # the packaged token to whatever the node happens to advertise — in production, that is
  # the real backend.
  echo "ERROR: PERSONICORE_PACKAGED_TOKEN and PERSONICORE_PACKAGED_ENDPOINT go together;" >&2
  echo "       only one is set. Set both, or neither." >&2
  exit 1
fi

# "Bearer" on its own is what the previous `source`-based parser left behind when the
# value was unquoted. Cheap to check, and the failure it catches reaches a panelist.
if [[ "$token" =~ ^[Bb]earer[[:space:]]*$ ]]; then
  echo "ERROR: PERSONICORE_PACKAGED_TOKEN is the word \"$token\" and no credential." >&2
  exit 1
fi

cat > "$file" <<ENV
# ─────────────────────────────────────────────────────────────────────────────
#  Generated when this package was built. Do not edit by hand, and do not commit
#  it: it is gitignored on purpose.
#
#  Compiled into the Windows executable as a resource, and written inside the
#  signed bundle on macOS — where it has to exist BEFORE codesign runs, because
#  the signature seals the bundle and a file added afterwards invalidates it.
#
#  To point an installed agent somewhere else, set PERSONICORE_GRAPHQL_ENDPOINT
#  in the environment: a real environment variable wins over everything here.
#  A .env next to the executable also wins, which is the developer's override;
#  one in the user's Application Support / LOCALAPPDATA folder does not.
# ─────────────────────────────────────────────────────────────────────────────
PERSONICORE_GRAPHQL_ENDPOINT=$endpoint
PERSONICORE_GRAPHQL_TOKEN=$token
ENV

# To stderr, not stdout: build.sh sends the publish scripts' stdout to /dev/null, and
# which endpoint got baked into a package is the last thing that should be swallowed —
# shipping panelists a build aimed at stage is the easy mistake here.
#
# The token itself is never echoed. Its length is, which is enough to tell "the secret
# arrived" from "the variable was empty" without putting the secret in a build log.
echo "  $name packaged · endpoint=$endpoint · token=${#token} chars" >&2
