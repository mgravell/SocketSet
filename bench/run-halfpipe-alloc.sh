#!/usr/bin/env bash
# Allocation + RSS axis for the half-pipe (branch cyclebuffer-halfpipe). The throughput A/B (run-halfpipe.sh)
# showed a small win; the "leaner machinery" claim should ALSO show as fewer gen-0 collections / fewer bytes
# allocated per request. This drives a FIXED request COUNT (not a duration) at each leg so allocation is
# directly comparable, and reads GC/RSS from /stats before and after.
#
# Reads /stats: gen0/1/2 (GC.CollectionCount), allocatedBytes (GC.GetTotalAllocatedBytes, process-wide),
# gcHeapBytes, rssBytes (Environment.WorkingSet). One leg per process (fresh server each), so process-wide
# counters are clean. Banner-gated like run-halfpipe.sh.
#
# Usage:  ./run-halfpipe-alloc.sh          (defaults below)
#         SIZE=16384 N=1000000 ./run-halfpipe-alloc.sh
set -uo pipefail

BACKEND=${BACKEND:-io-uring}
SIZE=${SIZE:-1024}
CONC=${CONC:-64}
N=${N:-1000000}                   # fixed request count per leg
SHARDS=${SHARDS:-12}
PORT=${PORT:-5088}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

for tool in jq curl taskset; do command -v "$tool" >/dev/null || { echo "missing $tool"; exit 1; }; done
[[ -x "$BOMB" ]] || { echo "missing $BOMB"; exit 1; }

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }
source "$REPO/bench/cpu-split.sh"

# name|extra|required banner|forbidden banner
LEGS=(
  "classic|--classic|byo=off|half-pipe=1"
  "byo|--byo|byo=pipe|half-pipe=1"
  "halfpipe|--half-pipe|half-pipe=1|byo=pipe"
)

echo
echo "HALF-PIPE alloc/RSS A/B on $BACKEND: payload=${SIZE}B -c $CONC N=$N requests/leg"
printf "%-9s %8s %8s %8s %12s %14s %10s\n" "leg" "gen0" "gen1" "gen2" "B/req" "totalMB" "rssMB"

stat() { curl -s --max-time 5 "http://127.0.0.1:$PORT/stats"; }

for spec in "${LEGS[@]}"; do
  IFS='|' read -r name extra want forbid <<<"$spec"
  taskset -c "$SERVER_CPUS" "$DEMO" --"$BACKEND" --shards "$SHARDS" $extra --port "$PORT" >/dev/null 2>&1 &
  pid=$!
  cfg=""; for ((i=0;i<80;i++)); do cfg=$(curl -s --max-time 3 "http://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break; sleep 0.4; done
  if [[ "$cfg" != *"$want"* || ( -n "$forbid" && "$cfg" == *"$forbid"* ) ]]; then
    echo "  $name: banner MISMATCH -> skipping"; kill $pid 2>/dev/null; wait $pid 2>/dev/null; continue
  fi
  url="http://127.0.0.1:$PORT/payload?n=$SIZE"
  # warm up (JIT, pools, connections) so steady-state allocation is what we measure
  taskset -c "$CLIENT_CPUS" "$BOMB" -k -c "$CONC" -n 200000 -t 10s "$url" >/dev/null 2>&1
  b=$(stat)
  taskset -c "$CLIENT_CPUS" "$BOMB" -k -c "$CONC" -n "$N" -t 10s "$url" >/dev/null 2>&1
  a=$(stat)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  g0=$(( $(jq -r .gen0 <<<"$a") - $(jq -r .gen0 <<<"$b") ))
  g1=$(( $(jq -r .gen1 <<<"$a") - $(jq -r .gen1 <<<"$b") ))
  g2=$(( $(jq -r .gen2 <<<"$a") - $(jq -r .gen2 <<<"$b") ))
  db=$(( $(jq -r .allocatedBytes <<<"$a") - $(jq -r .allocatedBytes <<<"$b") ))
  bpr=$(awk -v d="$db" -v n="$N" 'BEGIN{printf "%.1f", d/n}')
  totmb=$(awk -v d="$db" 'BEGIN{printf "%.0f", d/1048576}')
  rssmb=$(awk -v r="$(jq -r .rssBytes <<<"$a")" 'BEGIN{printf "%.0f", r/1048576}')
  printf "%-9s %8d %8d %8d %12s %14s %10s\n" "$name" "$g0" "$g1" "$g2" "$bpr" "$totmb" "$rssmb"
done

echo
echo "Reading: fewer gen0 + lower B/req at the same payload = the half-pipe's leaner machinery showing up on"
echo "the allocation axis. rssMB is footprint (pinned-pool legs cost more). This drives a FIXED request count"
echo "so B/req is directly comparable across legs."
