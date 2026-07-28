#!/usr/bin/env bash
# Linux counterpart to Run-Matrix.ps1: benchmark the AspNetDemo transports through bombardier.
#
# Same guards as the Windows harness, which were all learned the hard way (see AspNetDemo/RESULTS.md):
#   * /config is checked before every measurement, and a leg whose reported backend or TLS mode is not
#     what was asked for is recorded MISMATCH and excluded - silent fallback is the failure mode here.
#   * server and load generator are pinned to disjoint CPU sets, with DOTNET_PROCESSOR_COUNT/GOMAXPROCS
#     told the truth, since both runtimes size themselves before affinity is applied.
#   * the first pass is discarded as HOST warm-up: a cold machine runs at boost clocks and the transient
#     spans a whole pass, not a request.
#   * every leg is measured N times in a RESHUFFLED order and reported as a median with spread, and the
#     spread is compared against the between-leg range so "this does not rank transports" is explicit.
#   * keep-alive only: connection churn exhausts the ephemeral-port pool and corrupts later legs.
#   * kTLS legs are additionally checked against /proc/net/tls_stat, because /config can only report what
#     was CONFIGURED - see bench/ktls-verify.sh.
#   * CPU pinning splits by physical core, not logical index - see bench/cpu-split.sh.
#
# io_uring note: Docker's default seccomp profile BLOCKS the io_uring syscalls, so the backend silently
# falls back to managed sockets. Run the container with --security-opt seccomp=unconfined, and trust the
# /config check rather than the flag you passed. (The current bench host runs Linux on bare metal, so
# this does not apply there - but every Linux result recorded before 2026-07-28 was measured under it.)
set -uo pipefail

DURATION=${DURATION:-10s}
WARMUP=${WARMUP:-3s}
CONNECTIONS=${CONNECTIONS:-128}
REPS=${REPS:-4}            # first pass is host warm-up and is discarded
WARMUP_PASSES=${WARMUP_PASSES:-1}
PORT=${PORT:-5080}
FILTER=${FILTER:-}
# Sweepable, matching Run-TlsSizes.ps1's -Shards. Kestrel legs ignore it (its transport has no shards);
# SocketSet legs are expanded one leg per shard count, named e.g. "epoll+tls/s8". Worth sweeping because
# more shards is not obviously better: each shard is a dedicated loop thread competing with the
# ThreadPool that runs Kestrel and the bridge pump, on the same pinned cores. The top of the range should
# be the SERVER half's logical CPU count - 12 on the current host, hence 4/8/12.
SHARDS=${SHARDS:-"4 8 12"}
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

mkdir -p "$OUT" "$(dirname "$BOMB")"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/matrix-linux-$STAMP.csv"
LOGS="$OUT/logs-linux-$STAMP"
mkdir -p "$LOGS"

if [[ ! -x "$BOMB" ]]; then
  echo "fetching bombardier..."
  curl -sSL -o "$BOMB" https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-linux-amd64
  chmod +x "$BOMB"
fi

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

# --- CPU split, by physical core rather than by logical index: see bench/cpu-split.sh for why the
# --- obvious lower/upper-half split gives the server and the load generator the SAME cores here.
NCPU=$(nproc)
source "$REPO/bench/cpu-split.sh"

source "$REPO/bench/ktls-verify.sh"
KTLS_CHECKABLE=1; ktls_available || KTLS_CHECKABLE=0

# --- legs: name|demo args|expected transport|expected tls|shardable ---
# "shardable" is 1 for the SocketSet legs (expanded one leg per SHARDS entry) and 0 for the Kestrel
# controls, whose transport has no shard concept and whose /config omits the field entirely.
BASE_LEGS=(
  "kestrel|--kestrel|kestrel-sockets|off|0"
  "kestrel+tls|--kestrel --tls|kestrel-sockets|kestrel/sslstream|0"
  "epoll|--epoll|socketset/epoll|off|1"
  "epoll+tls|--epoll --tls|socketset/epoll|openssl|1"
  "iouring|--io-uring|socketset/io_uring|off|1"
  "iouring+tls|--io-uring --tls|socketset/io_uring|openssl|1"
  "iouring+ktls|--ktls|socketset/auto(iouring)|ktls (openssl + kernel offload)|1"
)

# Expand the shard dimension. Legs carry their expected shard count so /config can be checked for it -
# a --shards that was ignored would otherwise be indistinguishable from one that took.
LEGS=()
for spec in "${BASE_LEGS[@]}"; do
  IFS='|' read -r _n _a _xt _xtls _sh <<<"$spec"
  if [[ "$_sh" == "1" ]]; then
    for s in $SHARDS; do LEGS+=("$_n/s$s|$_a --shards $s|$_xt|$_xtls|$s"); done
  else
    LEGS+=("$_n|$_a|$_xt|$_xtls|")
  fi
done

echo "leg,rep,order,rps,lat_mean_us,lat_p50_us,lat_p99_us,req2xx,errors,status,config" > "$CSV"

wait_config() {  # $1=scheme
  local url="$1://127.0.0.1:$PORT/config" i
  for ((i=0; i<80; i++)); do
    local body; body=$(curl -sk --max-time 3 "$url" 2>/dev/null)
    if [[ -n "$body" ]]; then echo "$body"; return 0; fi
    sleep 0.4
  done
  return 1
}

run_leg() {  # $1=name $2=args $3=expect_transport $4=expect_tls $5=expect_shards $6=rep $7=order
  local name="$1" args="$2" xt="$3" xtls="$4" xsh="$5" rep="$6" order="$7"
  local scheme=http; [[ "$args" == *--tls* || "$args" == *--ktls* ]] && scheme=https
  # Shard-expanded legs are named "epoll+tls/s8"; the slash is a path separator, so log files use a
  # flattened form. (Caught by a smoke run - the first sharded run wrote no logs and reported NO /config.)
  local fn="${name//\//-}"

  taskset -c "$SERVER_CPUS" "$DEMO" $args --port "$PORT" >"$LOGS/$fn.r$rep.server.log" 2>&1 &
  local pid=$!
  local cfg; cfg=$(wait_config "$scheme") || {
    echo "  $name: NO /config"; echo "$name,$rep,$order,,,,,,,NOSTART," >> "$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return; }

  # Guard against measuring the wrong thing (silent fallback is THE failure mode here).
  if [[ "$cfg" != *"transport=$xt"* || "$cfg" != *"tls=$xtls"* ]]; then
    echo "  $name: MISMATCH -> $cfg"
    echo "$name,$rep,$order,,,,,,,MISMATCH,\"$cfg\"" >> "$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  if [[ -n "$xsh" && "$cfg" != *"shards=$xsh "* ]]; then
    echo "  $name: SHARD MISMATCH (wanted shards=$xsh) -> $cfg"
    echo "$name,$rep,$order,,,,,,,SHARD-MISMATCH,\"$cfg\"" >> "$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  local ktls_before=""
  [[ "$args" == *--ktls* && $KTLS_CHECKABLE == 1 ]] && ktls_before=$(ktls_tx_count)

  taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$WARMUP" -t 5s \
    "$scheme://127.0.0.1:$PORT/plaintext" >/dev/null 2>&1

  # The warm-up pass has now opened real connections, so the kernel has either taken the TLS sockets or
  # it has not. /config only proves kTLS was CONFIGURED; this proves it ENGAGED. Checked here rather than
  # after the scored pass so a silently-userspace leg costs a warm-up rather than a full measurement.
  if [[ -n "$ktls_before" ]]; then
    local ktls_after; ktls_after=$(ktls_tx_count)
    if [[ -n "$ktls_after" && "$ktls_after" == "$ktls_before" ]]; then
      echo "  $name: KTLS-NOT-ENGAGED (TlsTxSw stayed at $ktls_before) -> measuring userspace OpenSSL"
      echo "$name,$rep,$order,,,,,,,KTLS-NOT-ENGAGED,\"TlsTxSw=$ktls_before\"" >> "$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
    fi
  fi
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$DURATION" -t 5s \
    "$scheme://127.0.0.1:$PORT/plaintext" 2>"$LOGS/$fn.r$rep.err")
  echo "$j" > "$LOGS/$fn.r$rep.json"

  kill $pid 2>/dev/null; wait $pid 2>/dev/null
  sleep 1

  local rps p50 p99 mean ok errs
  rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then echo "  $name: no result"; echo "$name,$rep,$order,,,,,,,FAILED," >> "$CSV"; return; fi
  mean=$(jq -r '.result.latency.mean' <<<"$j"); p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j"); ok=$(jq -r '.result.req2xx' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  printf "  %-14s %10.0f rps  p99 %8.0fus  errs=%s\n" "$name" "$rps" "$p99" "$errs"
  echo "$name,$rep,$order,${rps%.*},${mean%.*},${p50%.*},${p99%.*},$ok,$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)," >> "$CSV"
}

echo
echo "matrix: ${#LEGS[@]} legs x $REPS passes (first $WARMUP_PASSES discarded as host warm-up)"
echo "  cpus     : $NCPU  server=$SERVER_CPUS client=$CLIENT_CPUS (split by physical core)"
echo "             DOTNET_PROCESSOR_COUNT=$DOTNET_PROCESSOR_COUNT GOMAXPROCS=$GOMAXPROCS"
echo "  shards   : $SHARDS (SocketSet legs only; kestrel has no shards)"
echo "  load     : -c $CONNECTIONS -d $DURATION (warmup $WARMUP), keep-alive only"
echo "  ktls     : $([[ $KTLS_CHECKABLE == 1 ]] && echo "verified against /proc/net/tls_stat" || echo "UNVERIFIABLE - tls module not loaded")"
echo "  csv      : $CSV"
echo

for ((rep=1; rep<=REPS; rep++)); do
  echo "--- pass $rep/$REPS ---"
  # Reshuffle every pass: a fixed order cannot separate a real difference from anything that accumulates.
  mapfile -t SHUFFLED < <(printf '%s\n' "${LEGS[@]}" | shuf)
  order=0
  for spec in "${SHUFFLED[@]}"; do
    IFS='|' read -r name args xt xtls xsh <<<"$spec"
    [[ -n "$FILTER" && "$name" != *$FILTER* ]] && continue
    order=$((order+1))
    run_leg "$name" "$args" "$xt" "$xtls" "$xsh" "$rep" "$order"
  done
done

echo
echo "=== keep-alive, median of $((REPS-WARMUP_PASSES)) scored passes (pass 1-$WARMUP_PASSES discarded) ==="
awk -F, -v warm="$WARMUP_PASSES" '
  NR>1 && $4!="" && $2>warm { n[$1]++; v[$1"_"n[$1]]=$4; p[$1"_"n[$1]]=$7; e[$1]+=$9 }
  END {
    printf "%-14s %10s %10s %10s %8s %10s %7s\n","leg","med_rps","min","max","spread%","med_p99us","errors"
    for (leg in n) {
      cnt=n[leg]; for(i=1;i<=cnt;i++){a[i]=v[leg"_"i]; b[i]=p[leg"_"i]}
      for(i=1;i<cnt;i++)for(j=i+1;j<=cnt;j++){if(a[i]>a[j]){t=a[i];a[i]=a[j];a[j]=t} if(b[i]>b[j]){t=b[i];b[i]=b[j];b[j]=t}}
      med=a[int((cnt+1)/2)]; medp=b[int((cnt+1)/2)]; LO[leg]=a[1]; HI[leg]=a[cnt]
      spread=(med>0)?100*(a[cnt]-a[1])/med:0
      printf "%-14s %10d %10d %10d %8.1f %10d %7d\n",leg,med,a[1],a[cnt],spread,medp,e[leg]
      if(med>best){best=med} if(worst==0||med<worst){worst=med} if(spread>ws){ws=spread}
    }
    range=(worst>0)?100*(best-worst)/worst:0
    printf "\n  between-leg range: %.1f%%   worst within-leg spread: %.1f%%\n",range,ws
    if (ws>=range) print "  VERDICT: within-leg spread rivals between-leg differences - this does NOT rank transports."
    else print "  VERDICT: between-leg differences exceed run-to-run spread - differences are likely real."

    # The verdict above is a crude GLOBAL heuristic: one leg far from the pack inflates the range and
    # makes everything look separable. This is the claim that actually holds - two legs differ only if
    # their observed min-max ranges do not overlap at all.
    print "\n  separable pairs (min-max ranges disjoint - the only orderings this data supports):"
    found=0
    for (x in LO) for (y in LO) if (x < y) {
      if (HI[x] < LO[y]) { printf "    %s < %s\n", x, y; found=1 }
      else if (HI[y] < LO[x]) { printf "    %s < %s\n", y, x; found=1 }
    }
    if (!found) print "    none - every leg overlaps every other; no ordering is supported"
  }' "$CSV"

echo
echo "csv : $CSV"
echo "logs: $LOGS"
echo "Caveats: loopback (client and server share the host - relative comparison only); keep-alive only,"
echo "so accept and TLS HANDSHAKE cost are out of scope; a container adds its own noise."
