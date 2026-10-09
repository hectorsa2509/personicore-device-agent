"""A stand-in for the Personicore GraphQL endpoint, so the states that depend on what
the panel answers can be exercised without touching production.

    SCENARIO=no-panelists python3 tools/fake-graphql.py

Then point the agent at it with a .env next to state.json:

    PERSONICORE_GRAPHQL_ENDPOINT=http://127.0.0.1:8099/
    PERSONICORE_GRAPHQL_TOKEN=test-token

Scenarios:
    healthy           a household with a router id and one panelist
    no-router         getPanelistsRouter knows no router for that MAC
    no-panelists      the router is known, the household has nobody
    empty-router-id   the router is known but has no id
    router-token      healthy, but presenceCreateDevice refuses the credential — what
                      the real resolver does when handed the router's JWT instead of a
                      panelist's. This is the expected production failure, not an edge case.
    wrong-household   healthy, but the token's household is not the router's
    moved-household   healthy, and presenceCreateDevice works — but the address report
                      refuses the identity, which is what the panel answers when the stored
                      deviceId belongs to a Device registered on another router. One computer
                      carried to a second metered home reaches this and nothing else does.

Every reply carries all the fields of the contract; the client only reads the one it
asked for, which keeps this independent of how the request body arrives.

presenceCreateDevice and presenceReportDeviceIp are answered for real, not stubbed flat.
Request ids and report ids are remembered, so resending one returns what it first answered,
and the IP report keeps one open period per (router, subject): the same address again is a
no-op, a different one closes the old period and opens a new one. That is what makes it worth
pointing the agent at — the idempotency is the part that can actually be got wrong.
"""
import json, os, uuid
from http.server import BaseHTTPRequestHandler, HTTPServer

SCENARIO = os.environ.get("SCENARIO", "no-router")

HOUSEHOLD = "HH-STUB-1"
ROUTER = "stub-router-1"

BODIES = {
    "no-router":       {"getPanelistsRouter": {"router": None, "panelists": []}},
    "no-panelists":    {"getPanelistsRouter": {"router": {"id": "6ab16b68bd8f0049c29b42f9",
                                                          "macAddress": "80:AF:CA:E2:97:10"},
                                               "panelists": []}},
    "empty-router-id": {"getPanelistsRouter": {"router": {"id": "", "macAddress": "80:AF:CA:E2:97:10"},
                                               "panelists": [{"id": "p1", "firstName": "Ana"}]}},
    "healthy":         {"getPanelistsRouter": {"router": {"id": ROUTER, "macAddress": "80:AF:CA:E2:97:10",
                                                          "household": {"id": HOUSEHOLD}},
                                               "panelists": [{"id": "p1", "firstName": "Ana"}]}},
}
BODIES["router-token"] = BODIES["healthy"]
BODIES["wrong-household"] = BODIES["healthy"]
BODIES["moved-household"] = BODIES["healthy"]

# The refusals presenceCreateDevice raises that the agent has to tell apart from bad data.
REFUSALS = {
    "router-token": "Access denied!",
    "wrong-household": "The JWT does not authorize this router!",
}

# A Device belongs to one router, and an address report cannot move it: this is the answer a
# computer gets in the second metered home it is carried to, with an id issued in the first.
# The agent maps it to DeviceOnAnotherNodeException — the long backoff, and its own sentence.
IP_REFUSALS = {
    "moved-household": "The Network Device is not registered on this router!",
}

# requestId -> the payload it was first answered with. The whole point of the Retry path.
ACCEPTED = {}

# reportId -> payload, and (routerId, subject) -> the open Local IP Period. Enough to show the
# two rules that matter: a new reportId with the same IP is a no-op, a new IP abuts, and a
# reused reportId returns the original period whatever the body now says.
RECEIPTS = {}
OPEN_PERIODS = {}


def create_device(variables):
    """The resolver's own create / retry / update decision, as documented."""
    data = variables.get("input") or {}
    request_id = data.get("requestId") or ""

    if request_id in ACCEPTED:
        print(f"[stub] retry of {request_id} -> {ACCEPTED[request_id]['deviceId']}", flush=True)
        return ACCEPTED[request_id]

    device_id = data.get("deviceId") or uuid.uuid4().hex[:24]
    payload = {
        "requestId": request_id,
        "deviceId": device_id,
        "routerId": data.get("routerId") or ROUTER,
        "householdId": HOUSEHOLD,
        "acceptedAt": "2026-10-01T00:00:00.000Z",
    }
    ACCEPTED[request_id] = payload

    what = "update" if data.get("deviceId") else "create"
    print(f"[stub] {what} {request_id} -> {device_id} "
          f"owners={data.get('panelistIds')} shared={data.get('deviceShared')}", flush=True)
    return payload


def report_device_ip(variables):
    """The receipt-then-occupancy order, as documented."""
    data = variables.get("input") or {}
    report_id = data.get("reportId") or ""
    subject = data.get("deviceId") or f'machineId:{data.get("machineId")}'
    router = data.get("routerId") or ROUTER
    ip = data.get("ip") or ""

    if report_id in RECEIPTS:
        kept = RECEIPTS[report_id]
        print(f"[stub] ip retry {report_id} -> keeps {kept['ip']} (body said {ip})", flush=True)
        return kept

    key = (router, subject)
    previous = OPEN_PERIODS.get(key)

    if previous == ip:
        print(f"[stub] ip no-op: {subject} still at {ip} on {router}", flush=True)
    else:
        if previous:
            print(f"[stub] ip change: closing {previous}, opening {ip} (abut)", flush=True)
        else:
            print(f"[stub] ip first period: {subject} -> {ip} on {router}", flush=True)
        OPEN_PERIODS[key] = ip

    payload = {"reportId": report_id, "deviceId": data.get("deviceId") or subject,
               "routerId": router, "ip": ip, "acceptedAt": "2026-10-01T00:00:00.000Z"}
    RECEIPTS[report_id] = payload
    return payload


class Handler(BaseHTTPRequestHandler):
    def read_body(self):
        """The .NET client sends chunked, with no Content-Length. Reading only the header
        got an empty body and every scenario still answered, which made the stub look
        healthy while it had seen nothing of the request."""
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            chunks = []
            while True:
                size = int(self.rfile.readline().split(b";")[0] or b"0", 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.readline()
            return b"".join(chunks)

        return self.rfile.read(int(self.headers.get("Content-Length", 0)))

    def do_POST(self):
        raw = self.read_body()
        print(f"[stub] {SCENARIO} <- {raw[:120]!r}", flush=True)

        try:
            request = json.loads(raw or b"{}")
        except ValueError:
            request = {}

        query = request.get("query") or ""
        variables = request.get("variables") or {}

        if "presenceCreateDevice" in query and SCENARIO in REFUSALS:
            return self.reply({"errors": [{"message": REFUSALS[SCENARIO]}]})

        if "presenceReportDeviceIp" in query and SCENARIO in IP_REFUSALS:
            return self.reply({"errors": [{"message": IP_REFUSALS[SCENARIO]}]})

        # Both fields in every reply: the client only reads the one it asked for, and
        # this avoids depending on how the request body reaches us.
        data = dict(
            BODIES[SCENARIO],
            routerIpsAssigment={"id": ROUTER, "macAddress": "80:AF:CA:E2:97:10"},
            presenceCreatePersonalDevice={"id": "DEV-STUB-0001", "name": "stub", "uuid": "stub"},
            presenceCreateDevice=create_device(variables) if "presenceCreateDevice" in query else None,
            presenceReportDeviceIp=report_device_ip(variables) if "presenceReportDeviceIp" in query else None,
        )
        self.reply({"data": data})

    def reply(self, body):
        payload = json.dumps(body).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *a):
        pass


HTTPServer(("127.0.0.1", 8099), Handler).serve_forever()
