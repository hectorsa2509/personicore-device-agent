#!/bin/sh
#
# Personicore proxy helper — the only part of the agent that runs as root.
#
# macOS keeps proxies per network service and system-wide, so changing them needs admin
# rights. Asking the panelist for a password every time would be unworkable: the agent
# also pauses the proxy on its own when the Mac leaves home. Instead this script is
# installed once (one password prompt) and launchd exposes it on a local socket.
#
# launchd runs it in inetd mode: one line of command on stdin, one line of result on
# stdout, one process per connection.
#
#   APPLY <ipv4> [host [mac]]
#                         point every network service at <ipv4>:3129 (web and secure web).
#                         The optional host is the name the agent found the node by; it is
#                         remembered so the watchdog can follow the node on its own. The
#                         optional mac is the node this computer is registered on: with it
#                         remembered, the watchdog follows that node and no other.
#   REPOINT <ipv4> [host [mac]]
#                         the node moved and the agent is following it. Unlike APPLY this
#                         is not the panelist asking for anything, so it touches only the
#                         proxies this helper still owns: whatever they have taken over in
#                         System Settings since stays theirs.
#   RESTORE               put back exactly what was there before the first APPLY
#   OWNED                 what this helper still owns, at which address, and whether the
#                         watchdog paused it. The agent asks before deciding that a proxy
#                         it does not recognise means the panelist changed it. The address
#                         is `mixed` when the owned settings do not agree.
#   STATUS                report the proxy of each service
#
# What it refuses, so that a local process cannot use it to hijack traffic:
#   * anything that is not one of those commands;
#   * an address that is not a private IPv4 (10/8, 172.16/12, 192.168/16);
#   * an address where no Personicore meter node answers (router_info.sh must return a
#     valid MAC), and —once a household MAC is remembered— a node that is not that one:
#     another panel household's meter is reachable on the same name and often the same
#     address, and proxying through it would file this computer's traffic under their home.
#     The port is fixed at 3129 and is never taken from the request.
#
set -u

PORT=3129
STATE_DIR="/Library/Application Support/Personicore"
PREVIOUS="$STATE_DIR/proxy-previous.txt"
# What this helper set, one line per network service: "<service>\t<ip>:<port>". Ownership
# is per service and per value, not one global flag: a Mac has several services, the
# panelist can take over one of them and leave the others alone, and a helper that decides
# with a single answer either writes over the service they changed or abandons the ones it
# is still responsible for.
APPLIED="$STATE_DIR/proxy-applied.txt"
# The node's name, as the agent resolved it. The watchdog needs it to follow the node to
# a new address without the agent; it is written by APPLY and never read from a request.
HOSTFILE="$STATE_DIR/router-host.txt"
# The MAC of the node this computer is registered on, as the agent knows it. The watchdog
# resolves a name every meter node answers to, so without this it follows whichever node is
# in front of the Mac — in another panel household, theirs. Absent before the first
# registration, and then any meter node is accepted: there is nothing yet to be foreign to.
MACFILE="$STATE_DIR/router-mac.txt"
# Written when the watchdog takes the proxy out because no node answered, and removed by
# the next APPLY or REPOINT. It is the difference between "the panelist turned our proxy
# off" and "we took it off ourselves so they could keep browsing": without it the agent
# reads its own safety net as the panelist's doing and switches itself off for good.
PAUSED="$STATE_DIR/proxy-paused.txt"
DEFAULT_HOST="router.example.invalid"

# "*.local" keeps Bonjour working; the private ranges keep the meter node, printers and
# the ISP router's page off the proxy.
BYPASS='*.local 169.254/16 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 127.0.0.1 localhost'

log() { /usr/bin/logger -t personicore-proxy-helper "$1"; }

services() {
    # Skip the "An asterisk denotes..." header and the * of disabled services.
    /usr/sbin/networksetup -listallnetworkservices 2>/dev/null | /usr/bin/tail -n +2 | /usr/bin/sed 's/^\*//'
}

# An address is private only if it is an address at all: four octets, each 0-255, in
# 10/8, 172.16/12 or 192.168/16. The three ranges are spelled out in full because they
# end with different numbers of free octets, and a shared tail is what let "10.0.1"
# through and kept "10.0.0.1" out.
octet='(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])'
PRIVATE_IPV4="^(10\.$octet\.$octet\.$octet|172\.(1[6-9]|2[0-9]|3[01])\.$octet\.$octet|192\.168\.$octet\.$octet)$"

is_private_ipv4() {
    echo "$1" | /usr/bin/grep -Eq "$PRIVATE_IPV4"
}

is_mac() {
    echo "$1" | /usr/bin/grep -Eq '^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$'
}

# The MAC this helper is allowed to proxy through, or empty when it has not been told one.
household_mac() {
    [ -f "$MACFILE" ] || return 0
    m=$(/usr/bin/head -1 "$MACFILE" | /usr/bin/tr -d '[:space:]')
    is_mac "$m" && echo "$m" | /usr/bin/tr 'A-Z' 'a-z'
}

# Is there a meter node at this address, and is it ours? The second half is the whole
# difference between "a node answers" and "our node answers": every meter node serves the
# same name, so a Mac carried into another panel household finds one at the very same
# address, and following it would meter this computer through that home.
is_meter_node() {
    mac=$(/usr/bin/curl -s -m 4 --noproxy '*' "http://$1/cgi-bin/router_info.sh" \
          | /usr/bin/sed -n 's/.*"wan_mac"[ ]*:[ ]*"\([^"]*\)".*/\1/p')
    is_mac "$mac" || return 1

    want=$(household_mac)
    [ -z "$want" ] && return 0

    [ "$(echo "$mac" | /usr/bin/tr 'A-Z' 'a-z')" = "$want" ]
}

is_hostname() {
    echo "$1" | /usr/bin/grep -Eq '^[A-Za-z0-9]([A-Za-z0-9._-]{0,251}[A-Za-z0-9])?$'
}

node_host() {
    if [ -f "$HOSTFILE" ]; then
        h=$(/usr/bin/head -1 "$HOSTFILE" | /usr/bin/tr -d '[:space:]')
        is_hostname "$h" && { echo "$h"; return 0; }
    fi
    echo "$DEFAULT_HOST"
}

# dig first because it asks the resolver, not the cache: after the node reboots with a new
# lease the cached answer is exactly the one that is already known to be wrong.
resolve_host() {
    /usr/bin/dig +short +time=2 +tries=1 "$1" A 2>/dev/null | /usr/bin/grep -E '^[0-9.]+$'
    /usr/bin/dscacheutil -q host -a name "$1" 2>/dev/null | /usr/bin/sed -n 's/^ip_address: //p'
}

save_previous() {
    [ -f "$PREVIOUS" ] && return 0          # keep the settings from before the first APPLY

    /bin/mkdir -p "$STATE_DIR"
    : > "$PREVIOUS.tmp"
    services | while IFS= read -r service; do
        [ -z "$service" ] && continue
        web=$(/usr/sbin/networksetup -getwebproxy "$service" 2>/dev/null | /usr/bin/tr '\n' '|')
        secure=$(/usr/sbin/networksetup -getsecurewebproxy "$service" 2>/dev/null | /usr/bin/tr '\n' '|')
        bypass=$(/usr/sbin/networksetup -getproxybypassdomains "$service" 2>/dev/null | /usr/bin/tr '\n' ' ')
        echo "$service	$web	$secure	$bypass" >> "$PREVIOUS.tmp"
    done
    /bin/mv "$PREVIOUS.tmp" "$PREVIOUS"
    /bin/chmod 600 "$PREVIOUS"
}

# Put one kind back, and check the OS took it. networksetup answers 0 for settings it
# never wrote —a service that is off, a name it does not recognise, a managed
# configuration— so the value is read back before it counts as restored.
#
# Returns 0 when the setting is what it should be, including the case of a kind marked as
# released: that one belongs to the panelist now, and putting our saved value back over it
# is exactly the overwrite this ownership bookkeeping exists to prevent.
restore_kind() {
    service="$1"; kind="$2"; saved="$3"

    [ "$saved" = "$RELEASED_MARK" ] && return 0

    if [ "$(field "$saved" Enabled)" = "Yes" ]; then
        server=$(field "$saved" Server)
        port=$(field "$saved" Port)
        case "$kind" in
            web)    /usr/sbin/networksetup -setwebproxy "$service" "$server" "$port" >/dev/null 2>&1 ;;
            secure) /usr/sbin/networksetup -setsecurewebproxy "$service" "$server" "$port" >/dev/null 2>&1 ;;
            *) return 1 ;;
        esac
        entry_matches "$service" "$kind" "$server:$port"
        return $?
    fi

    case "$kind" in
        web)    /usr/sbin/networksetup -setwebproxystate "$service" off >/dev/null 2>&1 ;;
        secure) /usr/sbin/networksetup -setsecurewebproxystate "$service" off >/dev/null 2>&1 ;;
        *) return 1 ;;
    esac
    [ "$(field "$(read_proxy "$service" "$kind")" Enabled)" = "No" ]
}

# Put one service's proxies back. $5 collects "<service>\t<kind>" for the ones that really
# went back — only those may give up their claim. Returns 1 if anything failed.
restore_service() {
    service="$1"; web="$2"; secure="$3"; bypass="$4"; restored="$5"
    failed=0

    if restore_kind "$service" "web" "$web"; then
        printf '%s\t%s\n' "$service" "web" >> "$restored"
    else
        log "$service (web): the previous proxy could not be put back"
        failed=1
    fi

    if restore_kind "$service" "secure" "$secure"; then
        printf '%s\t%s\n' "$service" "secure" >> "$restored"
    else
        log "$service (secure): the previous proxy could not be put back"
        failed=1
    fi

    # The bypass list is per service, not per kind: it goes back with whatever we restore.
    # A failure here is logged by macOS and does not hold up the release: the bypass list
    # cannot leave anybody without internet, and the proxies themselves are verified.
    case "$bypass" in
        ""|*"There aren't any bypass domains set"*)
            /usr/sbin/networksetup -setproxybypassdomains "$service" "Empty" >/dev/null 2>&1 ;;
        *)
            # shellcheck disable=SC2086
            /usr/sbin/networksetup -setproxybypassdomains "$service" $bypass >/dev/null 2>&1 ;;
    esac

    return $failed
}

# ── Ownership ───────────────────────────────────────────────────────────────
# Recorded per network service *and* per proxy kind: "<service>\t<kind>\t<ip>:<port>",
# kind being web or secure. A Mac has several services and two proxies on each, and the
# panelist can take over any one of them. Anything coarser either writes over the setting
# they changed or walks away from the ones this helper is still responsible for — and a
# setting abandoned while it still points at an absent node is a Mac that cannot browse.
#
# Service names contain spaces ("Thunderbolt Bridge"), so selections are always passed as
# files, one record per line, and read with a tab IFS. Never as shell arguments.

RELEASED_MARK="__released__"

# "Enabled: Yes|Server: 1.2.3.4|Port: 8080|" → the three values
field() { echo "$1" | /usr/bin/tr '|' '\n' | /usr/bin/sed -n "s/^$2: //p" | /usr/bin/head -1; }

read_proxy() {
    case "$2" in
        web)    /usr/sbin/networksetup -getwebproxy "$1" 2>/dev/null | /usr/bin/tr '\n' '|' ;;
        secure) /usr/sbin/networksetup -getsecurewebproxy "$1" 2>/dev/null | /usr/bin/tr '\n' '|' ;;
    esac
}

# Ours only if this exact proxy is on, at that address, on that port.
entry_matches() {
    value=$(read_proxy "$1" "$2")
    want_ip="${3%:*}"
    want_port="${3##*:}"

    [ "$(field "$value" Enabled)" = "Yes" ] || return 1
    [ "$(field "$value" Server)" = "$want_ip" ] || return 1
    [ "$(field "$value" Port)" = "$want_port" ] || return 1
    return 0
}

# Stop claiming one proxy of one service, without touching it: its ownership record goes,
# and its saved previous value is marked so a later restore cannot write over it. When a
# service has no owned proxy left, its backup line goes entirely.
release_entry() {
    service="$1"; kind="$2"

    # Only the two kinds we set can release a saved value. Anything else came from a
    # record we could not parse, and guessing would throw away recovery data for a proxy
    # that may still be ours.
    case "$kind" in
        web|secure) ;;
        *)
            if [ -f "$APPLIED" ]; then
                /usr/bin/awk -F'\t' -v s="$service" -v k="$kind" '!($1 == s && $2 == k)' "$APPLIED" > "$APPLIED.tmp" \
                    && /bin/mv "$APPLIED.tmp" "$APPLIED"
                [ -s "$APPLIED" ] || /bin/rm -f "$APPLIED"
            fi
            return 0 ;;
    esac

    if [ -f "$APPLIED" ]; then
        /usr/bin/awk -F'\t' -v s="$service" -v k="$kind" '!($1 == s && $2 == k)' "$APPLIED" > "$APPLIED.tmp" \
            && /bin/mv "$APPLIED.tmp" "$APPLIED"
    fi

    if [ -f "$PREVIOUS" ]; then
        /usr/bin/awk -F'\t' -v OFS='\t' -v s="$service" -v k="$kind" -v mark="$RELEASED_MARK" '
            $1 == s { if (k == "web") $2 = mark; else $3 = mark }
            { print }
        ' "$PREVIOUS" > "$PREVIOUS.tmp" && /bin/mv "$PREVIOUS.tmp" "$PREVIOUS"

        # A service with both kinds released has nothing left for us to put back.
        /usr/bin/awk -F'\t' -v mark="$RELEASED_MARK" '!($2 == mark && $3 == mark)' "$PREVIOUS" > "$PREVIOUS.tmp" \
            && /bin/mv "$PREVIOUS.tmp" "$PREVIOUS"
    fi

    [ -s "$PREVIOUS" ] || /bin/rm -f "$PREVIOUS"
    [ -s "$APPLIED" ] || /bin/rm -f "$APPLIED"
}

# Ownership files from older helpers, rewritten into the current three-column format
# before anything reads them.
#
# The format is decided by the number of columns, not by whether a tab is present: version
# 4 wrote two columns and version 5 writes three, so "has a tab" reads a version 4 row as a
# service with the address in place of the kind — and then releases that unknown kind,
# which marked the saved HTTPS as given up while HTTPS was still pointing at the node.
#
#   1 column  → version 3: one "<ip>:<port>" for the whole Mac
#   2 columns → version 4: "<service>\t<ip>:<port>", both kinds implied
#   3 columns → current
#
# Anything else is not guessed at. The claim is dropped and the backup kept, so adoption
# can pick up whatever is still on our port: losing recovery data is the one outcome worth
# avoiding, and a wrong guess is how that happens.
migrate_ownership() {
    [ -f "$APPLIED" ] || return 0

    columns=$(/usr/bin/awk -F'\t' 'NF > 0 { print NF; exit }' "$APPLIED")

    if [ -z "$columns" ]; then
        /bin/rm -f "$APPLIED"
        return 0
    fi

    [ "$columns" = "3" ] && return 0

    : > "$APPLIED.tmp"

    case "$columns" in
        1)
            legacy=$(/usr/bin/head -1 "$APPLIED" | /usr/bin/tr -d '[:space:]')
            case "$legacy" in
                *:*)
                    services | while IFS= read -r service; do
                        [ -z "$service" ] && continue
                        printf '%s\t%s\t%s\n' "$service" "web" "$legacy" >> "$APPLIED.tmp"
                        printf '%s\t%s\t%s\n' "$service" "secure" "$legacy" >> "$APPLIED.tmp"
                    done
                    ;;
                *) : ;;
            esac
            ;;
        2)
            while IFS="$(printf '\t')" read -r service address; do
                [ -z "$service" ] && continue
                case "$address" in
                    *:*)
                        printf '%s\t%s\t%s\n' "$service" "web" "$address" >> "$APPLIED.tmp"
                        printf '%s\t%s\t%s\n' "$service" "secure" "$address" >> "$APPLIED.tmp"
                        ;;
                    *) : ;;
                esac
            done < "$APPLIED"
            ;;
        *) : ;;
    esac

    if [ -s "$APPLIED.tmp" ]; then
        /bin/mv "$APPLIED.tmp" "$APPLIED"
        /bin/chmod 600 "$APPLIED"
        log "migrated a $columns-column ownership file to per-service, per-kind records"
    else
        # Nothing usable came out of it: stop claiming, keep the backup for adoption.
        /bin/rm -f "$APPLIED.tmp" "$APPLIED"
        log "ownership file was not in a format this helper understands; dropping the claim"
    fi
}

# Installs from before ownership was recorded have a backup and no ownership file. Rather
# than abandon them behind a proxy that may be dead, claim exactly the proxies on our
# port right now: that is the one value only this helper sets.
adopt_legacy_ownership() {
    [ -f "$PREVIOUS" ] || return 0
    [ -f "$APPLIED" ] && return 0

    : > "$APPLIED.tmp"
    services | while IFS= read -r service; do
        [ -z "$service" ] && continue
        for kind in web secure; do
            value=$(read_proxy "$service" "$kind")
            [ "$(field "$value" Enabled)" = "Yes" ] || continue
            [ "$(field "$value" Port)" = "$PORT" ] || continue
            printf '%s\t%s\t%s:%s\n' "$service" "$kind" "$(field "$value" Server)" "$PORT" >> "$APPLIED.tmp"
        done
    done

    if [ -s "$APPLIED.tmp" ]; then
        /bin/mv "$APPLIED.tmp" "$APPLIED"
        /bin/chmod 600 "$APPLIED"
        log "adopted ownership of the proxies still on port $PORT"
    else
        /bin/rm -f "$APPLIED.tmp"
    fi
}

# Give up every proxy the panelist has taken over, and write the ones still ours to $1.
prune_ownership() {
    : > "$1"
    [ -f "$APPLIED" ] || return 0

    snapshot=$(/usr/bin/mktemp)
    /bin/cat "$APPLIED" > "$snapshot"

    while IFS="$(printf '\t')" read -r service kind address; do
        [ -z "$service" ] && continue
        if entry_matches "$service" "$kind" "$address"; then
            printf '%s\t%s\t%s\n' "$service" "$kind" "$address" >> "$1"
        else
            log "$service ($kind) is no longer set by us; leaving it alone"
            release_entry "$service" "$kind"
        fi
    done < "$snapshot"

    /bin/rm -f "$snapshot"
}

# ── Applying and restoring ──────────────────────────────────────────────────
# Both the socket commands and the watchdog go through these, so what root does is
# written once.

# Take the panelist's current value for one proxy before we write ours over it — but only
# when we are not already holding one.
#
# save_previous refuses to refresh a backup that exists, which is right for the proxies we
# have held all along and wrong for two cases: a kind released earlier because the panelist
# took it over, and a network service added to the Mac after the backup was taken. Both are
# about to become ours again, and the saved value must stop saying otherwise. Without this
# the released mark survived the new APPLY, the restore that followed "succeeded" by
# writing nothing, and the Mac was left pointing at the node with its claim and its backup
# both deleted — which is also the state in which the watchdog gives up.
capture_previous() {
    service="$1"; kind="$2"

    [ -f "$PREVIOUS" ] || return 0

    held=$(/usr/bin/awk -F'\t' -v s="$service" -v k="$kind" '
        $1 == s { print (k == "web" ? $2 : $3); found = 1; exit }
        END { if (!found) print "__missing__" }
    ' "$PREVIOUS")

    # A real saved value means this kind is already ours; keeping it is the whole point.
    [ "$held" = "$RELEASED_MARK" ] || [ "$held" = "__missing__" ] || return 0

    if [ "$held" = "__missing__" ]; then
        # Both kinds at once, while neither has been written yet.
        web=$(read_proxy "$service" "web")
        secure=$(read_proxy "$service" "secure")
        bypass=$(/usr/sbin/networksetup -getproxybypassdomains "$service" 2>/dev/null | /usr/bin/tr '\n' ' ')
        printf '%s\t%s\t%s\t%s\n' "$service" "$web" "$secure" "$bypass" >> "$PREVIOUS"
        return 0
    fi

    value=$(read_proxy "$service" "$kind")
    [ -n "$value" ] || value="Enabled: No|"

    /usr/bin/awk -F'\t' -v OFS='\t' -v s="$service" -v k="$kind" -v v="$value" '
        $1 == s { if (k == "web") $2 = v; else $3 = v }
        { print }
    ' "$PREVIOUS" > "$PREVIOUS.tmp" && /bin/mv "$PREVIOUS.tmp" "$PREVIOUS"
}

# Set one proxy and check it took. networksetup exits 0 for settings it never wrote, so
# the value is read back: ownership has to mean "this helper set it", and a claim on a
# setting that was refused is a claim nobody can act on.
set_proxy() {
    service="$1"; kind="$2"; ip="$3"

    capture_previous "$service" "$kind"

    case "$kind" in
        web)    /usr/sbin/networksetup -setwebproxy "$service" "$ip" "$PORT" >/dev/null 2>&1 ;;
        secure) /usr/sbin/networksetup -setsecurewebproxy "$service" "$ip" "$PORT" >/dev/null 2>&1 ;;
        *) return 1 ;;
    esac

    entry_matches "$service" "$kind" "$ip:$PORT"
}
# Apply to every service, both kinds. This is the panelist turning the switch on: they
# are asking for the whole Mac, so previous ownership does not restrict it.
apply_proxy() {
    ip="$1"
    had_previous=0
    [ -f "$PREVIOUS" ] && had_previous=1
    save_previous

    : > "$APPLIED.tmp"
    services | while IFS= read -r service; do
        [ -z "$service" ] && continue
        touched=""
        for kind in web secure; do
            if set_proxy "$service" "$kind" "$ip"; then
                printf '%s\t%s\t%s:%s\n' "$service" "$kind" "$ip" "$PORT" >> "$APPLIED.tmp"
                touched="yes"
            else
                log "$service ($kind): the proxy could not be set"
            fi
        done
        if [ -n "$touched" ]; then
            # shellcheck disable=SC2086
            /usr/sbin/networksetup -setproxybypassdomains "$service" $BYPASS >/dev/null 2>&1
        fi
    done

    # Claiming a setting that was never written is how a Mac ends up behind a proxy that
    # nobody will restore, so nothing at all applied is a failure, not an empty success.
    if [ ! -s "$APPLIED.tmp" ]; then
        /bin/rm -f "$APPLIED.tmp"
        [ "$had_previous" = "0" ] && /bin/rm -f "$PREVIOUS"
        log "no proxy could be set on any network service"
        return 1
    fi

    /bin/mv "$APPLIED.tmp" "$APPLIED"
    /bin/chmod 600 "$APPLIED"
    /bin/rm -f "$PAUSED"
    log "proxy set to $ip:$PORT"
    return 0
}

# Move only the proxies listed in $2 to a new address. Used when the node changed address
# and some proxies on this Mac are no longer ours to touch.
apply_entries() {
    ip="$1"; selection="$2"
    moved=0

    : > "$APPLIED.tmp"
    while IFS="$(printf '\t')" read -r service kind address; do
        [ -z "$service" ] && continue
        if set_proxy "$service" "$kind" "$ip"; then
            printf '%s\t%s\t%s:%s\n' "$service" "$kind" "$ip" "$PORT" >> "$APPLIED.tmp"
            moved=$((moved + 1))
        else
            # It could not be moved, so it is still pointing where we put it: keeping the
            # old claim is what lets a later restore find it. Dropping it would abandon a
            # proxy aiming at an address that may already be gone.
            log "$service ($kind): could not be re-pointed to $ip; keeping the claim on $address"
            printf '%s\t%s\t%s\n' "$service" "$kind" "$address" >> "$APPLIED.tmp"
        fi
    done < "$selection"

    if [ -s "$APPLIED.tmp" ]; then
        /bin/mv "$APPLIED.tmp" "$APPLIED"
        /bin/chmod 600 "$APPLIED"
    else
        /bin/rm -f "$APPLIED.tmp"
    fi

    if [ "$moved" = "0" ]; then
        log "no proxy could be re-pointed to $ip:$PORT"
        return 1
    fi

    /bin/rm -f "$PAUSED"
    log "proxy re-pointed to $ip:$PORT on $moved setting(s)"
    return 0
}

# Put back what we saved, for the proxies listed in $1 — or for everything still
# recorded when no selection is given. A proxy is released only once the OS has really
# taken its previous value back: it is the panelist's again, and ours to leave alone from
# now on. Returns 1 when something could not be put back.
#
# Releasing on the way out regardless of the result was worse than doing nothing. The
# release deletes the ownership record *and* the saved value, so a refused write left the
# Mac behind a proxy aiming at the node with nothing left to undo it — and the watchdog,
# which gives up as soon as the backup is gone, could no longer rescue it either.
restore_proxy() {
    [ -f "$PREVIOUS" ] || return 0

    selection="${1:-}"
    owned=""
    if [ -z "$selection" ]; then
        owned=$(/usr/bin/mktemp)
        if [ -f "$APPLIED" ]; then /bin/cat "$APPLIED" > "$owned"; fi
        selection="$owned"
    fi

    # One line per service to touch, without repeats, and without splitting on spaces.
    wanted=$(/usr/bin/mktemp)
    /usr/bin/awk -F'\t' '!seen[$1]++ {print $1}' "$selection" > "$wanted"

    restored=$(/usr/bin/mktemp)
    failed=0

    while IFS="$(printf '\t')" read -r service web secure bypass; do
        [ -z "$service" ] && continue
        /usr/bin/grep -Fxq "$service" "$wanted" || continue
        restore_service "$service" "$web" "$secure" "$bypass" "$restored" || failed=1
    done < "$PREVIOUS"

    # A service claimed after the backup was taken —one added to the Mac later— has no
    # saved value of its own. The only honest undo is to switch off what we set, which is
    # what a service we had to enable a proxy on had anyway.
    while IFS="$(printf '\t')" read -r service kind address; do
        [ -z "$service" ] && continue
        /usr/bin/awk -F'\t' -v s="$service" '$1 == s { found = 1 } END { exit !found }' "$PREVIOUS" && continue
        if restore_kind "$service" "$kind" "Enabled: No|"; then
            printf '%s\t%s\n' "$service" "$kind" >> "$restored"
        else
            log "$service ($kind): could not be switched off"
            failed=1
        fi
    done < "$selection"

    while IFS="$(printf '\t')" read -r service kind address; do
        [ -z "$service" ] && continue
        /usr/bin/grep -Fxq "$(printf '%s\t%s' "$service" "$kind")" "$restored" || continue
        release_entry "$service" "$kind"
    done < "$selection"

    /bin/rm -f "$wanted" "$restored"
    [ -n "$owned" ] && /bin/rm -f "$owned"

    if [ "$failed" = "1" ]; then
        log "some proxy settings could not be put back; their claim and their backup are kept"
        return 1
    fi

    log "previous proxy settings restored"
    return 0
}


# Where the node answers now, if it is not where the proxy points any more. Only private
# addresses where a meter node really answers are accepted, the same rule as APPLY.
follow_node() {
    for candidate in $(resolve_host "$(node_host)"); do
        if is_private_ipv4 "$candidate" && is_meter_node "$candidate"; then
            echo "$candidate"
            return 0
        fi
    done
    return 1
}

# ── Watchdog ────────────────────────────────────────────────────────────────
# Run every minute by its own LaunchDaemon, not through the socket. It is the safety net
# for when the agent is not running —quit, killed, crashed, never opened— and something
# moves underneath it. Two things can happen to a proxy nobody is watching:
#
#   * the node is still at home but got a new address (it rebooted, or its lease was
#     renewed) → follow it, so the panelist never notices;
#   * there is no node at all (the Mac left home, the node is off) → take the proxy out,
#     or the panelist is left with no internet and no idea why.
#
# The agent does the same, faster, while it runs; this is what covers the rest.
certificate_trusted() (
    # The installer substitutes the pinned public CA. No caller-selected path or root
    # override is accepted; security evaluates actual system SSL trust and validity.
    umask 077
    cert=$(/usr/bin/mktemp /private/tmp/personicore-proxy-ca.XXXXXXXX) || exit 1
    trap '/bin/rm -f "$cert"' EXIT
    /usr/bin/printf '%s' '__PERSONICORE_CA_BASE64__' | /usr/bin/base64 -D > "$cert" || exit 1
    /usr/bin/security verify-cert -c "$cert" -p ssl -l -L -q >/dev/null 2>&1
)

if [ "${1:-}" = "watchdog" ]; then
    [ -f "$PREVIOUS" ] || exit 0          # we changed nothing: nothing to undo

    migrate_ownership
    adopt_legacy_ownership

    owned=$(/usr/bin/mktemp)
    prune_ownership "$owned"

    if [ ! -s "$owned" ]; then
        /bin/rm -f "$owned"
        exit 0
    fi

    if ! certificate_trusted; then
        log "watchdog: HTTPS CA is not trusted; restoring owned proxy settings"
        /bin/rm -f "$PAUSED"
        restore_proxy "$owned"
        /bin/rm -f "$owned"
        exit 0
    fi

    # The records do not all carry the same address. A re-point macOS refused for one
    # setting leaves that one behind at the old one, and probing only the first record —
    # which is what this did — answers for the setting that is fine while the other keeps
    # pointing at an address the node has left. Every distinct address is probed.
    count=$(/usr/bin/wc -l < "$owned" | /usr/bin/tr -d ' ')
    addresses=$(/usr/bin/awk -F'\t' '{split($3, a, ":"); print a[1]}' "$owned" | /usr/bin/sort -u)

    live=""
    for candidate in $addresses; do
        if is_meter_node "$candidate"; then live="$candidate"; break; fi
    done

    # None of them answers: the node may still be at home with a new address.
    if [ -z "$live" ]; then
        live=$(follow_node) || live=""
    fi

    if [ -n "$live" ]; then
        stale=$(/usr/bin/awk -F'\t' -v want="$live:$PORT" '$3 != want' "$owned" | /usr/bin/wc -l | /usr/bin/tr -d ' ')

        if [ "$stale" = "0" ]; then
            /bin/rm -f "$owned"
            exit 0
        fi

        # The whole list goes in, not just the stale part: apply_entries rewrites the
        # ownership file from what it is given, and a selection would drop the claims on
        # the settings that were already right. Re-writing a proxy to the value it
        # already has costs one networksetup call and keeps the bookkeeping whole.
        log "watchdog: $stale of $count proxy setting(s) are not at $live; bringing them forward"
        apply_entries "$live" "$owned"
        /bin/rm -f "$owned"
        exit 0
    fi

    current=$(echo "$addresses" | /usr/bin/head -1)
    log "watchdog: no meter node of this household answers at $current; restoring $count proxy setting(s) so the Mac keeps browsing"

    # Left for the agent: this is the safety net taking the proxy out, not the panelist
    # turning it off. Without the difference the agent switches itself off and the proxy
    # never comes back when the Mac gets home.
    /bin/mkdir -p "$STATE_DIR"
    echo "$current" > "$PAUSED"
    /bin/chmod 644 "$PAUSED"

    restore_proxy "$owned"
    /bin/rm -f "$owned"
    exit 0
fi

command=$(/usr/bin/head -1)
verb=$(echo "$command" | /usr/bin/awk '{print $1}')
argument=$(echo "$command" | /usr/bin/awk '{print $2}')
host=$(echo "$command" | /usr/bin/awk '{print $3}')
household=$(echo "$command" | /usr/bin/awk '{print $4}')

case "$verb" in
    APPLY|REPOINT)
        if ! certificate_trusted; then
            log "refused $verb: HTTPS CA is not trusted"
            echo "ERROR HTTPS certificate is not trusted; use Install HTTPS certificate in the Personicore menu"
            exit 0
        fi
        if ! is_private_ipv4 "$argument"; then
            log "refused $verb: $argument is not a private IPv4"
            echo "ERROR not a private IPv4 address"
            exit 0
        fi
        # Remembered for the watchdog, and only if it looks like a hostname: it ends up
        # in a DNS query that root makes, never in a command. "-" is the agent holding the
        # place of a name it does not have so that a MAC can follow it.
        if [ -n "$host" ] && [ "$host" != "-" ] && is_hostname "$host"; then
            /bin/mkdir -p "$STATE_DIR"
            echo "$host" > "$HOSTFILE"
            /bin/chmod 644 "$HOSTFILE"
        fi

        # Which node the watchdog may follow, and which the probe below is held to. It is
        # recorded *before* that probe, and the order is the whole of a house move: the
        # panelist's new node is not the one in this file, so probing first would refuse the
        # APPLY that is bringing the new MAC, and the proxy could never come on again in the
        # new home. A request can only ever narrow what this helper will do — the probe still
        # has to find a real meter node at the address, and at the MAC the request named.
        #
        # An agent with no registration sends no MAC, and a remembered one is then dropped:
        # the computer has been registered somewhere else, or had its owner forgotten, and
        # until it is registered again any meter node is as good as the last one.
        if [ -n "$household" ]; then
            if is_mac "$household"; then
                /bin/mkdir -p "$STATE_DIR"
                echo "$household" | /usr/bin/tr 'A-Z' 'a-z' > "$MACFILE"
                /bin/chmod 644 "$MACFILE"
            else
                log "refused $verb: $household is not a MAC address"
                echo "ERROR not a MAC address"
                exit 0
            fi
        else
            /bin/rm -f "$MACFILE"
        fi

        if ! is_meter_node "$argument"; then
            log "refused $verb: no meter node of this household answers at $argument"
            echo "ERROR no Personicore meter node of this household answers at that address"
            exit 0
        fi

        # APPLY is the panelist turning the switch on: they are asking for the whole Mac,
        # and every service and both kinds are set. REPOINT is the agent following the
        # node to a new address, which nobody asked for: it moves only what this helper
        # still owns, so a proxy taken over in System Settings since is not written over.
        if [ "$verb" = "APPLY" ] || [ ! -f "$PREVIOUS" ]; then
            if apply_proxy "$argument"; then
                echo "OK $argument:$PORT"
            else
                echo "ERROR the proxy could not be set on any network service"
            fi
            exit 0
        fi

        migrate_ownership
        adopt_legacy_ownership
        pruned=$(/usr/bin/mktemp)
        prune_ownership "$pruned"

        if [ ! -s "$pruned" ]; then
            # Everything we had set has been taken over. There is nothing to move, and
            # nothing for this helper to claim either; the agent switches off on "none".
            /bin/rm -f "$pruned"
            log "REPOINT: nothing left that this helper owns"
            echo "OK none"
            exit 0
        fi

        if apply_entries "$argument" "$pruned"; then
            echo "OK $argument:$PORT"
        else
            echo "ERROR the proxy could not be re-pointed"
        fi
        /bin/rm -f "$pruned"
        ;;

    RESTORE)
        # Release anything the panelist has taken over first, so an explicit "off" puts
        # back only the services this helper is still responsible for.
        migrate_ownership
        adopt_legacy_ownership
        pruned=$(/usr/bin/mktemp)
        prune_ownership "$pruned"

        # An unverified "OK" here is what let a failed restore delete the only way back;
        # the agent needs to know so it can keep the switch on and try again.
        if restore_proxy "$pruned"; then
            /bin/rm -f "$PAUSED"
            echo "OK restored"
        else
            echo "ERROR some proxy settings could not be put back"
        fi
        /bin/rm -f "$pruned"
        ;;

    OWNED)
        # What root still owns, for an agent that cannot see these files. Ownership is
        # pruned first, so the answer is what is true now and not what was true when the
        # proxy was applied: the panelist may have taken one service over since, and the
        # watchdog may have restored or moved the rest while the agent was not running.
        migrate_ownership
        adopt_legacy_ownership
        pruned=$(/usr/bin/mktemp)
        prune_ownership "$pruned"

        count=$(/usr/bin/wc -l < "$pruned" | /usr/bin/tr -d ' ')

        # "mixed" when the records disagree, which happens when a re-point moved some
        # settings and not others. The agent cannot see the difference —it only reads the
        # web proxy of each service— and would report the proxy healthy while one of them
        # sat at an address the node has left.
        addresses=$(/usr/bin/awk -F'\t' '{print $3}' "$pruned" | /usr/bin/sort -u)
        distinct=$(echo "$addresses" | /usr/bin/grep -c .)

        if [ "$count" = "0" ]; then
            address="none"
        elif [ "$distinct" = "1" ]; then
            address=$(echo "$addresses" | /usr/bin/head -1)
        else
            address="mixed"
        fi
        state="active"
        [ -f "$PAUSED" ] && state="paused"

        /bin/rm -f "$pruned"
        echo "OK owned $count $address $state"
        ;;

    STATUS)
        services | while IFS= read -r service; do
            [ -z "$service" ] && continue
            web=$(/usr/sbin/networksetup -getwebproxy "$service" 2>/dev/null | /usr/bin/tr '\n' '|')
            echo "SERVICE $service enabled=$(field "$web" Enabled) server=$(field "$web" Server) port=$(field "$web" Port)"
        done
        echo "OK status"
        ;;

    *)
        log "refused unknown command"
        echo "ERROR unknown command"
        ;;
esac
