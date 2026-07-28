#!/usr/bin/env bash
# Message-size sweep across the TLS transports - specifically, "at what payload size (if ever) does kTLS
# start to pay off?".
#
# WHY THIS EXISTS. A 512-byte request/response says almost nothing about TLS offload. AES-GCM with
# AES-NI runs at several GB/s, so 512 bytes is ~50-100ns of crypto against a syscall costing 1-2us: the
# I/O model dominates and the cipher is rounding error. kTLS is built for the opposite regime - bulk
# transfer, ideally sendfile() straight from page cache, ideally with a NIC that does the crypto inline.
# This sweeps payload size so the crossover (or its absence) is visible rather than assumed.
#
# READ BEFORE TRUSTING THE OUTPUT:
#  * On loopback there is no NIC, so inline TLS offload - kTLS's biggest win - cannot appear at all.
#    Expect kTLS to look unremarkable here no matter the size. Run it on real hardware, ideally two
#    machines, before concluding anything about kTLS itself.
#  * SocketSet's kTLS path currently drives receive as io_uring POLL + SSL_read, one syscall per message,
#    rather than the RECVMSG + TLS_GET_RECORD_TYPE cmsg design in TlsFilter's notes. So it forfeits
#    multishot receive and provided buffers. If kTLS trails at every size, that is the first suspect -
#    it is an integration property, not a kTLS property.
#  * Docker blocks io_uring via seccomp: run with --security-opt seccomp=unconfined, and trust the
#    /config check below rather than the flag you passed.
#
# Usage:  ./run-tls-sizes.sh
#         SIZES="4096 65536 1048576" REPS=5 ./run-tls-sizes.sh
#         FILTER=ktls ./run-tls-sizes.sh
set -uo pipefail

SIZES=${SIZES:-"512 4096 16384 65536 262144"}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
CONNECTIONS=${CONNECTIONS:-64}
# First pass is discarded as host warm-up, so this is scored passes + 1. Do not drop below 4: a single
# scored pass showed 3x swings on the large-payload legs, which is enough to invent a result from noise.
REPS=${REPS:-4}
PORT=${PORT:-5080}
FILTER=${FILTER:-}
# Shard count for the SocketSet legs (the Kestrel controls have no shards). A LIST expands one leg per
# value, as Run-TlsSizes.ps1 -Shards does; the default is a single value because this sweep already
# multiplies legs by payload size. The natural top of the range is the SERVER half's logical CPU count.
SHARDS=${SHARDS:-12}
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

mkdir -p "$OUT" "$(dirname "$BOMB")"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/tls-sizes-$STAMP.csv"
LOGS="$OUT/logs-tls-sizes-$STAMP"
mkdir -p "$LOGS"

for tool in jq curl taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done

if [[ ! -x "$BOMB" ]]; then
  echo "fetching bombardier..."
  curl -sSL -o "$BOMB" https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-linux-amd64
  chmod +x "$BOMB"
fi

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

# CPU split by physical core rather than by logical index - see bench/cpu-split.sh for why the obvious
# lower/upper-half split gives the server and the load generator the SAME cores on this host.
NCPU=$(nproc)
source "$REPO/bench/cpu-split.sh"

# name|demo args|expected transport|expected tls
#
# Both PLAINTEXT controls matter, and it is a mistake to include only one. Measured 2026-07-26, at a
# 256KB payload kestrel+tls reached 8383 MiB/s against plaintext io_uring's 4379 - a TLS leg beating a
# plaintext leg by ~2x, which is impossible unless the two are limited by different things. They are: the
# SocketSet legs go through the demo's Kestrel bridge (two Pipes plus a copy per write), and at large
# payloads that bridge, not the transport or the cipher, is what is being measured. Without a plaintext
# kestrel leg there is nothing to separate "TLS is cheap" from "our bridge is expensive".
#
# The trailing field is "shardable": SocketSet legs are expanded one leg per SHARDS entry and named
# e.g. "epoll+tls/s8"; the Kestrel controls have no shard concept and their /config omits the field.
BASE_LEGS=(
  "kestrel|--kestrel|kestrel-sockets|off|0"
  "epoll|--epoll|socketset/epoll|off|1"
  "iouring|--io-uring|socketset/io_uring|off|1"
  "iouring+tls|--io-uring --tls|socketset/io_uring|openssl|1"
  "iouring+ktls|--ktls|socketset/auto(iouring)|ktls (openssl + kernel offload)|1"
  "epoll+tls|--epoll --tls|socketset/epoll|openssl|1"
  "kestrel+tls|--kestrel --tls|kestrel-sockets|kestrel/sslstream|0"
)

source "$REPO/bench/ktls-verify.sh"
KTLS_CHECKABLE=1; ktls_available || KTLS_CHECKABLE=0

LEGS=()
for spec in "${BASE_LEGS[@]}"; do
  IFS='|' read -r _n _a _xt _xtls _sh <<<"$spec"
  if [[ "$_sh" == "1" ]]; then
    for s in $SHARDS; do
      if [[ $(wc -w <<<"$SHARDS") -gt 1 ]]; then LEGS+=("$_n/s$s|$_a --shards $s|$_xt|$_xtls|$s")
      else LEGS+=("$_n|$_a --shards $s|$_xt|$_xtls|$s"); fi
    done
  else
    LEGS+=("$_n|$_a|$_xt|$_xtls|")
  fi
done

echo "size,leg,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

measure() { # $1=name $2=args $3=xt $4=xtls $5=expect_shards $6=size $7=rep
  local name="$1" args="$2" xt="$3" xtls="$4" xsh="$5" size="$6" rep="$7"
  local scheme=http; [[ "$args" == *--tls* || "$args" == *--ktls* ]] && scheme=https
  local url="$scheme://127.0.0.1:$PORT/payload?n=$size"

  taskset -c "$SERVER_CPUS" "$DEMO" $args --port "$PORT" >"$LOGS/${name//\//-}.$size.r$rep.log" 2>&1 &
  local pid=$! cfg="" i
  for ((i=0;i<80;i++)); do
    cfg=$(curl -sk --max-time 3 "$scheme://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
    sleep 0.4
  done
  if [[ -z "$cfg" ]]; then
    echo "    $name: no /config"; echo "$size,$name,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Never measure a leg that silently loaded a different backend.
  if [[ "$cfg" != *"transport=$xt"* || "$cfg" != *"tls=$xtls"* ]]; then
    echo "    $name: MISMATCH -> $cfg"; echo "$size,$name,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  if [[ -n "$xsh" && "$cfg" != *"shards=$xsh "* ]]; then
    echo "    $name: SHARD MISMATCH (wanted shards=$xsh) -> $cfg"
    echo "$size,$name,$rep,,,,,,SHARD-MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  local ktls_before=""
  [[ "$args" == *--ktls* && $KTLS_CHECKABLE == 1 ]] && ktls_before=$(ktls_tx_count)

  taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$CONNECTIONS" -d "$WARMUP" -t 10s "$url" >/dev/null 2>&1

  # /config proves kTLS was CONFIGURED; only the kernel's own counter proves it ENGAGED. The warm-up has
  # opened real connections by now, so checking here costs a warm-up rather than a full scored pass.
  if [[ -n "$ktls_before" ]]; then
    local ktls_after; ktls_after=$(ktls_tx_count)
    if [[ -n "$ktls_after" && "$ktls_after" == "$ktls_before" ]]; then
      echo "    $name: KTLS-NOT-ENGAGED (TlsTxSw stayed at $ktls_before) -> measuring userspace OpenSSL"
      echo "$size,$name,$rep,,,,,,KTLS-NOT-ENGAGED" >>"$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
    fi
  fi
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$DURATION" -t 10s "$url" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  local rps; rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then echo "    $name: no result"; echo "$size,$name,$rep,,,,,,FAILED" >>"$CSV"; return; fi
  local p50 p99 errs mib
  p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  # Goodput matters more than request rate once payloads are large.
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  printf "    %-14s %9.0f rps  %8s MiB/s  p99 %7.0fus\n" "$name" "$rps" "$mib" "$p99"
  echo "$size,$name,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

echo
echo "TLS message-size sweep: ${#LEGS[@]} legs x $(wc -w <<<"$SIZES") sizes x $REPS passes (pass 1 discarded)"
echo "  cpus  : $NCPU  server=$SERVER_CPUS client=$CLIENT_CPUS (split by physical core)"
echo "          DOTNET_PROCESSOR_COUNT=$DOTNET_PROCESSOR_COUNT GOMAXPROCS=$GOMAXPROCS"
echo "  shards: $SHARDS (SocketSet legs only; kestrel has no shards)"
echo "  load  : -c $CONNECTIONS -d $DURATION, keep-alive, GET /payload?n=<size>"
echo "  ktls  : $([[ $KTLS_CHECKABLE == 1 ]] && echo "verified against /proc/net/tls_stat" || echo "UNVERIFIABLE - tls module not loaded")"
echo "  csv   : $CSV"
echo

for size in $SIZES; do
  echo "=== payload ${size} bytes ==="
  for ((rep=1; rep<=REPS; rep++)); do
    mapfile -t SHUF < <(printf '%s\n' "${LEGS[@]}" | shuf)
    for spec in "${SHUF[@]}"; do
      IFS='|' read -r name args xt xtls xsh <<<"$spec"
      [[ -n "$FILTER" && "$name" != *$FILTER* ]] && continue
      measure "$name" "$args" "$xt" "$xtls" "$xsh" "$size" "$rep"
    done
  done
done

echo
echo "=== goodput MiB/s, median of scored passes (pass 1 discarded) ==="
awk -F, 'NR>1 && $4!="" && $3>1 { k=$1"|"$2; n[k]++; v[k"_"n[k]]=$5 }
  END {
    for (k in n) { split(k,p,"|"); sizes[p[1]]=1; legs[p[2]]=1 }
    printf "%-10s","size"; for (l in legs) printf "%14s", l; printf "\n"
    m=asorti(sizes, ss, "@ind_num_asc")
    for (i=1;i<=m;i++) { s=ss[i]; printf "%-10s", s
      for (l in legs) { k=s"|"l; c=n[k]
        if (c==0) { printf "%14s","-"; continue }
        for(j=1;j<=c;j++) a[j]=v[k"_"j]+0
        for(x=1;x<c;x++)for(y=x+1;y<=c;y++) if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t}
        printf "%14.1f", a[int((c+1)/2)] }
      printf "\n" }
  }' "$CSV" 2>/dev/null || awk -F, 'NR>1 && $3>1 && $4!="" {printf "%-8s %-14s %10s MiB/s\n",$1,$2,$5}' "$CSV"

echo
echo "csv : $CSV"
echo "Reminder: loopback has no NIC, so kTLS inline offload cannot show up here at ANY size."
echo "A flat or trailing kTLS curve on this host is expected and is not evidence about kTLS on real hardware."
