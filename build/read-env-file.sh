#!/usr/bin/env bash
#
# Sourceable helper: reads a KEY=VALUE file into the environment.
#
#   source "<repo>/build/read-env-file.sh"
#   read_env_file "<repo>/packaging.env"
#
# Two scripts need `packaging.env` — write-packaged-env.sh for the credential it bakes
# into the package, and macos/build/sign-app.sh for the signing identity — and a second
# copy of this parser would drift from the first. It lives here once.
#
# Parsed, not sourced, and the reason is a failure that shipped. A token can legitimately
# contain a space — stage wants the value to be "Bearer eyJ...", because the agent sends
# the header verbatim — and `source` reads that as the command `eyJ...` with a one-word
# variable in front. The token silently became "Bearer", the build reported success, and
# the package was broken. Parsing also means the file cannot run code.
#
# A variable already set in the real environment always wins, so CI can inject a secret
# without writing it to disk.

read_env_file() {
  local file="${1:?usage: read_env_file <path>}"
  local line key value

  [[ -f "$file" ]] || return 0

  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ "$line" =~ ^[[:space:]]*(#|$) ]] && continue
    [[ "$line" =~ ^[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*=(.*)$ ]] || continue

    key="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"

    # Quotes are optional and stripped if present; everything else is taken literally,
    # including spaces, so the value never needs escaping.
    [[ "$value" == \"*\" || "$value" == \'*\' ]] && value="${value:1:${#value}-2}"

    # Already in the environment wins.
    [[ -n "${!key:-}" ]] || printf -v "$key" '%s' "$value"
    export "${key?}"
  done < "$file"
}
