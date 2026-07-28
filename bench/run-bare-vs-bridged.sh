#!/usr/bin/env bash
# Is the 64KB -> 256KB goodput DECLINE a property of the transport, or of the Kestrel bridge?
#
# WHY THIS EXISTS. TODO item 1 records that every SocketSet leg falls from a 64KB to a 256KB payload
# (-24% to -53%) while BOTH Kestrel controls rise (+16%, +24%) in the same reshuffled passes - which rules
# out the client, the box and the payload shape, but does not say which of our two components owns it.
# The obvious move was to compare against the bare responder, and the file explicitly refuses it:
#
#   "the bare responder shows no collapse at all, which looks like a clean indictment of the Kestrel
#    bridge - except that comparison is cross-run, cross-shard-count, and confounded by HttpBench
#    funnelling all sends through two threads. Bridged io_uring at 16KB measures FASTER than bare io_uring
#    at 16KB, and a bridge cannot cost negative time. Next step is a clean bare-vs-bridged isolation in
#    one session at a matched shard count, not another sweep."
#
# This is that isolation. It runs the BARE responder at the SAME shard count, connection count, duration,
# CPU split and payloads as bench/run-tls-sizes.sh runs the bridged demo, so the two tables subtract.
# Run it in the same session as the bridged sweep and compare against that CSV.
#
# WHAT WOULD FALSIFY WHAT:
#   * Bare RISES 64KB -> 256KB while bridged FALLS  => the decline is the BRIDGE. Item 1 resolves to
#     "pipes and thread hops", which is where 2b-result already landed independently.
#   * Bare FALLS too                                 => the decline is in the TRANSPORT and item 1 is a
#     real transport defect that survived the 2026-07-28 allocation fix.
#   * Bare measures FASTER bridged than bare at any  => the harnesses are not comparable and neither
#     size (as happened at 16KB before)                 conclusion may be drawn. That is the check that
#                                                       stopped the last attempt; it is why this prints
#                                                       both and refuses to subtract them for you.
#
# Note the known asymmetry, deliberately NOT corrected: HttpBench funnels sends through the loop threads
# of a bare SocketSet, while AspNetDemo adds two Pipes and Kestrel's own pipeline. That IS the thing being
# measured. What is controlled here is everything else - shards, connections, duration, pinning, client.
#
# Usage:  ./run-bare-vs-bridged.sh
#         SIZES="65536 262144" REPS=7 SHARDS=12 ./run-bare-vs-bridged.sh
set -uo pipefail

SIZES=${SIZES:-"65536 262144"}
BACKENDS=${BACKENDS:-"io-uring epoll"}
SHARDS=${SHARDS:-12}          # MUST match the bridged sweep's SHARDS, or the comparison is void
CONNECTIONS=${CONNECTIONS:-64}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
REPS=${REPS:-7}               # pass 1 discarded; 6 scored, because 3 passes lie at 256KB
PORT=${PORT:-5098}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
SMOKE="$REPO/SmokeTest/bin/Release/net10.0/SmokeTest"

mkdir -p "$OUT"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/bare-vs-bridged-$STAMP.csv"
LOGS="$OUT/logs-bare-vs-bridged-$STAMP"
mkdir -p "$LOGS"

for tool in jq taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
[[ -x "$BOMB" ]] || { echo "missing $BOMB (run another rig once to fetch it)"; exit 1; }

echo "building SmokeTest (Release)..."
dotnet build "$REPO/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

source "$REPO/bench/cpu-split.sh"

echo "size,backend,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

echo
echo "bare responder, matched to the bridged sweep: ${BACKENDS// / } x ${SIZES// / } x $REPS passes"
echo "  shards=$SHARDS  -c $CONNECTIONS -d $DURATION  server=$SERVER_CPUS client=$CLIENT_CPUS"
echo "  csv: $CSV"
echo

measure() {
  local backend="$1" size="$2" rep="$3"
  local log="$LOGS/$backend.$size.r$rep.log"
  taskset -c "$SERVER_CPUS" "$SMOKE" --http --"$backend" -n "$SHARDS" -z "$size" --port "$PORT" >"$log" 2>&1 &
  local pid=$! banner="" i
  for ((i=0;i<80;i++)); do
    banner=$(grep -m1 'http-bench:' "$log" 2>/dev/null) && [[ -n "$banner" ]] && break
    sleep 0.25
  done
  if [[ -z "$banner" ]]; then
    echo "    $backend: NOSTART"; echo "$size,$backend,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Trust the banner (bench/README.md rule 5): confirm the backend AND the body size actually took.
  if [[ "$banner" != *"body=$size "* ]]; then
    echo "    $backend: BANNER MISMATCH -> $banner"; echo "$size,$backend,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$CONNECTIONS" -d "$WARMUP" -t 10s \
    "http://127.0.0.1:$PORT/" >/dev/null 2>&1
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$DURATION" -t 10s \
    "http://127.0.0.1:$PORT/" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  local rps; rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then echo "    $backend: no result"; echo "$size,$backend,$rep,,,,,,FAILED" >>"$CSV"; return; fi
  local p50 p99 errs mib
  p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  printf "    %-10s %9.0f rps  %9s MiB/s  p99 %7.0fus\n" "$backend" "$rps" "$mib" "$p99"
  echo "$size,$backend,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

for size in $SIZES; do
  echo "=== payload ${size} bytes ==="
  for ((rep=1; rep<=REPS; rep++)); do
    mapfile -t SHUF < <(printf '%s\n' $BACKENDS | shuf)
    for backend in "${SHUF[@]}"; do measure "$backend" "$size" "$rep"; done
  done
done

echo
echo "=== goodput MiB/s, median of scored passes (pass 1 discarded), min-max in brackets ==="
awk -F, 'NR>1 && $3>1 && $5!="" { k=$1"|"$2; n[k]++; v[k"_"n[k]]=$5+0 }
  END { for (k in n) { c=n[k]
      for(i=1;i<=c;i++) a[i]=v[k"_"i]
      for(x=1;x<c;x++)for(y=x+1;y<=c;y++) if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t}
      split(k,p,"|"); printf "%-9s %-10s %9.1f  [%.1f-%.1f]  n=%d\n", p[1], p[2], a[int((c+1)/2)], a[1], a[c], c } }' "$CSV" | sort -n

echo
echo "csv: $CSV"
echo "Compare against the bridged sweep's CSV at the SAME shard count. If bare rises where bridged falls,"
echo "the decline is the bridge. If bare falls too, it is the transport. Do not subtract the two tables"
echo "if bridged measures FASTER than bare at any payload - that means they are not comparable."
