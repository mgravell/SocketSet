#!/usr/bin/env bash
# verify-bind-address.sh — does Listen(IPEndPoint) actually bind the address it was given?
#
# WHY THIS EXISTS. Until 2026-08-04 every native backend had a literal `sin_addr = 0` with a
# "TODO: use the actual IP" beside it, so Listen(new IPEndPoint(IPAddress.Loopback, p)) bound
# INADDR_ANY and the service was reachable on every interface. Only the managed backend honoured
# the address. Nothing in the repo could see it: the smoke matrix binds IPAddress.Any (which is
# 0.0.0.0 either way) and connects to 127.0.0.1, which an Any-bound listener answers happily. A
# bind that ignores its argument and one that honours it are byte-identical under every existing
# gate. That is bench/README.md rule 2 — "confirm the fast path was TAKEN, not just enabled" —
# in its security-relevant form.
#
# WHAT MAKES IT DISCRIMINATING. Two cells per backend, and BOTH must land:
#   loopback : asked for 127.0.0.1 -> the kernel must report 127.0.0.1
#   any      : asked for 0.0.0.0   -> the kernel must report 0.0.0.0
# The `any` cell is the control. Without it, a build that hard-coded the OPPOSITE mistake (always
# bind loopback) would pass the loopback cell and read as correct, and so would a script whose
# `ss` parsing was silently matching nothing.
#
# THE ASSERTION IS THE KERNEL'S, NOT OURS. The probe prints only the address it was ASKED for;
# what it actually bound is read out of `ss -ltn` by pid. A probe reporting its own opinion of the
# bind address would have passed just as happily before the fix.
#
# Linux only (uses `ss`). Windows equivalent wanted — see TODO; the Windows half of the fix is
# UNRUN, and `netstat -ano` + the pid is the same shape of check there.
#
# Usage: bench/verify-bind-address.sh [port]
# Exit:  0 = all cells PASS, 1 = any FAIL, 2 = setup failure.
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${1:-19731}"

c_red=$'\e[31m'; c_grn=$'\e[32m'; c_off=$'\e[0m'
command -v ss >/dev/null 2>&1 || { echo "${c_red}ss(8) not found; install iproute2${c_off}"; exit 2; }

SMOKE="$repo/SmokeTest/bin/Release/net10.0/SmokeTest"
if [[ ! -x "$SMOKE" ]]; then
    echo "building SmokeTest..."
    dotnet build "$repo/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo -f net10.0 >/tmp/bindaddr-build.$$.log 2>&1 \
        || { cat /tmp/bindaddr-build.$$.log; echo "${c_red}build failed${c_off}"; exit 2; }
    rm -f /tmp/bindaddr-build.$$.log
fi

failures=0
cells=0

# Wait until no listener remains on $PORT, so one cell cannot read the previous cell's socket.
# MANDATORY, not tidiness: SO_REUSEPORT is set on every IP listener here, so a new bind SUCCEEDS
# alongside a not-yet-reaped one and `ss` then shows two rows for the port. That is precisely how the
# first run of this script reported a FAIL against correct code (io-uring/any read the io-uring/loopback
# socket). Belt to the pid match's braces.
await_port_clear() {
    local deadline=$((SECONDS + 10))
    while (( SECONDS < deadline )); do
        [[ -z "$(ss -ltnH "sport = :$PORT" 2>/dev/null)" ]] && return 0
        sleep 0.1
    done
    return 1
}

# One cell: start the probe on $backend bound to $want, ask the kernel what it bound, compare.
probe() {
    local backend="$1" want="$2" label="$3"
    local log="/tmp/bindaddr.$$.log" pid bound deadline
    cells=$((cells + 1))

    if ! await_port_clear; then
        printf '  %sFAIL%s  %-22s port %s still has a listener from a previous cell\n' \
            "$c_red" "$c_off" "$label" "$PORT"
        failures=$((failures + 1))
        return
    fi

    "$SMOKE" "$backend" -n 1 --bind-probe "$want" --port "$PORT" >"$log" 2>&1 &
    pid=$!

    # Wait for the listener to appear rather than sleeping a guess: a fixed sleep that is too short
    # reads an empty `ss` and reports a FAIL that is really a race.
    #
    # Match on pid=$pid EXPLICITLY (hence -p, which -H alone does not imply). Reading whichever row
    # `ss` happened to list first is what made the first version of this script wrong.
    bound=""
    deadline=$((SECONDS + 10))
    while (( SECONDS < deadline )); do
        kill -0 "$pid" 2>/dev/null || break   # probe died; fall through and report its log
        bound=$(ss -ltnpH "sport = :$PORT" 2>/dev/null | grep -F "pid=$pid," | awk '{print $4}' | head -1)
        [[ -n "$bound" ]] && break
        sleep 0.2
    done

    local ok=0 detail=""
    if [[ -z "$bound" ]]; then
        detail="no listening socket on port $PORT ($(tail -2 "$log" | tr '\n' ' '))"
    else
        # ss prints "ADDR:PORT"; strip the port. 0.0.0.0 may render as "*".
        local addr="${bound%:*}"
        [[ "$addr" == "*" ]] && addr="0.0.0.0"
        if [[ "$addr" == "$want" ]]; then ok=1; else detail="kernel reports $addr, asked for $want"; fi
    fi

    kill -TERM "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null
    rm -f "$log"

    if (( ok )); then
        printf '  %sPASS%s  %-22s asked %-9s got %s\n' "$c_grn" "$c_off" "$label" "$want" "$want"
    else
        printf '  %sFAIL%s  %-22s %s\n' "$c_red" "$c_off" "$label" "$detail"
        failures=$((failures + 1))
    fi
}

echo "=== verify-bind-address (port $PORT) ==="
for backend in --io-uring --epoll --managed; do
    name="${backend#--}"
    echo "-- $name"
    probe "$backend" 127.0.0.1 "$name/loopback"
    probe "$backend" 0.0.0.0   "$name/any (control)"   # MUST bind wide, or the check proves nothing
done

echo
if (( failures == 0 )); then
    echo "${c_grn}=== verify-bind-address: $cells/$cells PASS ===${c_off}"
else
    echo "${c_red}=== verify-bind-address: $failures of $cells FAILED ===${c_off}"
fi
exit $(( failures == 0 ? 0 : 1 ))
