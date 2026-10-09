#!/usr/bin/env python3
"""Is presenceCreateDevice deployed yet, and does its shape still match what the agent sends?

    python3 tools/check-device-mutation.py                       # the endpoint the packages carry
    python3 tools/check-device-mutation.py <endpoint> <token>     # somewhere else

With no arguments it reads the .env the last build baked into windows/dist/win-x64, which is
the point: it checks the endpoint the packages will actually talk to, not one typed from
memory.

It only introspects — nothing is written, so it is safe to run against any environment. One
Python file rather than a shell script so it runs the same on Windows, where this needs
checking too.

Exit code: 0 deployed and the shape matches, 3 not deployed, 4 deployed but the shape
differs, 1 the endpoint complained.
"""
import json
import os
import random
import subprocess
import sys

# What the agent sends, from PersonicoreApi.BuildDeviceRequest. Kept here so this answers
# "does the deployed shape still match the code", not merely "does a type exist".
AGENT_SENDS = {
    "requestId", "routerId", "deviceId", "machineId", "name", "deviceType",
    "brand", "model", "deviceOs", "panelistIds", "principalPanelistId",
    "deviceShared", "isPersonal",
}

# What BuildDeviceIpRequest sends. Checked separately because the two mutations deploy
# separately, and the one that carries the address was written from a document rather than from
# a schema — so the day it lands, this is what says whether the document was right.
AGENT_SENDS_IP = {"reportId", "routerId", "deviceId", "machineId", "ip"}

QUERY = """query Probe%d {
  i: __type(name: "PresenceCreateDeviceInput") {
    inputFields { name type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } } }
  }
  p: __type(name: "PresenceDevicePayload") {
    fields { name type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } } }
  }
  ip_i: __type(name: "PresenceReportDeviceIpInput") {
    inputFields { name type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } } }
  }
  ip_p: __type(name: "PresenceDeviceIpPayload") {
    fields { name type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } } }
  }
  m: __type(name: "Mutation") { fields { name } }
}"""


def baked_env():
    """The endpoint and token the last build put next to the Windows .exe."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "..", "windows", "dist", "win-x64", ".env")
    if not os.path.isfile(path):
        return None, None

    values = {}
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("PERSONICORE_GRAPHQL_"):
                key, _, value = line.partition("=")
                values[key.strip()] = value.strip()

    print("Using the endpoint and token baked into windows/dist/win-x64/.env")
    return values.get("PERSONICORE_GRAPHQL_ENDPOINT"), values.get("PERSONICORE_GRAPHQL_TOKEN")


def render(node):
    """A type node as it is written in the schema: ID!, [ID!]!, String.

    A "?" means the introspection nesting in QUERY ran out before the element type was
    reached — not that the schema is odd. Deepen it if one ever appears.
    """
    if not node:
        return "?"
    kind, name, of = node.get("kind"), node.get("name"), node.get("ofType")
    if kind == "NON_NULL":
        return render(of) + "!"
    if kind == "LIST":
        return "[" + render(of) + "]"
    return name or render(of)


def main():
    if len(sys.argv) >= 3:
        endpoint, token = sys.argv[1], sys.argv[2]
    else:
        endpoint, token = baked_env()

    if not endpoint or not token:
        print("Usage: check-device-mutation.py <endpoint> <token>", file=sys.stderr)
        return 2

    print(f"Endpoint: {endpoint}\n")

    # A unique operation name and a cache-busting parameter: "it is deployed, you are
    # looking at a cached schema" is otherwise unanswerable.
    body = json.dumps({"query": QUERY % random.randrange(10**6)})
    url = f"{endpoint}{'&' if '?' in endpoint else '?'}cb={random.randrange(10**9)}"

    # curl rather than urllib, and not for convenience: curl trusts the operating system's
    # certificate store, which is what .NET does too, so this sees the endpoint exactly as
    # the agent will. Python ships its own CA bundle, and on this very endpoint it rejects a
    # perfectly valid Google Trust Services chain over a cross-signed root — a failure the
    # agent would never have, reported as if the endpoint were broken.
    command = [
        "curl", "-sS", "-m", "30", "-X", "POST", url,
        "-H", "Content-Type: application/json",
        "-H", f"Authorization: {token}",
        "-H", "Cache-Control: no-cache, no-store",
        "--data-binary", body,
    ]

    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=40)
    except FileNotFoundError:
        print("curl is not on PATH, and it is what this uses to reach the endpoint.")
        return 1
    except subprocess.TimeoutExpired:
        print("The endpoint did not answer within 40 s.")
        return 1

    if result.returncode != 0:
        print(f"Could not reach the endpoint: {result.stderr.strip() or f'curl exit {result.returncode}'}")
        return 1

    try:
        answer = json.loads(result.stdout)
    except ValueError:
        # An HTML error page, a login redirect, a captive portal. Showing it beats
        # "invalid JSON", which says nothing about what arrived.
        print("The endpoint did not answer with JSON. First 500 characters:\n")
        print(result.stdout[:500])
        return 1

    if answer.get("errors"):
        print("The endpoint answered with errors:")
        for error in answer["errors"]:
            print("  -", error.get("message"))
        return 1

    data = answer.get("data") or {}
    mutations = [f["name"] for f in (data.get("m") or {}).get("fields", [])]

    print(f"Mutations in the schema: {len(mutations)}")
    present = "presenceCreateDevice" in mutations
    print(f"presenceCreateDevice:    {'PRESENT' if present else 'ABSENT'}")
    print(f"presenceReportDeviceIp:  {'PRESENT' if 'presenceReportDeviceIp' in mutations else 'ABSENT'}")

    if not present:
        # Only the ones that START with presence: "presence" anywhere in the name matches
        # thirty-odd unrelated log mutations and buries the answer.
        near = sorted(n for n in mutations if n.lower().startswith("presence"))
        print(f"\nNot deployed to this endpoint. The only presence* mutations here are: "
              f"{', '.join(near) or '(none)'}")
        print("So either this is not the endpoint it went to, or the gateway has not "
              "recomposed its schema.")
        return 3

    fields = {f["name"]: render(f["type"])
              for f in (data.get("i") or {}).get("inputFields", [])}

    # Which revision of the contract is deployed. HP-1844 made routerId optional and, when it
    # is sent, stopped calling validatePanelistByUserId — which is the whole reason an agent
    # token can register at all. Two signatures distinguish it from the first cut, and both
    # are read off the schema rather than guessed from a document.
    router_optional = fields.get("routerId") == "ID"
    has_ip_mutation = "presenceReportDeviceIp" in mutations

    print()
    print("Contract revision:")
    print(f"  routerId                 {fields.get('routerId', '(absent)')}"
          f"{'   ← optional: household comes from the router, no panelist check' if router_optional else '   ← required: first cut of the contract'}")
    print(f"  presenceReportDeviceIp   {'present  ← the IP has a home of its own' if has_ip_mutation else 'absent   ← the IP still travels via routerIpsAssigment'}")

    if not router_optional:
        print("\n  This is the pre-HP-1844 schema. Expect `The panelist doesn't exist!` from")
        print("  presenceCreateDevice: that build calls validatePanelistByUserId even when")
        print("  routerId is sent, and no token the agent can hold is a panelist.")

    print("\nPresenceCreateDeviceInput        (+ = the agent does not send it)")
    for name, kind in sorted(fields.items()):
        print(f"  {' ' if name in AGENT_SENDS else '+'} {name:<22} {kind}")

    print("\nPresenceDevicePayload")
    for field in (data.get("p") or {}).get("fields", []):
        print(f"    {field['name']:<22} {render(field['type'])}")

    refused = sorted(n for n in AGENT_SENDS if n not in fields)
    required_unsent = sorted(n for n, k in fields.items()
                             if k.endswith("!") and n not in AGENT_SENDS)
    optional_unsent = sorted(n for n in fields if n not in AGENT_SENDS)

    print()
    if refused:
        print(f"MISMATCH — the agent sends fields the schema does not accept: {', '.join(refused)}")
    if required_unsent:
        print(f"MISMATCH — the schema requires fields the agent does not send: {', '.join(required_unsent)}")
    if refused or required_unsent:
        print("\nFix PersonicoreApi.BuildDeviceRequest before turning sendToBackend on.")
        return 4

    if optional_unsent:
        print(f"Optional fields the agent does not send, which is fine: {', '.join(optional_unsent)}")
    print("\npresenceCreateDevice: the shape matches what the agent builds.")

    # ── presenceReportDeviceIp, the address ──────────────────────────────────────────────
    print()
    if not has_ip_mutation:
        print("presenceReportDeviceIp is not deployed, so the address still goes through")
        print("routerIpsAssigment (registrationMode=deviceAndRouterIps). Nothing to check yet.")
        return 0

    ip_fields = {f["name"]: render(f["type"])
                 for f in (data.get("ip_i") or {}).get("inputFields", [])}

    print("PresenceReportDeviceIpInput      (+ = the agent does not send it)")
    for name, kind in sorted(ip_fields.items()):
        print(f"  {' ' if name in AGENT_SENDS_IP else '+'} {name:<22} {kind}")

    print("\nPresenceDeviceIpPayload")
    for field in (data.get("ip_p") or {}).get("fields", []):
        print(f"    {field['name']:<22} {render(field['type'])}")

    ip_refused = sorted(n for n in AGENT_SENDS_IP if n not in ip_fields)
    ip_required_unsent = sorted(n for n, k in ip_fields.items()
                                if k.endswith("!") and n not in AGENT_SENDS_IP)

    print()
    if ip_refused:
        print(f"MISMATCH — the agent sends fields the schema does not accept: {', '.join(ip_refused)}")
    if ip_required_unsent:
        print(f"MISMATCH — the schema requires fields the agent does not send: {', '.join(ip_required_unsent)}")
    if ip_refused or ip_required_unsent:
        print("\nFix PersonicoreApi.BuildDeviceIpRequest before switching to deviceAndIp.")
        return 4

    print("presenceReportDeviceIp: the shape matches too.")
    print("\nBoth are deployed and both match, which is what deviceAndIp — the default — needs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
