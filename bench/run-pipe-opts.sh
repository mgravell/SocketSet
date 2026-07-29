#!/usr/bin/env bash
# Do the bridge's PIPE OPTIONS matter? (TODO item 2d)
#
# WHY THIS EXISTS. The bare-vs-bridged isolation put the Kestrel bridge at 24-40% at 256KB while the bare
# transport does not decline at all, and zero-copy send then recovered +45.1% of it on io_uring. What was
# never examined is that the bridge's two Pipes are OURS to configure and were left almost entirely at
# their defaults: ThreadPool schedulers on both ends, the framework's ~4KB blocks, an unpinned pool.
#
# The 4KB block size is not a detail. Measured 2026-07-29 with SS_URING_STATS: a 256KB response is
# **exactly 65.00 iovec segments** at the default block size and **5.00** at a 64KB block. That one number
# drives three separate costs at once:
#   * iovec count per writev, and WriteAll iterations per response;
#   * one GCHandle pin per segment on the zero-copy send path (65 pins + 65 disposes per response) -
#     unless the pool is pinned, which --pipe-pinned makes it, at which point the branch disappears;
#   * IOCP's MaxSendPages cap is 64, so 65 segments is one past it - which is why IOCP's zero-copy send
#     declined every 256KB response and measured as "no gain". (Windows-only; not exercised here.)
#
# LEGS. classic is the shipped bridge (no UsePipe) and is the constant control. The three byo legs isolate
# one change each, so the comparison is byo-vs-byo rather than byo-vs-classic - otherwise the reading would
# be credited with whatever pipe mode itself costs.
#
# PRE-REGISTERED: bigger blocks should help at 256KB and do nothing much at 64KB (where a response is ~1-2
# segments either way). Pinning should be a smaller, additive win, and only on top of zero-copy send - it
# removes pins, not copies. If bigger blocks do NOT help at 256KB, then segment count was not a real cost
# and the 65-vs-64 IOCP story, while still true as an explanation of the DECLINE, buys nothing by itself.
#
# Usage:  ./run-pipe-opts.sh
#         SIZES="65536 262144" REPS=5 BACKEND=io-uring ./run-pipe-opts.sh
set -uo pipefail

BACKEND=${BACKEND:-io-uring}
SIZES=${SIZES:-"65536 262144"}
SHARDS=${SHARDS:-12}
CONNECTIONS=${CONNECTIONS:-64}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
REPS=${REPS:-5}          # pass 1 discarded
PORT=${PORT:-5082}
SEG=${SEG:-65536}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

mkdir -p "$OUT"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/pipe-opts-$STAMP.csv"
LOGS="$OUT/logs-pipe-opts-$STAMP"
mkdir -p "$LOGS"

for tool in jq curl taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
[[ -x "$BOMB" ]] || { echo "missing $BOMB"; exit 1; }

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }
source "$REPO/bench/cpu-split.sh"

echo "size,leg,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

# name | extra args | /config fragment that MUST be present | fragment that must be ABSENT
LEGS=(
  "classic|||byo=pipe"          # no required fragment; must NOT report byo=pipe
  "byo|--byo|byo=pipe|pipeseg="
  "byo+seg|--byo --pipe-segment $SEG|pipeseg=$SEG|pipepinned="
  "byo+seg+pin|--byo --pipe-segment $SEG --pipe-pinned|pipepinned=1|"
)

measure() {
  local name="$1" extra="$2" want="$3" nowant="$4" size="$5" rep="$6"
  taskset -c "$SERVER_CPUS" "$DEMO" --"$BACKEND" --shards "$SHARDS" $extra --port "$PORT" \
    >"$LOGS/${name//+/-}.$size.r$rep.log" 2>&1 &
  local pid=$! cfg="" i
  for ((i=0;i<80;i++)); do
    cfg=$(curl -s --max-time 3 "http://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
    sleep 0.4
  done
  if [[ -z "$cfg" ]]; then
    echo "    $name: no /config"; echo "$size,$name,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Gate BOTH ways: the flag that must have taken, and the one that must NOT be set - otherwise two legs
  # can silently be the same configuration measured twice.
  if [[ -n "${want// /}" && "$cfg" != *"$want"* ]] || [[ -n "${nowant// /}" && "$cfg" == *"$nowant"* ]]; then
    echo "    $name: CONFIG MISMATCH -> $cfg"; echo "$size,$name,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  local url="http://127.0.0.1:$PORT/payload?n=$size"
  taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$CONNECTIONS" -d "$WARMUP" -t 10s "$url" >/dev/null 2>&1
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$DURATION" -t 10s "$url" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  local rps; rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then echo "    $name: no result"; echo "$size,$name,$rep,,,,,,FAILED" >>"$CSV"; return; fi
  local p50 p99 errs mib
  p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  printf "    %-12s %9.0f rps  %9s MiB/s  p99 %7.0fus\n" "$name" "$rps" "$mib" "$p99"
  echo "$size,$name,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

echo
echo "pipe-options A/B on $BACKEND: ${#LEGS[@]} legs x $(wc -w <<<"$SIZES") sizes x $REPS passes (pass 1 discarded)"
echo "  segment under test: $SEG   shards=$SHARDS -c $CONNECTIONS -d $DURATION"
echo "  csv: $CSV"
echo

for size in $SIZES; do
  echo "=== payload ${size} bytes ==="
  for ((rep=1; rep<=REPS; rep++)); do
    mapfile -t SHUF < <(printf '%s\n' "${LEGS[@]}" | shuf)
    for spec in "${SHUF[@]}"; do
      IFS='|' read -r name extra want nowant <<<"$spec"
      measure "$name" "$extra" "$want" "$nowant" "$size" "$rep"
    done
  done
done

echo
echo "=== goodput MiB/s, median of scored passes, min-max in brackets ==="
awk -F, 'NR>1 && $3>1 && $5!="" { k=$1"|"$2; n[k]++; v[k"_"n[k]]=$5+0 }
  END { for (k in n) { c=n[k]
      for(i=1;i<=c;i++) a[i]=v[k"_"i]
      for(x=1;x<c;x++)for(y=x+1;y<=c;y++) if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t}
      split(k,p,"|"); printf "%-9s %-12s %9.1f  [%.1f-%.1f]  n=%d\n", p[1], p[2], a[int((c+1)/2)], a[1], a[c], c } }' "$CSV" | sort -n
echo
echo "csv: $CSV"
