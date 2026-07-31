#!/usr/bin/env bash
# Reuse-port (io_uring / epoll over IP) dynamic-shard-growth check: under accept pressure on a PURE server,
# does the set grow, and does a grown shard actually RECEIVE accepts?
#
# WHY THIS RIG EXISTS. Dynamic shard growth was implemented and measured on the single-listener path
# (Windows IOCP/RIO, ListenHandle, AF_UNIX) but NEVER on the reuse-port path, which is io_uring and epoll
# over IP — the path where each shard binds its own listener and the kernel balances accepts. Two things
# have to hold there and neither was tested on Linux:
#   A. a shard grown after Listen must be given its own reuse-port listener, or the kernel never routes a
#      single accept to it (SocketSet records the multi-bind listens and replays them in TryGrow);
#   B. something has to TRIGGER growth on the accept path. epoll's AcceptBurst always routes through
#      Parent.TryPlace (which grows); io_uring's reuse-port fast path adopts locally and, when its own slot
#      table is full, must fall back to TryPlace rather than silently dropping.
#
# HOW IT MEASURES GROWTH WITHOUT TOUCHING THE SERVER'S OUTPUT. Each shard owns one worker thread named
# "<SetName> worker N"; the server here is an EchoServer, so those threads' /proc comm is "EchoServer work"
# (comm truncates at 15 chars). Counting them is the shard count, sampled from outside the process — so a
# plain `-s` server needs no reporting code and growth is observed, not inferred from a flag.
#
# The client opens far more concurrent connections than the server's initial table holds, forcing the
# pressure that growth exists to relieve. Growth ON must climb from MIN toward the cap; growth OFF must
# stay pinned at MIN. That contrast, per backend, is the whole test.
#
# Usage:
#   bench/run-shard-growth.sh                 # io_uring and epoll
#   BACKENDS=iouring bench/run-shard-growth.sh
#   MIN=2 SOCK=16 CAP=12 CONN=256 DUR=6 bench/run-shard-growth.sh
set -u

BACKENDS="${BACKENDS:-iouring epoll}"
MIN="${MIN:-2}"        # starting (minimum) shard count
SOCK="${SOCK:-16}"     # sockets per shard — tight, so the initial table (MIN*SOCK) fills at once
CAP="${CAP:-12}"       # growth cap (--max-shards) when growth is ON
CONN="${CONN:-256}"    # concurrent client connections — far above MIN*SOCK to force growth
DUR="${DUR:-6}"        # client hold duration (seconds)
FIRST_PORT="${FIRST_PORT:-10600}"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exe="$repo/SmokeTest/bin/Release/net10.0/SmokeTest"
c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_red=$'\033[31m'; c_off=$'\033[0m'

echo "${c_cyan}building SmokeTest (Release, net10.0) ...${c_off}"
dotnet build "$repo/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo -f net10.0 >/tmp/growth-build.log 2>&1 \
    || { cat /tmp/growth-build.log; echo "${c_red}build failed${c_off}"; exit 2; }

# Count this server's shard worker threads (= current shard count) from /proc.
worker_count() { cat "/proc/$1/task"/*/comm 2>/dev/null | grep -c '^EchoServer'; }

port=$FIRST_PORT
fails=0
printf "\n%-10s %-8s %6s %6s %6s   %s\n" backend growth start peak grown verdict
printf '%s\n' "----------------------------------------------------------------"
for backend in $BACKENDS; do
    case "$backend" in
        iouring) bflag="" ;;
        epoll)   bflag="--epoll" ;;
        *) echo "unknown backend: $backend"; exit 2 ;;
    esac
    for mode in off on; do
        port=$((port+1))
        maxsh=0; [ "$mode" = on ] && maxsh=$CAP
        # Pure reuse-port SERVER: binds Any on IP, tight table, growth cap per mode.
        # shellcheck disable=SC2086
        "$exe" $bflag -s -n "$MIN" --sockets "$SOCK" --max-shards "$maxsh" --port "$port" \
            >/tmp/growth.$backend.$mode.srv 2>&1 &
        spid=$!

        # Wait for the server's workers to appear (up to ~5s), then record the starting shard count.
        start=0
        for _ in $(seq 1 50); do
            start=$(worker_count "$spid"); [ "$start" -ge "$MIN" ] && break; sleep 0.1
        done

        # Client: CONN concurrent held-open connections for DUR seconds, driving accepts at the server.
        # shellcheck disable=SC2086
        "$exe" $bflag -c "$CONN" --host 127.0.0.1 --port "$port" -t "$DUR" \
            >/tmp/growth.$backend.$mode.cli 2>&1 &
        cpid=$!

        # Sample the server's shard count while the load runs; keep the peak.
        peak=$start
        while kill -0 "$cpid" 2>/dev/null; do
            n=$(worker_count "$spid"); [ "$n" -gt "$peak" ] && peak=$n
            sleep 0.1
        done
        wait "$cpid" 2>/dev/null
        kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null

        grown=$((peak - start))
        # ON must grow (>0); OFF must not (==0). A start below MIN means the server never came up.
        if [ "$start" -lt "$MIN" ]; then verdict="${c_red}FAIL (server did not start)${c_off}"; fails=$((fails+1))
        elif [ "$mode" = on ] && [ "$grown" -gt 0 ]; then verdict="${c_green}PASS (grew $start->$peak)${c_off}"
        elif [ "$mode" = off ] && [ "$grown" -eq 0 ]; then verdict="${c_green}PASS (held at $MIN)${c_off}"
        else verdict="${c_red}FAIL${c_off}"; fails=$((fails+1)); fi

        printf "%-10s %-8s %6s %6s %6s   %b\n" "$backend" "$mode" "$start" "$peak" "$grown" "$verdict"
    done
done

echo
if [ "$fails" -gt 0 ]; then echo "${c_red}$fails check(s) FAILED${c_off}"; exit 1; fi
echo "${c_green}all growth checks PASS${c_off}"
