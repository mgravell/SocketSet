#!/usr/bin/env bash
# RESP proxy transport A/B: the SAME proxy core (StackExchange.Redis toys/RESPite.Proxy) hosted on the
# hand-rolled WorkerPool/SAEA layer vs on SocketSet (io_uring / epoll), against a real backend.
#
# WHY THIS RIG AND NOT THE ASP.NET ONES. Every throughput number in AspNetDemo/RESULTS.md is scored
# through Kestrel, whose bridge costs 24-40% at 256 KB -- and whose "control" leg is a different
# APPLICATION path, not just a different transport, so bridge cost and transport cost stay fused. Here the
# application is held constant and the transport is the only variable. That is the comparison the ASP.NET
# rigs structurally cannot make.
#
# THE CONTROL YOU MUST READ FIRST. `direct` is the load generator talking straight to the backend, with
# NO proxy. It is a CEILING REFERENCE, NOT A PEER: it runs one fewer process and one fewer network hop, so
# do not subtract it from a proxy leg as though they were the same shape. Its job is to prove the BACKEND
# HAS HEADROOM. If a proxy leg approaches it, the backend is the bottleneck and every proxy comparison in
# that column is measuring the backend -- the "uniformity across cells that should differ is a harness
# failure" pattern from bench/README.md. Measured 2026-08-02: garnet-server 2.1.1 direct does ~400-580k
# ops/s at -P 1 and ~1.2M at -P 16, well clear of what any proxy leg here reaches.
#
# PIPELINE DEPTH IS A REGIME, NOT A PARAMETER, so the two are reported separately and never blended:
#   -P 1   syscall/latency bound -- one round trip per command. This is where per-request machinery
#          (thread hops, wakeups, accept/dispatch cost) dominates, and where io_uring batching should pay.
#   -P 16  parse/throughput bound -- many commands per read. This is where framing efficiency and copies
#          dominate, and where the transport matters much less. A win at one depth can be a loss at the
#          other; that would be a finding, not noise.
#
# CPU SPLIT: three processes now, not two. client / proxy / backend each get their own PHYSICAL cores
# (with both SMT siblings), because on this host CPUs 0-11 are one thread of each core and 12-23 are the
# siblings -- see bench/cpu-split.sh for the full story of why the obvious split is wrong here.
#
# Usage:
#   bench/run-proxy-ab.sh
#   DEPTHS="1" LEGS="direct worker socketset-iouring" REPS=5 bench/run-proxy-ab.sh
set -uo pipefail

REPS=${REPS:-6}                       # scored passes; pass 1 is NOT discarded here (each leg restarts
                                      # its own process and redis-benchmark does its own ramp), but the
                                      # leg ORDER is reshuffled every pass, which is what matters
DEPTHS=${DEPTHS:-"1 16"}
LEGS=${LEGS:-"direct envoy worker socketset-iouring socketset-epoll socketset-l2"}
# Requests per test, SCALED BY PIPELINE DEPTH below. This must be large enough that each test runs for
# ~10s: at -n 50000 and ~200k ops/s a test lasts 0.25s and redis-benchmark reports QUANTISED rps -- the
# shakeout produced exactly 200000, 100000 and 99800 ops/s, and the same leg read 200k on GET and 99.8k on
# SET. Those are timer granularity, not measurements, and they look entirely plausible in a table.
REQUESTS=${REQUESTS:-1500000}
CLIENTS=${CLIENTS:-64}
DATASIZE=${DATASIZE:-32}
TESTS=${TESTS:-get,set}
BACKEND_PORT=${BACKEND_PORT:-7379}
PROXY_PORT=${PROXY_PORT:-7380}
ENVOY_PORT=${ENVOY_PORT:-7381}      # Envoy listens on its own port; its config is bench/envoy-redis.yaml
ENVOY_ADMIN=${ENVOY_ADMIN:-9901}
SHARDS=${SHARDS:-8}
# Upstream connections ("legs"). NOT a neutral default: clients are round-robined onto these sticky legs,
# and FEWER legs means MORE client commands coalesce into each upstream write. Measured 2026-08-02, level 2
# unpinned: 1->333k, 2->375k, 3->353k, 5->316k, 16->207k, 32->167k, 64->143k. Monotonic above 2 and a clear
# optimum AT 2 -- so the shipped default of 5 is leaving ~19% on the table, and the intuition that more
# upstream connections means more parallelism is exactly backwards here.
UPSTREAM_CONNS=${UPSTREAM_CONNS:-5}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH="$REPO/bench/.tools/redis-benchmark"
PROXY="${PROXY_EXE:-/home/marc/code/StackExchange.Redis/toys/RESPite.Proxy/bin/Release/net10.0/RESPite.Proxy}"
GARNET="${GARNET_EXE:-$HOME/.dotnet/tools/garnet-server}"
ENVOY="${ENVOY_EXE:-$REPO/bench/.tools/envoy}"
ENVOY_CFG="${ENVOY_CFG:-$REPO/bench/envoy-redis.yaml}"

for f in "$BENCH" "$PROXY" "$GARNET"; do
  [[ -x "$f" ]] || { echo "missing/not executable: $f"; exit 1; }
done
command -v taskset >/dev/null || { echo "missing taskset"; exit 1; }

# Three-way split by physical core. lscpu -p gives CPU,CORE; take the cores in thirds and hand each third
# every logical CPU belonging to it.
read -r CLIENT_CPUS PROXY_CPUS SERVER_CPUS <<<"$(lscpu -p=CPU,CORE | grep -v '^#' | awk -F, '
  { cpu[NR]=$1; core[NR]=$2; if (!($2 in seen)) { seen[$2]=1; order[++n]=$2 } }
  END {
    third = int(n/3); if (third < 1) third = 1
    for (i=1;i<=n;i++) { c=order[i]; grp[c] = (i<=third) ? 1 : (i<=2*third ? 2 : 3) }
    for (i=1;i<=NR;i++) { g=grp[core[i]]; s[g] = (s[g]=="" ? cpu[i] : s[g] "," cpu[i]) }
    print s[1], s[2], s[3]
  }')"
[[ -n "$SERVER_CPUS" ]] || { echo "CPU split failed"; exit 1; }

STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$REPO/bench/results/proxy-ab-$STAMP"
mkdir -p "$OUT"
CSV="$OUT/results.csv"
echo "depth,leg,rep,test,rps,p50_ms,status" > "$CSV"

echo "proxy transport A/B -- $(wc -w <<<"$LEGS") legs x $(wc -w <<<"$DEPTHS") depths x $REPS passes"
echo "  governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)/$(cat /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference 2>/dev/null)"
echo "  client=$CLIENT_CPUS"
echo "  proxy =$PROXY_CPUS"
echo "  server=$SERVER_CPUS"
echo "  -c $CLIENTS -n $REQUESTS -d $DATASIZE -t $TESTS shards=$SHARDS"
echo "  -> $OUT"
echo

# --- backend, started once and shared by every leg (so it is a constant, not a variable) --------------
# -x matches the process NAME, not the full command line. `pkill -f garnet-server` also matches any SHELL
# whose command line happens to contain that string -- including the one invoking this script, which kills
# the run with SIGTERM and looks like a rig crash. Cost two runs before it was obvious.
pkill -x garnet-server 2>/dev/null; sleep 1
taskset -c "$SERVER_CPUS" "$GARNET" --port "$BACKEND_PORT" --bind 127.0.0.1 >"$OUT/garnet.log" 2>&1 &
GARNET_PID=$!
cleanup() {
  [[ -n "${PROXY_PID:-}" ]] && kill "$PROXY_PID" 2>/dev/null
  kill "$GARNET_PID" 2>/dev/null
  wait 2>/dev/null
}
trap cleanup EXIT INT TERM
for i in {1..40}; do
  timeout 5 "$BENCH" -h 127.0.0.1 -p "$BACKEND_PORT" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break
  sleep 0.5
done

start_proxy() { # $1=leg -> sets PROXY_PID, echoes the banner; empty return means "no proxy" (direct)
  local leg="$1" args=""
  case "$leg" in
    direct) PROXY_PID=""; return 0 ;;
    envoy)
      # Give Envoy the SAME core budget as our proxy, or this is a core-count comparison wearing a
      # throughput costume. --concurrency is worker threads; count the logical CPUs in PROXY_CPUS.
      local nworkers; nworkers=$(tr ',' '\n' <<<"$PROXY_CPUS" | grep -c .)
      taskset -c "$PROXY_CPUS" "$ENVOY" -c "$ENVOY_CFG" --concurrency "$nworkers" --log-level warn \
          >"$OUT/proxy-envoy.log" 2>&1 &
      PROXY_PID=$!
      local j
      for ((j=0;j<60;j++)); do
        timeout 5 "$BENCH" -h 127.0.0.1 -p "$ENVOY_PORT" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break
        sleep 0.5
      done
      # Envoy prints no banner, so gate on its admin endpoint instead -- the equivalent evidence that it
      # is really serving rather than merely spawned.
      local state
      state=$(timeout 5 curl -s "http://127.0.0.1:$ENVOY_ADMIN/server_info" 2>/dev/null | grep -o '"state": *"[A-Z]*"' | head -1)
      [[ "$state" == *LIVE* ]] || { echo "    ENVOY NOT LIVE: ${state:-(no admin response)}"; return 1; }
      return 0 ;;
    worker)             args="--transport worker" ;;
    socketset-iouring)  args="--transport socketset --backend io-uring --shards $SHARDS" ;;
    socketset-epoll)    args="--transport socketset --backend epoll --shards $SHARDS" ;;
    # LEVEL 2: RESP framing on the loop thread -- no pipes, no pump, no ThreadPool hop.
    socketset-l2)       args="--transport socketset --backend io-uring --l2 --shards $SHARDS" ;;
    socketset-l2-u2)    args="--transport socketset --backend io-uring --l2 --shards $SHARDS --upstream-connections 2" ;;
    socketset-l2-epoll) args="--transport socketset --backend epoll --l2 --shards $SHARDS" ;;
    *) echo "unknown leg '$leg'"; return 1 ;;
  esac
  taskset -c "$PROXY_CPUS" "$PROXY" $args --port "$PROXY_PORT" --upstream-port "$BACKEND_PORT" \
      >"$OUT/proxy-$leg.log" 2>&1 &
  PROXY_PID=$!
  # Wait for it to actually serve, then TRUST THE BANNER: a leg whose transport silently fell back would
  # measure as a perfectly plausible result for the wrong thing.
  local i
  for ((i=0;i<60;i++)); do
    timeout 5 "$BENCH" -h 127.0.0.1 -p "$PROXY_PORT" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break
    sleep 0.5
  done
  local banner
  banner=$(head -1 "$OUT/proxy-$leg.log")
  case "$leg" in
    worker)            [[ "$banner" == *"transport=worker-saea"* ]] || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
    socketset-iouring) [[ "$banner" == *"transport=socketset/io-uring"* ]] || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
    socketset-epoll)   [[ "$banner" == *"transport=socketset/epoll"* ]] || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
    # Gate on bridge=direct too: a level-2 leg that silently ran the pipe bridge would measure as a
    # perfectly plausible level-1 result and the whole experiment would report nothing.
    socketset-l2)      [[ "$banner" == *"transport=socketset/io-uring"* && "$banner" == *"bridge=direct"* ]] \
                         || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
    socketset-l2-u2)   [[ "$banner" == *"bridge=direct"* && "$banner" == *"legs=2"* ]] \
                         || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
    socketset-l2-epoll) [[ "$banner" == *"transport=socketset/epoll"* && "$banner" == *"bridge=direct"* ]] \
                         || { echo "    BANNER MISMATCH: $banner"; return 1; } ;;
  esac
  return 0
}

measure() { # $1=leg $2=depth $3=rep
  local leg="$1" depth="$2" rep="$3" port="$PROXY_PORT"
  [[ "$leg" == "direct" ]] && port="$BACKEND_PORT"
  [[ "$leg" == "envoy" ]] && port="$ENVOY_PORT"
  if ! start_proxy "$leg"; then
    echo "$depth,$leg,$rep,,,,STARTFAIL" >>"$CSV"; return
  fi
  local csv
  # Pipelining multiplies achievable rate roughly with depth, so scale the request count with it or the
  # -P 16 legs finish in a fraction of the time and are measured at a coarser effective resolution.
  local nreq=$(( REQUESTS * depth ))
  csv=$(taskset -c "$CLIENT_CPUS" "$BENCH" -h 127.0.0.1 -p "$port" -c "$CLIENTS" -n "$nreq" \
        -d "$DATASIZE" -t "$TESTS" -P "$depth" --threads 6 --csv 2>/dev/null)
  # redis-benchmark --csv: "test","rps","avg","min","p50","p95","p99","max"
  while IFS= read -r line; do
    [[ "$line" == '"test"'* ]] && continue
    [[ -z "$line" ]] && continue
    local t r p50
    t=$(cut -d, -f1 <<<"$line" | tr -d '"')
    r=$(cut -d, -f2 <<<"$line" | tr -d '"')
    p50=$(cut -d, -f5 <<<"$line" | tr -d '"')
    [[ -z "$r" ]] && continue
    echo "$depth,$leg,$rep,$t,$r,$p50,OK" >>"$CSV"
    printf '    %-20s %-4s %12.0f ops/s  p50 %sms\n' "$leg" "$t" "$r" "$p50"
  done <<<"$csv"
  [[ -n "${PROXY_PID:-}" ]] && { kill "$PROXY_PID" 2>/dev/null; wait "$PROXY_PID" 2>/dev/null; }
  PROXY_PID=""
  sleep 2
}

for depth in $DEPTHS; do
  echo "=== pipeline depth -P $depth ==="
  for ((rep=1; rep<=REPS; rep++)); do
    echo "  pass $rep/$REPS"
    # Reshuffle leg order every pass: a fixed order lets host drift land on the same leg every time and
    # read as a real difference.
    for leg in $(shuf -e $LEGS); do
      measure "$leg" "$depth" "$rep"
    done
  done
done

echo
echo "=== ops/s, $REPS passes, min-max ranges ==="
awk -F, '
  NR>1 && $7=="OK" { k=$1 SUBSEP $2 SUBSEP $4; v[k][++n[k]]=$5+0; d[$1]=1; legs[$2]=1; tests[$4]=1
                     if (!((k SUBSEP $5) in seenval)) { seenval[k SUBSEP $5]=1; distinct[k]++ } }
  function stats(a,c,  i,j,t,tmp){for(i=1;i<=c;i++)tmp[i]=a[i];for(i=1;i<c;i++)for(j=i+1;j<=c;j++)if(tmp[j]<tmp[i]){t=tmp[i];tmp[i]=tmp[j];tmp[j]=t}lo=tmp[1];hi=tmp[c];med=(c%2)?tmp[int(c/2)+1]:(tmp[c/2]+tmp[c/2+1])/2}
  END {
    nd=asorti(d,do_,"@ind_num_asc"); nl=asorti(legs,lo_,"@ind_str_asc"); nt=asorti(tests,to_,"@ind_str_asc")
    for (a=1;a<=nd;a++) { dd=do_[a]
      for (b=1;b<=nt;b++) { tt=to_[b]
        printf "--- -P %s  %s ---\n", dd, tt
        # direct is a CEILING REFERENCE, not a peer: print it first and label it. envoy IS a peer -- it is
        # the external bar that makes this comparison mean something outside this repo.
        kk=dd SUBSEP "direct" SUBSEP tt
        if (kk in n) { stats(v[kk],n[kk]); printf "  %-20s %12.0f  (%.0f-%.0f)   <- CEILING (no proxy; one fewer hop, NOT a peer)\n", "direct", med, lo, hi
                       cmed=med; clo=lo; chi=hi } else cmed=0
        # worker is the peer baseline the SocketSet legs are actually judged against.
        wk=dd SUBSEP "worker" SUBSEP tt
        if (wk in n) { stats(v[wk],n[wk]); wmed=med; wlo=lo; whi=hi } else wmed=0
        for (c=1;c<=nl;c++) { ll=lo_[c]; if (ll=="direct") continue
          k=dd SUBSEP ll SUBSEP tt; if (!(k in n)) continue
          stats(v[k],n[k])
          rel = (wmed>0) ? sprintf("%+6.1f%% vs worker", (med-wmed)/wmed*100) : ""
          vs  = (ll=="worker") ? "(peer baseline)" \
              : (wmed<=0) ? "" \
              : (lo > whi) ? "DISJOINT -- ahead of worker" \
              : (wlo > hi) ? "DISJOINT -- behind worker" : "overlapping -- UNPROVEN"
          hdr = (cmed>0) ? sprintf(" [%.0f%% of ceiling]", med/cmed*100) : ""
          printf "  %-20s %12.0f  (%.0f-%.0f) %s %s%s\n", ll, med, lo, hi, rel, vs, hdr
        }
        printf "\n"
      }
    }
    # QUANTISATION AUDIT. redis-benchmark resolves elapsed time to ~250 ms, so a short test yields only a
    # handful of possible rps values and every pass snaps to one of them. The min-max range then looks
    # TIGHT -- which reads as reproducibility and is really the opposite: the rig cannot see any variation
    # smaller than one tick. A DISJOINT verdict computed from such a range is not evidence. Observed
    # 2026-08-02: 6 passes producing 2 distinct values, with implied elapsed times landing exactly on
    # 3.000 / 6.250 / 6.500 / 7.250 / 7.500 s.
    printf "=== quantisation audit (distinct values per cell, of the passes scored) ===\n"
    bad = 0
    for (k in distinct) {
      split(k, parts, SUBSEP)
      # n < 3 can never support a range claim at all -- and the naive ratio test PASSES a single-pass cell
      # (1 distinct > 0.5 of 1), reporting "clean" for a cell that resolved nothing. Caught on the Envoy
      # shakeout for this very rig. NOTE: no apostrophes in this awk block -- it is single-quoted in shell,
      # so one silently truncates the program.
      if (n[k] < 3 || distinct[k] * 2 <= n[k]) {
        printf "  WARNING -P %-3s %-18s %-4s  %d distinct value(s) of %d passes -- range is\n", \
               parts[1], parts[2], parts[3], distinct[k], n[k]
        printf "           TIMER-QUANTISED, not measured. Do not quote its range or any DISJOINT verdict\n"
        printf "           derived from it; raise -n until each test runs long enough to resolve.\n"
        bad++
      }
    }
    if (bad == 0) printf "  clean: every cell resolved more than half its passes as distinct values\n"
    printf "\n"
  }' "$CSV"

echo "csv: $CSV"
echo
echo "Quote a delta only where min-max ranges are DISJOINT. 'direct' is a ceiling, not a peer -- if a proxy"
echo "leg approaches it, the BACKEND is the bottleneck and that column compares nothing."
