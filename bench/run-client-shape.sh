#!/usr/bin/env bash
# The SE.REDIS CLIENT SHAPE: one connection (or two), deeply pipelined, scored on TAIL LATENCY.
#
# Why this rig exists: every other rig here is many-connections/server-accept, but SE.Redis client mode
# is ~1-2 multiplexed connections per endpoint with deep pipelining, and an APP BLOCKS on the call — so
# p99 is the product and throughput is secondary. Nothing else measures that regime, and it is the regime
# that decides whether SocketSet is viable as SE.Redis's IO core. The affine level-3 leg IS the prototype
# of that core (outbound SocketSet connection, framed inline on its loop, callback-granularity batching);
# this measures it as one.
#
# Legs: direct (the floor — what the backend+loopback cost alone), and the proxy as a stand-in for "a
# SocketSet hop in the path". A proxy is NOT a client library — extra hop, extra process — so read the
# proxy legs as "what does one SocketSet-based hop add to the client shape", not as SE.Redis numbers.
#
# Sweep -P (multiplex depth): 1 is the pathological floor; 16-64 is a busy multiplexer; 256 is a
# saturated one. -c 1 vs -c 2 mirrors SE.Redis's connection-per-endpoint choices.
#
# Usage:
#   bench/run-client-shape.sh
#   DEPTHS="1 64" CONNS="1" LEGS="direct socketset-l3" bench/run-client-shape.sh
set -uo pipefail

DEPTHS=${DEPTHS:-"1 16 64 256"}
CONNS=${CONNS:-"1 2"}
LEGS=${LEGS:-"direct socketset-l3"}
REPS=${REPS:-5}
REQUESTS=${REQUESTS:-2000000}       # scaled by depth below, same reasoning as run-proxy-ab.sh
DATASIZE=${DATASIZE:-32}
BACKEND_PORT=${BACKEND_PORT:-7379}
PROXY_PORT=${PROXY_PORT:-7380}
# SHARDS must not exceed the proxy's LOGICAL CPUs (6 here: quarter of the cores, both siblings), or loop
# threads share CPUs and their clients eat the queueing delay as tail. Measured 2026-08-02: 8 shards on 6
# logical CPUs nearly DOUBLED p99 (0.49 -> 0.87 ms) versus 6-on-6 -- an oversubscription artifact that
# was briefly mistaken for a product property.
SHARDS=${SHARDS:-6}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH="$REPO/bench/.tools/redis-benchmark"
PROXY="${PROXY_EXE:-/home/marc/code/StackExchange.Redis/toys/RESPite.Proxy/bin/Release/net10.0/RESPite.Proxy}"
GARNET="${GARNET_EXE:-$HOME/.dotnet/tools/garnet-server}"
for f in "$BENCH" "$PROXY" "$GARNET"; do [[ -x "$f" ]] || { echo "missing: $f"; exit 1; }; done

# Client-heavy split on purpose: with ONE connection the server side is nearly idle and the generator's
# scheduling jitter lands straight in the tail we are trying to measure.
read -r CLIENT_CPUS PROXY_CPUS SERVER_CPUS <<<"$(lscpu -p=CPU,CORE | grep -v '^#' | awk -F, '
  { cpu[NR]=$1; core[NR]=$2; if (!($2 in seen)) { seen[$2]=1; order[++n]=$2 } }
  END { c1=int(n/2); c2=int(n/4); if (c1<1) c1=1; if (c2<1) c2=1
    for (i=1;i<=n;i++) { c=order[i]; grp[c]=(i<=c1)?1:(i<=c1+c2?2:3) }
    for (i=1;i<=NR;i++) { g=grp[core[i]]; s[g]=(s[g]==""?cpu[i]:s[g] "," cpu[i]) }
    print s[1], s[2], s[3] }')"

STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$REPO/bench/results/client-shape-$STAMP"
mkdir -p "$OUT"
CSV="$OUT/results.csv"
echo "conns,depth,leg,rep,test,rps,p50_ms,p95_ms,p99_ms,max_ms,status" > "$CSV"

echo "client-shape: conns={$CONNS} depths={$DEPTHS} legs={$LEGS} reps=$REPS"
echo "  governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)"
echo "  client=$CLIENT_CPUS proxy=$PROXY_CPUS server=$SERVER_CPUS"
echo "  -> $OUT"

pkill -x garnet-server 2>/dev/null; sleep 1
taskset -c "$SERVER_CPUS" "$GARNET" --port "$BACKEND_PORT" --bind 127.0.0.1 >"$OUT/garnet.log" 2>&1 &
GARNET_PID=$!
PROXY_PID=""
cleanup() { [[ -n "$PROXY_PID" ]] && kill "$PROXY_PID" 2>/dev/null; kill "$GARNET_PID" 2>/dev/null; wait 2>/dev/null; }
trap cleanup EXIT INT TERM
for i in {1..40}; do timeout 5 "$BENCH" -h 127.0.0.1 -p "$BACKEND_PORT" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break; sleep 0.5; done

measure() { # $1=leg $2=conns $3=depth $4=rep
  local leg="$1" conns="$2" depth="$3" rep="$4" port="$BACKEND_PORT"
  if [[ "$leg" == "socketset-l3" ]]; then
    port="$PROXY_PORT"
    taskset -c "$PROXY_CPUS" "$PROXY" --transport socketset --backend io-uring --l2 --affinity \
        --shards "$SHARDS" --port "$PROXY_PORT" --upstream-port "$BACKEND_PORT" >"$OUT/proxy.r$rep.log" 2>&1 &
    PROXY_PID=$!
    local i; for ((i=0;i<60;i++)); do timeout 5 "$BENCH" -h 127.0.0.1 -p "$port" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break; sleep 0.5; done
    head -1 "$OUT/proxy.r$rep.log" | grep -q 'upstream=socketset-affine' || { echo "  BANNER MISMATCH"; echo "$conns,$depth,$leg,$rep,,,,,,,MISMATCH" >>"$CSV"; return; }
  fi
  # With -c 1 the offered load is latency-bound, so total requests scale with depth or the test is too
  # short to resolve (the 250 ms quantum again).
  local nreq=$(( REQUESTS * depth / 16 )); (( nreq < 200000 )) && nreq=200000
  local line
  line=$(taskset -c "$CLIENT_CPUS" "$BENCH" -h 127.0.0.1 -p "$port" -c "$conns" -n "$nreq" -d "$DATASIZE" \
         -t get -P "$depth" --threads 1 --csv 2>/dev/null | tail -1)
  local r p50 p95 p99 pmax
  r=$(cut -d, -f2 <<<"$line" | tr -d '"'); p50=$(cut -d, -f5 <<<"$line" | tr -d '"')
  p95=$(cut -d, -f6 <<<"$line" | tr -d '"'); p99=$(cut -d, -f7 <<<"$line" | tr -d '"'); pmax=$(cut -d, -f8 <<<"$line" | tr -d '"')
  if [[ -z "$r" ]]; then echo "$conns,$depth,$leg,$rep,GET,,,,,,NORESULT" >>"$CSV"
  else
    echo "$conns,$depth,$leg,$rep,GET,$r,$p50,$p95,$p99,$pmax,OK" >>"$CSV"
    printf '    c%-2s -P %-4s %-14s %10.0f ops/s  p50 %s p99 %s\n' "$conns" "$depth" "$leg" "$r" "$p50" "$p99"
  fi
  [[ -n "$PROXY_PID" ]] && { kill "$PROXY_PID" 2>/dev/null; wait "$PROXY_PID" 2>/dev/null; PROXY_PID=""; sleep 1; }
}

for conns in $CONNS; do
  for depth in $DEPTHS; do
    echo "=== -c $conns -P $depth ==="
    for ((rep=1; rep<=REPS; rep++)); do
      for leg in $(shuf -e $LEGS); do measure "$leg" "$conns" "$depth" "$rep"; done
    done
  done
done

echo; echo "=== GET: median (min-max) rps and p99 over $REPS passes ==="
awk -F, '
  NR>1 && $11=="OK" { k=$1 SUBSEP $2 SUBSEP $3; v[k][++n[k]]=$6+0; t[k][n[k]]=$9+0; cs[$1]=1; ds[$2]=1; ls[$3]=1 }
  function med(a,m,  i,j,q,tmp){for(i=1;i<=m;i++)tmp[i]=a[i];for(i=1;i<m;i++)for(j=i+1;j<=m;j++)if(tmp[j]<tmp[i]){q=tmp[i];tmp[i]=tmp[j];tmp[j]=q}
    lo=tmp[1];hi=tmp[m];return (m%2)?tmp[int(m/2)+1]:(tmp[m/2]+tmp[m/2+1])/2}
  END{ nc=asorti(cs,co,"@ind_num_asc"); ndp=asorti(ds,dd,"@ind_num_asc"); nl=asorti(ls,ll,"@ind_str_asc")
    for(x=1;x<=nc;x++) for(y=1;y<=ndp;y++){ printf "--- c%s -P %s ---\n", co[x], dd[y]
      for(z=1;z<=nl;z++){ k=co[x] SUBSEP dd[y] SUBSEP ll[z]; if(!(k in n))continue
        rm=med(v[k],n[k]); rlo=lo; rhi=hi; tm=med(t[k],n[k])
        printf "  %-14s %10.0f (%.0f-%.0f)  p99 %.3f (%.3f-%.3f)\n", ll[z], rm, rlo, rhi, tm, lo, hi } } }' "$CSV"
echo "csv: $CSV"
