#!/usr/bin/env bash
# TODO item 0c: io_uring does not always exit on SIGINT after sustained load. Observed 2026-07-28,
# reproduced every time, never diagnosed. This script reproduces it and NAMES the blocked thread.
#
# WHAT THE FILE USED TO SUSPECT, AND WHY THAT IS REFUTED. The entry blamed teardown waiting on
# RecvArmed/SendBusy/CancelPending clearing per connection (IoUringShard.TryFinalize). Nothing waits on
# that: shard pump threads are created at one site (SocketSet.cs:49-61) with IsBackground = true, and
# SocketSet.Dispose only calls shard.Stop() - it never joins them. A background thread cannot hold a .NET
# process open, so a slot that never finalizes cannot be this symptom's cause however stuck it is.
# (That does not clear a connection-state LEAK, which would strand a slot in normal operation. Two
# separate investigations; the entry had merged them.)
#
# So the blocker is elsewhere: the main thread after httpStop.Wait() returns (SmokeTest/Program.cs:284,
# which then disposes HttpBench - a SocketSet), a finalizer at exit, or the SIGINT handler never running.
# This script does not guess between them - it reads what every thread is actually blocked in.
#
# No debugger is needed and none is installed on this host (no gdb, no eu-stack, no dotnet-dump):
# /proc/<pid>/task/*/comm names each thread and /proc/<pid>/task/*/wchan says what it is sleeping in.
# That distinguishes "stuck in io_uring_enter" from "futex" from "close()" without a symbol server.
set -uo pipefail

SHARDS=${SHARDS:-8}
CONNECTIONS=${CONNECTIONS:-64}
LOAD=${LOAD:-20s}      # ~200k requests at this host's small-message rate, which is what triggered it
PORT=${PORT:-5097}
GRACE=${GRACE:-80}     # the entry's own threshold: "fails to exit within 80s"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BOMB="$REPO/bench/.tools/bombardier"
SMOKE="$REPO/SmokeTest/bin/Release/net10.0/SmokeTest"
[[ -x "$BOMB" ]] || { echo "missing $BOMB"; exit 1; }

echo "building SmokeTest (Release)..."
dotnet build "$REPO/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }
source "$REPO/bench/cpu-split.sh"

dump_threads() { # $1 = pid, $2 = label
  local pid=$1 label=$2
  echo "--- threads at $label (pid $pid) ---"
  echo "process state: $(awk '/^State:/{$1="";print}' /proc/$pid/status 2>/dev/null)"
  local t
  for t in /proc/$pid/task/*; do
    [[ -d "$t" ]] || continue
    printf "  tid %-8s %-20s wchan=%s\n" \
      "$(basename "$t")" "$(cat "$t/comm" 2>/dev/null)" "$(cat "$t/wchan" 2>/dev/null || echo '?')"
  done
}

run_case() { # $1 = "idle" or "loaded"
  local mode="$1"
  echo
  echo "=============== SIGINT after $mode ==============="
  taskset -c "$SERVER_CPUS" "$SMOKE" --http --io-uring -n "$SHARDS" -z 512 --port "$PORT" \
    >/tmp/sigint-$mode.$$.log 2>&1 &
  local pid=$!
  local i banner=""
  for ((i=0;i<80;i++)); do
    banner=$(grep -m1 'http-bench:' /tmp/sigint-$mode.$$.log 2>/dev/null) && [[ -n "$banner" ]] && break
    sleep 0.25
  done
  [[ -n "$banner" ]] || { echo "NOSTART"; kill -9 $pid 2>/dev/null; return; }

  if [[ "$mode" == loaded ]]; then
    echo "applying load: -c $CONNECTIONS -d $LOAD"
    taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$CONNECTIONS" -d "$LOAD" -t 10s \
      "http://127.0.0.1:$PORT/" 2>/dev/null | jq -r '"  served: \(.result.req1xx + .result.req2xx) requests"'
    sleep 1
  fi

  dump_threads $pid "steady state, before SIGINT"
  echo "sending SIGINT..."
  local t0=$SECONDS
  kill -INT $pid
  local exited=0 waited
  for ((waited=0; waited<GRACE; waited++)); do
    kill -0 $pid 2>/dev/null || { exited=1; break; }
    sleep 1
  done

  if [[ $exited == 1 ]]; then
    echo "RESULT ($mode): exited after $((SECONDS - t0))s"
  else
    echo "RESULT ($mode): STILL ALIVE after ${GRACE}s - this is the reproduction"
    dump_threads $pid "${GRACE}s after SIGINT"
    echo "--- last 15 lines of its output ---"
    tail -15 /tmp/sigint-$mode.$$.log
    kill -9 $pid 2>/dev/null
  fi
  wait $pid 2>/dev/null
  rm -f /tmp/sigint-$mode.$$.log
}

# Idle first: the entry says idle shuts down promptly, so it is the control. If idle ALSO hangs, the
# "after sustained load" framing is wrong and the bug is unconditional.
run_case idle
run_case loaded

echo
echo "Read the wchan column of the loaded case against the idle one. A main thread in futex_wait with"
echo "every shard thread gone points at a finalizer or a join; shard threads still in io_uring_enter point"
echo "at a loop that never observed the stop; and any thread in a close()/unix_release path points at fd"
echo "teardown. Whatever it is, it is NOT TryFinalize - those threads are background and are not joined."
