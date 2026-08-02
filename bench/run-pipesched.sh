#!/usr/bin/env bash
# Interleaved A/B of the bridge's pipe SCHEDULERS (SS_PIPE_SCHED), same binary every leg.
#
# The Linux counterpart of Run-PipeSched.ps1, ported 2026-08-02. It exists because the Linux answer on
# this question is stale in a way that READS AS SETTLED: SS_PIPE_SCHED=inline was measured here at −28%
# on io_uring, and that number is quoted as though the thread-hop question were closed. It is not. That
# knob only ever moved the OUTBOUND reader (the SocketSet pump). The INBOUND reader — the one that resumes
# KESTREL'S REQUEST PIPELINE when data arrives — was hard-wired to ThreadPool until a264998, so every
# "thread hop" number on file for Linux is about the WRITE side and the read side is untouched ground.
#
# On Windows this rig found that the ~3% small-payload deficit to vanilla Kestrel IS the read-side hop,
# essentially in full — the first mechanism ever found for it, after copies, pool pinning and segment
# counts were each measured and each failed to explain it. This asks the same question of io_uring/epoll.
#
# WHY IT IS AN EXPERIMENT AND NOT A PROPOSED DEFAULT: an inline INBOUND reader runs Kestrel's whole
# request pipeline on the transport's loop thread, blocking that loop for every backend that owns one
# (all but managed). Kestrel runs its own IO queues for precisely this reason. So a win here is NOT
# shippable as-is. Its value is that it UPPER-BOUNDS what removing the read hop could ever be worth, which
# is what decides whether an inbound half-pipe (a real fix — the loop drains on its own timeline, no
# pipeline on the loop thread) is worth building. A NULL RESULT DEPRIORITISES THAT WORK OUTRIGHT, which is
# just as useful an answer and is why this is worth running either way.
#
# PRE-REGISTERED EXPECTATION (write it down before running it, not after):
#   The read hop is a per-REQUEST cost, not a per-byte one — one resumption per request whatever the body
#   size. So the gain must be LARGEST AT SMALL PAYLOADS (highest request rate = highest hop rate) and must
#   FADE TOWARD NOTHING at 256 KB. IF THE GAIN GROWS WITH PAYLOAD, THE MECHANISM IS NOT THE HOP and this
#   rig has found something else. Small payloads are also exactly where vanilla Kestrel still beats us on
#   Linux (512 B −3-5%, 16 KB −2-6% disjoint), which is why they are the default sizes.
#
# THE KESTREL CONTROL RUNS IN THE SAME PASSES, and that is the point of including it: the question is not
# "is inline-read faster than off" but "does removing the read hop close the gap to Kestrel". Reading our
# number against a Kestrel number from another run is precisely the cross-session subtraction that
# RESULTS.md forbids and that has produced confident nonsense here before.
#
# Usage:
#   bench/run-pipesched.sh
#   BACKEND=epoll SIZES="512 16384" MODES="off inline-read kestrel" REPS=7 bench/run-pipesched.sh
set -uo pipefail

SIZES=${SIZES:-"512 4096 16384 262144"}
MODES=${MODES:-"off inline-read inline inline-both kestrel"}
BACKEND=${BACKEND:-io-uring}          # io-uring | epoll | managed
DEMO_ARGS=${DEMO_ARGS:---byo}         # the bridge mode under test; the inbound pipe exists in all of them
SHARDS=${SHARDS:-12}
CONNECTIONS=${CONNECTIONS:-64}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-3s}
REPS=${REPS:-7}                       # first pass discarded => scored = REPS-1
# Below the ephemeral range (32768-60999 here) and varying per run: a fixed base inside that range lets
# the load generator hold the port the next server needs. See compare-commits.sh for the full story.
PORT=${PORT:-$(( 20000 + (RANDOM % 8000) ))}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BOMB="$REPO/bench/.tools/bombardier"
DEMO="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

case "$BACKEND" in
  io-uring) XPORT="socketset/io_uring" ;;
  epoll)    XPORT="socketset/epoll" ;;
  managed)  XPORT="socketset/managed" ;;
  *) echo "BACKEND must be io-uring | epoll | managed (got '$BACKEND')"; exit 1 ;;
esac

for tool in jq curl taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
mkdir -p "$(dirname "$BOMB")"
if [[ ! -x "$BOMB" ]]; then
  echo "fetching bombardier..."
  curl -sSL -o "$BOMB" https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-linux-amd64
  chmod +x "$BOMB"
fi

echo "building AspNetDemo (Release)..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

source "$REPO/bench/cpu-split.sh"

STAMP=$(date +%Y%m%d-%H%M%S)
LOGS="$REPO/bench/results/pipesched-$STAMP"
mkdir -p "$LOGS"
CSV="$LOGS/results.csv"
echo "size,mode,rep,rps,mib_s,p99_us,status" > "$CSV"

measure_one() { # $1=mode $2=size $3=rep
  local mode="$1" size="$2" rep="$3"
  local log="$LOGS/$mode.$size.r$rep" args pid cfg="" i is_control=0
  [[ "$mode" == "kestrel" ]] && is_control=1

  if (( is_control )); then
    args="--kestrel --port $PORT"
  else
    args="--$BACKEND $DEMO_ARGS --shards $SHARDS --port $PORT"
  fi

  # Set SS_PIPE_SCHED for the CHILD ONLY. A value left in this shell would silently apply to every later
  # leg, which turns an A/B into two copies of the same thing and reports the difference as noise.
  if [[ "$mode" == "off" || $is_control == 1 ]]; then
    taskset -c "$SERVER_CPUS" env -u SS_PIPE_SCHED "$DEMO" $args >"$log.log" 2>&1 &
  else
    taskset -c "$SERVER_CPUS" env SS_PIPE_SCHED="$mode" "$DEMO" $args >"$log.log" 2>&1 &
  fi
  pid=$!

  for ((i=0;i<100;i++)); do
    cfg=$(curl -s --max-time 3 "http://127.0.0.1:$PORT/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
    sleep 0.4
  done
  if [[ -z "$cfg" ]]; then
    echo "    $mode/$size r$rep: no /config"
    echo "$size,$mode,$rep,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return 1
  fi

  # TRUST THE BANNER, NOT THE FLAG. An env var not inherited by the child, or mistyped, makes two legs
  # identical and the difference gets reported as noise rather than as a broken experiment.
  local conf geo
  conf=$(jq -r '.config // ""' <<<"$cfg")
  geo=$(jq -r '.geometry // ""' <<<"$cfg")
  if (( is_control )); then
    # The control must be REAL vanilla Kestrel. Our transport publishes a geometry and Kestrel's does not,
    # so transport-string AND empty-geometry together are the pair a mis-parsed flag cannot fake.
    if [[ "$conf" != *"transport=kestrel-sockets"* ]]; then
      echo "    kestrel/$size r$rep: not the vanilla leg -> $conf"
      echo "$size,$mode,$rep,,,,MISMATCH" >>"$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return 1
    fi
  else
    [[ "$conf" == *"transport=$XPORT"* ]] || {
      echo "    $mode/$size r$rep: wrong transport -> $conf"
      echo "$size,$mode,$rep,,,,MISMATCH" >>"$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return 1; }
    if [[ "$mode" == "off" ]]; then
      # `off` must show NO scheduler. Without this check a leftover env var would make `off` secretly be
      # `inline-both`, and the whole table would collapse toward "no effect" for the right-looking reason.
      [[ "$geo" != *"pipesched="* ]] || {
        echo "    off/$size r$rep: banner HAS a scheduler set -> $geo"
        echo "$size,$mode,$rep,,,,MISMATCH" >>"$CSV"
        kill $pid 2>/dev/null; wait $pid 2>/dev/null; return 1; }
    else
      [[ "$geo" == *"pipesched=$mode"* ]] || {
        echo "    $mode/$size r$rep: banner missing 'pipesched=$mode' -> $geo"
        echo "$size,$mode,$rep,,,,MISMATCH" >>"$CSV"
        kill $pid 2>/dev/null; wait $pid 2>/dev/null; return 1; }
    fi
  fi

  local phase
  for phase in "$WARMUP" "$DURATION"; do
    taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$phase" -t 15s \
      "http://127.0.0.1:$PORT/payload?n=$size" >"$log.json" 2>/dev/null
  done

  local rps p99 errs ok2xx mib
  rps=$(jq -r '.result.rps.mean // empty' "$log.json" 2>/dev/null)
  p99=$(jq -r '.result.latency.percentiles."99" // 0' "$log.json" 2>/dev/null)
  errs=$(jq -r '[(.result.req4xx//0),(.result.req5xx//0),(.result.others//0)] | add' "$log.json" 2>/dev/null)
  ok2xx=$(jq -r '.result.req2xx // 0' "$log.json" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null
  sleep 1

  if [[ -z "$rps" ]]; then
    echo "    $mode/$size r$rep: no result"; echo "$size,$mode,$rep,,,,NORESULT" >>"$CSV"; return 1
  fi
  # REFUSE a leg that errored. bombardier has no `errors` property — failures land in req4xx/req5xx/others
  # — so a rig reading `.errors` prints an empty column that is indistinguishable from "zero errors" at a
  # glance. Run-PipeSched.ps1 did exactly that on its first run and checked nothing for 156 legs.
  if [[ "$errs" != "0" || "$ok2xx" -le 0 ]]; then
    echo "    $mode/$size r$rep: ERRORS bad=$errs 2xx=$ok2xx"
    echo "$size,$mode,$rep,,,,ERRORS" >>"$CSV"; return 1
  fi
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  echo "$size,$mode,$rep,$rps,$mib,$p99,OK" >>"$CSV"
  printf '    %-12s %10.0f rps %10s MiB/s  p99 %8.0fus\n' "$mode" "$rps" "$mib" "$p99"
}

echo
echo "pipe-scheduler A/B: $(wc -w <<<"$MODES") modes x $(wc -w <<<"$SIZES") sizes x $REPS passes (first discarded)"
echo "  backend=$BACKEND $DEMO_ARGS shards=$SHARDS -c $CONNECTIONS -d $DURATION port=$PORT"
echo "  governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)/$(cat /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference 2>/dev/null)"
echo "  server=$SERVER_CPUS client=$CLIENT_CPUS"
echo "  -> $LOGS"
echo

for size in $SIZES; do
  echo "=== payload $size ==="
  for ((rep=1; rep<=REPS; rep++)); do
    # Reshuffle the mode order EVERY pass: a fixed order lets slow host drift land on the same leg every
    # time and read as a real difference.
    for mode in $(shuf -e $MODES); do
      measure_one "$mode" "$size" "$rep"
    done
  done
done

echo
echo "=== goodput MiB/s, $((REPS-1)) scored passes (pass 1 discarded) ==="
awk -F, '
  NR>1 && $7=="OK" && $3>1 { k=$1 SUBSEP $2; v[k][++n[k]]=$5+0; sizes[$1]=1; modes[$2]=1 }
  function stats(arr,cnt,  i,j,t,tmp) {
    for (i=1;i<=cnt;i++) tmp[i]=arr[i]
    for (i=1;i<cnt;i++) for (j=i+1;j<=cnt;j++) if (tmp[j]<tmp[i]) { t=tmp[i]; tmp[i]=tmp[j]; tmp[j]=t }
    lo=tmp[1]; hi=tmp[cnt]; med=(cnt%2)?tmp[int(cnt/2)+1]:(tmp[cnt/2]+tmp[cnt/2+1])/2
  }
  END {
    ns=asorti(sizes, so, "@ind_num_asc"); nm=asorti(modes, mo, "@ind_str_asc")
    for (a=1;a<=ns;a++) {
      s=so[a]
      # The Kestrel control is the reference column: this rig exists to answer "does removing the read hop
      # close the gap to KESTREL", not "is inline-read faster than off".
      kk=s SUBSEP "kestrel"; haveK=(kk in n)
      if (haveK) { stats(v[kk], n[kk]); kmed=med; klo=lo; khi=hi }
      printf "--- %s ---%s\n", s, haveK ? sprintf("   kestrel control: %.1f (%.1f-%.1f)", kmed, klo, khi) : "   (no kestrel control)"
      printf "  %-13s %10s %18s %10s  %s\n", "mode", "median", "min-max", "vs kestrel", "verdict"
      for (b=1;b<=nm;b++) {
        m=mo[b]; if (m=="kestrel") continue
        k=s SUBSEP m; if (!(k in n)) continue
        stats(v[k], n[k])
        if (!haveK) { printf "  %-13s %10.1f %18s\n", m, med, sprintf("(%.1f-%.1f)", lo, hi); continue }
        d=(kmed>0)?(med-kmed)/kmed*100:0
        verdict = (n[k]<3 || n[kk]<3) ? sprintf("n=%d/%d -- TOO FEW PASSES", n[k], n[kk]) \
                : (lo > khi) ? "DISJOINT -- ahead of Kestrel" \
                : (klo > hi) ? "DISJOINT -- behind Kestrel" : "overlapping -- parity"
        printf "  %-13s %10.1f %18s %9.1f%%  %s\n", m, med, sprintf("(%.1f-%.1f)", lo, hi), d, verdict
      }
      printf "\n"
    }
  }' "$CSV"

echo "csv: $CSV"
echo
echo "Quote a delta only where the min-max ranges are DISJOINT. An inline INBOUND reader runs Kestrel on"
echo "the transport loop thread, so a win here is an UPPER BOUND on removing the read hop, NOT a shippable"
echo "default -- see the header. And check the SHAPE: a gain that grows with payload falsifies the hop."
