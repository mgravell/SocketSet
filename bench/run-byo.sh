#!/usr/bin/env bash
# Does zero-copy send (BYO buffers) buy anything on the ASP.NET bridge?
#
# WHAT THIS COMPARES, and why it is not the same as the size sweep. `--byo` selects the bridge that hands
# its pipes to the transport via ctx.UsePipe, so the transport's own zero-copy send path can run; without
# it, AspNetConnection copies inbound and runs its own outbound pump. Phase 1 measured the two AT PARITY
# on IOCP (that was the prediction and the point - relocating the bridge into the library is not supposed
# to be faster), so ANY difference here is the per-backend zero-copy path, which is what we are measuring.
#
# MEASURE AGAINST `--byo`, NOT AGAINST THE CALLBACK PATH. The classic leg is included as the shipped
# baseline, but the honest zero-copy comparison is byo-with-zero-copy against byo-without, or the reading
# credits zero-copy with whatever pipe mode itself costs. On a backend with no zero-copy path the two byo
# legs are identical by construction.
#
# WHAT TO EXPECT, PRE-REGISTERED (2026-07-29, io_uring):
#   * The prize is at 256KB, not 16KB. The bridge costs io_uring 24.5% at 256KB and ~2% at 64KB.
#   * IOCP measured +3.5% at 16KB and nothing at 256KB - but IOCP caps at 64 WSABUFs and a 256KB response
#     through Kestrel's 4KB pipe blocks is ~64 segments, so it plausibly DECLINED and fell back to copying
#     at exactly the payload of interest. io_uring's IovMax is 1024, so that ceiling is not in play.
#   * THE PIN COST MAY EAT IT. With an unpinned MemoryPool this takes one GCHandle pin per SEGMENT: ~64
#     pins + 64 disposes per 256KB response, against one copy saved. A null or NEGATIVE result is
#     therefore ambiguous until the pool is pinned (TODO 2d), and must not be read as "copies don't cost".
#     Check SS_URING_STATS=1: zero-copy=N segments proves the path was actually taken.
#
# Usage:  ./run-byo.sh
#         BACKEND=io-uring SIZES="65536 262144" REPS=7 ./run-byo.sh
set -uo pipefail

BACKEND=${BACKEND:-io-uring}      # io-uring | epoll | iocp (the demo's flag, minus the leading --)
SIZES=${SIZES:-"65536 262144"}
SHARDS=${SHARDS:-12}
CONNECTIONS=${CONNECTIONS:-64}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
REPS=${REPS:-7}                   # pass 1 discarded; 6 scored, because 3 passes lie at 256KB
PORT=${PORT:-5081}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

mkdir -p "$OUT"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/byo-$STAMP.csv"
LOGS="$OUT/logs-byo-$STAMP"
mkdir -p "$LOGS"

for tool in jq curl taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
[[ -x "$BOMB" ]] || { echo "missing $BOMB"; exit 1; }

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

source "$REPO/bench/cpu-split.sh"

echo "size,leg,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

# name|extra demo args|expected /config fragment
LEGS=(
  "classic|| "
  "byo|--byo|byo=pipe"
)

measure() {
  local name="$1" extra="$2" want="$3" size="$4" rep="$5"
  local log="$LOGS/$name.$size.r$rep.log"
  taskset -c "$SERVER_CPUS" "$DEMO" --"$BACKEND" --shards "$SHARDS" $extra --port "$PORT" >"$log" 2>&1 &
  local pid=$! cfg="" i
  for ((i=0;i<80;i++)); do
    cfg=$(curl -s --max-time 3 "http://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
    sleep 0.4
  done
  if [[ -z "$cfg" ]]; then
    echo "    $name: no /config"; echo "$size,$name,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Never measure a leg whose flag was ignored - the whole point is whether the byo path ran.
  if [[ -n "${want// /}" && "$cfg" != *"$want"* ]]; then
    echo "    $name: MISMATCH (wanted '$want') -> $cfg"; echo "$size,$name,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # ...and the classic leg must NOT report byo, or the two legs are the same thing measured twice.
  if [[ "$name" == classic && "$cfg" == *"byo=pipe"* ]]; then
    echo "    classic: reports byo=pipe -> $cfg"; echo "$size,$name,$rep,,,,,,MISMATCH" >>"$CSV"
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
  printf "    %-8s %9.0f rps  %9s MiB/s  p99 %7.0fus\n" "$name" "$rps" "$mib" "$p99"
  echo "$size,$name,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

echo
echo "BYO zero-copy A/B on $BACKEND: ${#LEGS[@]} legs x $(wc -w <<<"$SIZES") sizes x $REPS passes (pass 1 discarded)"
echo "  shards=$SHARDS -c $CONNECTIONS -d $DURATION  server=$SERVER_CPUS client=$CLIENT_CPUS"
echo "  csv: $CSV"
echo

for size in $SIZES; do
  echo "=== payload ${size} bytes ==="
  for ((rep=1; rep<=REPS; rep++)); do
    mapfile -t SHUF < <(printf '%s\n' "${LEGS[@]}" | shuf)
    for spec in "${SHUF[@]}"; do
      IFS='|' read -r name extra want <<<"$spec"
      measure "$name" "$extra" "$want" "$size" "$rep"
    done
  done
done

echo
echo "=== goodput MiB/s, median of scored passes, min-max in brackets ==="
awk -F, 'NR>1 && $3>1 && $5!="" { k=$1"|"$2; n[k]++; v[k"_"n[k]]=$5+0 }
  END { for (k in n) { c=n[k]
      for(i=1;i<=c;i++) a[i]=v[k"_"i]
      for(x=1;x<c;x++)for(y=x+1;y<=c;y++) if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t}
      split(k,p,"|"); printf "%-9s %-8s %9.1f  [%.1f-%.1f]  n=%d\n", p[1], p[2], a[int((c+1)/2)], a[1], a[c], c } }' "$CSV" | sort -n

echo
echo "csv: $CSV"
echo "If byo does not beat classic, check SS_URING_STATS=1 for a non-zero zero-copy= segment count before"
echo "concluding anything: a path that silently declined measures identical to one that ran and did not pay."
