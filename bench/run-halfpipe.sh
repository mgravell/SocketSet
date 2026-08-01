#!/usr/bin/env bash
# Does the outbound HALF-PIPE (CycleBuffer PipeWriter, direct Send, no pump/hop) beat the classic pump and
# BYO on the axis it targets -- CONCURRENCY at small/mid payloads? (branch cyclebuffer-halfpipe)
#
# WHAT THIS TESTS, pre-registered (2026-08-01). The half-pipe removes the per-connection ThreadPool pump
# task + its thread hop + the outbound Pipe state machine, at the cost of a COPY on send (Connection.Send
# instead of the transport's zero-copy writev). So:
#   * It should NOT win at 256KB -- BYO's zero-copy send wins there, and the copy the half-pipe reintroduces
#     is exactly what costs. This rig deliberately uses a SMALL payload where the copy is cheap and the
#     removed machinery dominates.
#   * The hypothesis is CONCURRENCY: at c64 the three legs should be close; if the pump-task-contention
#     theory (TODO "Two half-pipes" #1) is right, half-pipe should degrade LESS than classic (and than BYO)
#     as c goes 64 -> 128 -> 256, because there are no N pump tasks contending.
#   * FALSIFIER: if half-pipe does not pull ahead of classic as c rises, the pump-contention hypothesis is
#     wrong -- and that is the finding. Say so; do not bury it.
#
# House rules honored: banner-gated legs (a flag that parsed but was ignored measures identically), same
# session per rep, shuffled leg order, pass 1 discarded + 6 scored, ranges reported not just medians.
#
# Usage:  ./run-halfpipe.sh
#         BACKEND=epoll SIZE=16384 CONCS="64 128 256" REPS=7 ./run-halfpipe.sh
set -uo pipefail

BACKEND=${BACKEND:-io-uring}      # io-uring | epoll
TLS=${TLS:-}                      # empty=plaintext; "ssl"=transport OpenSSL TLS (--tls, https + curl -k)
SIZES=${SIZES:-1024}              # payload(s) to sweep; small on purpose (the half-pipe copies on send)
CONCS=${CONCS:-"64 128 256"}      # concurrency/-ies to sweep. Sweep ONE axis at a time for a clean read:
                                  # SIZES=1024 CONCS="64 128 256" (concurrency) or SIZES="256 4096 65536
                                  # 262144" CONCS=64 (the crossover: where does BYO's zero-copy retake it?)
SHARDS=${SHARDS:-12}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
REPS=${REPS:-7}                   # pass 1 discarded; 6 scored
PORT=${PORT:-5087}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

mkdir -p "$OUT"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/halfpipe-$STAMP.csv"
LOGS="$OUT/logs-halfpipe-$STAMP"
mkdir -p "$LOGS"

for tool in jq curl taskset shuf awk; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
[[ -x "$BOMB" ]] || { echo "missing $BOMB"; exit 1; }

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

source "$REPO/bench/cpu-split.sh"

# TLS wiring: append --tls to every leg, switch scheme to https, and gate the banner on tls=openssl.
TLS_ARG=""; SCHEME=http; TLS_WANT="tls=off"
if [[ "$TLS" == "ssl" ]]; then TLS_ARG="--tls"; SCHEME=https; TLS_WANT="tls=openssl"; fi

echo "size,conc,leg,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

# name|extra demo args|required /config fragment|forbidden fragment (or empty)
LEGS=(
  "classic|--classic|byo=off|half-pipe=1"
  "byo|--byo|byo=pipe|half-pipe=1"
  "halfpipe|--half-pipe|half-pipe=1|byo=pipe"
)

measure() {
  local name="$1" extra="$2" want="$3" forbid="$4" size="$5" conc="$6" rep="$7"
  local log="$LOGS/$name.s$size.c$conc.r$rep.log"
  taskset -c "$SERVER_CPUS" "$DEMO" --"$BACKEND" --shards "$SHARDS" $extra $TLS_ARG --port "$PORT" >"$log" 2>&1 &
  local pid=$! cfg="" i
  for ((i=0;i<80;i++)); do
    cfg=$(curl -sk --max-time 3 "$SCHEME://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
    sleep 0.4
  done
  if [[ -z "$cfg" ]]; then
    echo "    $name: no /config"; echo "$size,$conc,$name,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Trust the banner: the leg fragment present, its forbidden one absent, and the TLS mode as configured.
  if [[ "$cfg" != *"$want"* ]]; then
    echo "    $name: MISMATCH (wanted '$want') -> $cfg"; echo "$size,$conc,$name,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  if [[ -n "$forbid" && "$cfg" == *"$forbid"* ]]; then
    echo "    $name: has forbidden '$forbid' -> $cfg"; echo "$size,$conc,$name,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  if [[ "$cfg" != *"$TLS_WANT"* ]]; then
    echo "    $name: TLS MISMATCH (wanted '$TLS_WANT') -> $cfg"; echo "$size,$conc,$name,$rep,,,,,,TLS-MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  local url="$SCHEME://127.0.0.1:$PORT/payload?n=$size"
  taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$conc" -d "$WARMUP" -t 10s "$url" >/dev/null 2>&1
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$conc" -d "$DURATION" -t 10s "$url" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  local rps; rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then echo "    $name: no result"; echo "$size,$conc,$name,$rep,,,,,,FAILED" >>"$CSV"; return; fi
  local p50 p99 errs mib
  p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  printf "    %-8s s%-6s c%-4s %10.0f rps  %8s MiB/s  p99 %7.0fus  err %s\n" "$name" "$size" "$conc" "$rps" "$mib" "$p99" "$errs"
  echo "$size,$conc,$name,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

echo
echo "HALF-PIPE A/B on $BACKEND: 3 legs x $(wc -w <<<"$SIZES") size(s) x $(wc -w <<<"$CONCS") conc(s) x $REPS passes (pass 1 discarded)"
echo "  sizes='$SIZES' concs='$CONCS' shards=$SHARDS -d $DURATION  server=$SERVER_CPUS client=$CLIENT_CPUS"
echo "  csv: $CSV"
echo

for size in $SIZES; do
  for conc in $CONCS; do
    echo "=== payload ${size}B, concurrency c${conc} ==="
    for ((rep=1; rep<=REPS; rep++)); do
      mapfile -t SHUF < <(printf '%s\n' "${LEGS[@]}" | shuf)
      for spec in "${SHUF[@]}"; do
        IFS='|' read -r name extra want forbid <<<"$spec"
        measure "$name" "$extra" "$want" "$forbid" "$size" "$conc" "$rep"
      done
    done
  done
done

echo
echo "=== rps (and MiB/s), median of scored passes, min-max in brackets ==="
awk -F, 'NR>1 && $4>1 && $5!="" { k=$1"|"$2"|"$3; n[k]++; r[k"_"n[k]]=$5+0; m[k"_"n[k]]=$6+0 }
  END { for (k in n) { c=n[k]
      for(i=1;i<=c;i++){a[i]=r[k"_"i]; b[i]=m[k"_"i]}
      for(x=1;x<c;x++)for(y=x+1;y<=c;y++){ if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t} if(b[x]>b[y]){t=b[x];b[x]=b[y];b[y]=t} }
      split(k,p,"|"); printf "s%-7s c%-5s %-9s %10.0f rps  [%.0f-%.0f]   %8.1f MiB/s   n=%d\n",
        p[1], p[2], p[3], a[int((c+1)/2)], a[1], a[c], b[int((c+1)/2)], c } }' "$CSV" | sort -t_ -k1 | sort -n -k1.2 -k2.2

echo
echo "csv: $CSV"
echo "Reading: the crossover. Half-pipe copies on send, so as size grows BYO's zero-copy send should retake"
echo "the lead; below that it wins on cheaper machinery. Concurrency sweep (fixed small size): the win was"
echo "flat, not growing -- so it is per-request machinery, not pump-contention relief."
