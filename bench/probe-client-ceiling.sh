#!/usr/bin/env bash
# Is the proxy rig's -P 1 ceiling per-PROCESS or per-CORE?
#
# WHY THIS EXISTS. run-proxy-ab.sh cannot separate us from Envoy at -P 1: both legs sit at 90% of a
# CLIENT-limited ceiling (451,613 vs 451,584 against a direct ceiling of 499,929 -- the same number
# twice). "Parity" is therefore a FLOOR, not a result. This probe does not measure a proxy at all; it
# measures the GENERATOR, using `direct` (redis-benchmark straight to garnet-server) as the instrument.
# Pre-registration, including what would falsify each prediction, is in
# bench/prereg-client-ceiling-2026-09-11.md -- read it before reading any number out of here.
#
# THE ANSWER DECIDES THE FIX, and the two fixes cost very differently:
#   per-process -> sum N processes on the SAME client cores. Free; the three-way split is untouched and
#                  every existing number stays comparable.
#   per-core    -> the client third has to grow, so the proxy or backend third shrinks. That is a
#                  different machine under test, not a purer measurement.
#
# READ THE SUM COLUMN CAREFULLY. Multi-process legs SUM per-process rps, which is only honest if the
# processes actually overlap in time. Every process is given the SAME -n so they finish together, and the
# rig reports the per-process WALL SPREAD; if that spread is a material fraction of the run, the sum is
# overstating an instantaneous rate and the cell says so rather than being quoted.
#
# Usage:
#   bench/probe-client-ceiling.sh
#   REPS=3 LEGS="1x64 4x16" bench/probe-client-ceiling.sh
set -uo pipefail

REPS=${REPS:-6}
LEGS=${LEGS:-"1x64 1x64-t8 2x32 4x16 4x64"}
# The leg every other leg is compared against. Must be present in LEGS, or the summary refuses to print
# rather than print a table with no baseline -- which is how a run gets read as "all legs equal".
CTL=${CTL:-1x64}
DEPTH=${DEPTH:-1}
DATASIZE=${DATASIZE:-32}
TESTS=${TESTS:-get}
BACKEND_PORT=${BACKEND_PORT:-7379}
# Total requests per LEG, split evenly across that leg's processes, so every leg moves the same number of
# ops and a leg is not advantaged by simply running longer. At ~500k ops/s this is ~8s per leg.
TOTAL_REQUESTS=${TOTAL_REQUESTS:-4000000}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH="${BENCH_EXE:-$REPO/bench/.tools/redis-benchmark}"
GARNET="${GARNET_EXE:-$HOME/.dotnet/tools/garnet-server}"

for f in "$BENCH" "$GARNET"; do
  [[ -x "$f" ]] || { echo "missing/not executable: $f"; exit 1; }
done
command -v taskset >/dev/null || { echo "missing taskset"; exit 1; }

# Same three-way physical-core split run-proxy-ab.sh uses, so the client budget under test here is
# EXACTLY the client budget there. The proxy third is left idle, exactly as the real rig's direct leg
# leaves it -- which is itself worth knowing: the recorded 499,929 ceiling had a third of the box idle.
# CORES="client:proxy:server" PHYSICAL core counts, same spelling as run-proxy-ab.sh. Default is the
# even three-way split that rig uses, so the probe's client budget IS the real rig's client budget.
# Unlike that rig this one accepts a ZERO-core proxy third: there is no proxy here, so those cores are
# otherwise idle, and lending them to the client is exactly how "client-limited" gets distinguished from
# "backend-limited".
CORES=${CORES:-}
read -r CLIENT_CPUS PROXY_CPUS SERVER_CPUS <<<"$(lscpu -p=CPU,CORE | grep -v '^#' | awk -F, -v spec="$CORES" '
  { cpu[NR]=$1; core[NR]=$2; if (!($2 in seen)) { seen[$2]=1; order[++n]=$2 } }
  END {
    if (spec != "") { split(spec, w, ":"); c1=w[1]+0; c2=w[2]+0 } else { c1=int(n/3); c2=int(n/3) }
    if (c1<1) c1=1; if (c2<0) c2=0
    for (i=1;i<=n;i++) { c=order[i]; grp[c] = (i<=c1) ? 1 : (i<=c1+c2 ? 2 : 3) }
    for (i=1;i<=NR;i++) { g=grp[core[i]]; s[g] = (s[g]=="" ? cpu[i] : s[g] "," cpu[i]) }
    print s[1], (s[2]=="" ? "-" : s[2]), s[3]
  }')"
[[ -n "$SERVER_CPUS" ]] || { echo "CPU split failed"; exit 1; }

GOV=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)
EPP=$(cat /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference 2>/dev/null)

STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$REPO/bench/results/client-ceiling-$STAMP"
mkdir -p "$OUT"
CSV="$OUT/results.csv"
echo "leg,rep,procs,conns_total,threads,sum_rps,min_proc_rps,max_proc_rps,wall_spread_s,client_cores,server_cores,p50_ms,p99_ms" >"$CSV"

echo "client-ceiling probe -- $(wc -w <<<"$LEGS") legs x $REPS passes, -P $DEPTH -t $TESTS -d $DATASIZE"
echo "  governor=$GOV/$EPP"
[[ "$GOV" == "performance" ]] || echo "  ** GOVERNOR IS NOT 'performance' -- legs here are comparable to EACH OTHER ONLY, never to RESULTS.md **"
echo "  kernel  =$(uname -r)"
echo "  client  =$CLIENT_CPUS"
echo "  proxy   =$PROXY_CPUS  (IDLE -- no proxy in this probe)"
echo "  server  =$SERVER_CPUS"
echo "  total requests per leg=$TOTAL_REQUESTS (split across that leg's processes)"
echo "  -> $OUT"
echo

CLIENT_NCPU=$(tr ',' '\n' <<<"$CLIENT_CPUS" | grep -c .)
SERVER_NCPU=$(tr ',' '\n' <<<"$SERVER_CPUS" | grep -c .)

# -x matches the process NAME, not the command line: `pkill -f garnet-server` also matches the shell
# running this script. That trap is documented in run-proxy-ab.sh and cost two runs there.
pkill -x garnet-server 2>/dev/null; sleep 1
taskset -c "$SERVER_CPUS" "$GARNET" --port "$BACKEND_PORT" --bind 127.0.0.1 >"$OUT/garnet.log" 2>&1 &
GARNET_PID=$!
cleanup() { kill "$GARNET_PID" 2>/dev/null; wait "$GARNET_PID" 2>/dev/null; }
trap cleanup EXIT INT TERM

for i in {1..40}; do
  timeout 5 "$BENCH" -h 127.0.0.1 -p "$BACKEND_PORT" -n 1 -c 1 -t ping_mbulk --csv >/dev/null 2>&1 && break
  sleep 0.5
done
kill -0 "$GARNET_PID" 2>/dev/null || { echo "garnet-server did not start -- see $OUT/garnet.log"; exit 1; }

# CPU seconds consumed by a pid, from /proc: utime+stime in ticks. Used for the "cores busy" columns,
# which are the only way to tell a raised ceiling from a MOVED one (P16).
cpu_secs() {
  local pid="$1" st
  st=$(cat "/proc/$pid/stat" 2>/dev/null) || { echo 0; return; }
  # utime=14, stime=15, but comm (field 2) may contain spaces -- cut after the closing paren.
  awk -v s="${st#*) }" 'BEGIN { split(s, f, " "); printf "%.3f", (f[12] + f[13]) / '"$(getconf CLK_TCK)"' }'
}

# Leg names are a GRAMMAR, not a list: <procs>x<conns-each>[-t<threads>], e.g. 4x64-t4 is four
# processes of 64 connections with 4 threads each. Default threads is 6, which is the value
# run-proxy-ab.sh hard-codes -- so a bare "1x64" IS the status quo and must stay the control.
leg_spec() { # leg -> "procs conns_each threads"
  local l="$1" procs conns threads=6
  [[ "$l" =~ ^([0-9]+)x([0-9]+)(-t([0-9]+))?$ ]] || return 1
  procs="${BASH_REMATCH[1]}"; conns="${BASH_REMATCH[2]}"
  [[ -n "${BASH_REMATCH[4]}" ]] && threads="${BASH_REMATCH[4]}"
  (( procs >= 1 && conns >= 1 && threads >= 1 )) || return 1
  echo "$procs $conns $threads"
}

measure() { # $1=leg $2=rep
  local leg="$1" rep="$2" procs conns threads
  read -r procs conns threads <<<"$(leg_spec "$leg")" || { echo "unknown leg: $leg"; return; }

  local nreq=$(( TOTAL_REQUESTS / procs ))
  local d="$OUT/$leg-rep$rep"; mkdir -p "$d"

  local srv0 t0 pids=() p
  srv0=$(cpu_secs "$GARNET_PID")
  t0=$(date +%s.%N)

  for ((p=0; p<procs; p++)); do
    # /usr/bin/time gives this process's OWN user+sys, which is how the client-cores column is built
    # without having to race /proc against a process that is about to exit.
    /usr/bin/time -f "%e %U %S" -o "$d/time-$p" \
      taskset -c "$CLIENT_CPUS" "$BENCH" -h 127.0.0.1 -p "$BACKEND_PORT" \
        -c "$conns" -n "$nreq" -d "$DATASIZE" -t "$TESTS" -P "$DEPTH" --threads "$threads" --csv \
        >"$d/bench-$p.csv" 2>"$d/bench-$p.err" &
    pids+=($!)
  done
  for p in "${pids[@]}"; do wait "$p"; done

  local t1 srv1 wall
  t1=$(date +%s.%N); srv1=$(cpu_secs "$GARNET_PID")
  wall=$(awk -v a="$t0" -v b="$t1" 'BEGIN{printf "%.3f", b-a}')

  # Sum per-process rps. An EMPTY csv is a failed process, and a loop over empty input writes nothing --
  # i.e. the leg would silently read as slower rather than as broken. Count them and say so.
  local sum=0 lo="" hi="" nres=0 p50="" p99="" cli=0 wlo="" whi=""
  for ((p=0; p<procs; p++)); do
    local line r e u s
    line=$(grep -v '^"test"' "$d/bench-$p.csv" 2>/dev/null | grep -v '^$' | head -1)
    if [[ -z "$line" ]]; then continue; fi
    r=$(cut -d, -f2 <<<"$line" | tr -d '"')
    [[ -z "$r" ]] && continue
    nres=$((nres+1))
    sum=$(awk -v a="$sum" -v b="$r" 'BEGIN{printf "%.0f", a+b}')
    [[ -z "$lo" || $(awk -v a="$r" -v b="$lo" 'BEGIN{print (a<b)?1:0}') == 1 ]] && lo="$r"
    [[ -z "$hi" || $(awk -v a="$r" -v b="$hi" 'BEGIN{print (a>b)?1:0}') == 1 ]] && hi="$r"
    # p50/p99 reported from the FIRST process only: these are per-connection latencies and averaging
    # percentiles across processes is not a percentile of anything.
    [[ -z "$p50" ]] && { p50=$(cut -d, -f5 <<<"$line" | tr -d '"'); p99=$(cut -d, -f7 <<<"$line" | tr -d '"'); }
    read -r e u s <"$d/time-$p"
    cli=$(awk -v a="$cli" -v b="$u" -v c="$s" 'BEGIN{printf "%.3f", a+b+c}')
    [[ -z "$wlo" || $(awk -v a="$e" -v b="$wlo" 'BEGIN{print (a<b)?1:0}') == 1 ]] && wlo="$e"
    [[ -z "$whi" || $(awk -v a="$e" -v b="$whi" 'BEGIN{print (a>b)?1:0}') == 1 ]] && whi="$e"
  done

  if (( nres != procs )); then
    echo "    $leg rep$rep: only $nres/$procs processes returned a result -- NOT SCORED"
    echo "$leg,$rep,$procs,$((procs*conns)),$threads,,,,,,,," >>"$CSV"
    return
  fi

  local spread ccores scores
  spread=$(awk -v a="$wlo" -v b="$whi" 'BEGIN{printf "%.3f", b-a}')
  ccores=$(awk -v c="$cli" -v w="$wall" 'BEGIN{printf "%.2f", (w>0)?c/w:0}')
  scores=$(awk -v a="$srv0" -v b="$srv1" -v w="$wall" 'BEGIN{printf "%.2f", (w>0)?(b-a)/w:0}')

  echo "$leg,$rep,$procs,$((procs*conns)),$threads,$sum,$lo,$hi,$spread,$ccores,$scores,$p50,$p99" >>"$CSV"
  printf '    %-9s rep%-2s %10.0f ops/s  (procs %s-%s)  client %s/%s cores  server %s/%s cores  spread %ss\n' \
    "$leg" "$rep" "$sum" "$lo" "$hi" "$ccores" "$CLIENT_NCPU" "$scores" "$SERVER_NCPU" "$spread"
  sleep 2
}

for ((rep=1; rep<=REPS; rep++)); do
  echo "  pass $rep/$REPS"
  # Reshuffle every pass: a fixed order lets host drift land on the same leg every time and read as a
  # real difference. Same discipline as run-proxy-ab.sh.
  for leg in $(shuf -e $LEGS); do measure "$leg" "$rep"; done
done

echo
echo "=== summed ops/s, $REPS passes, median (min-max) ==="
awk -F, -v ctl="$CTL" '
  NR>1 && $6!="" { v[$2==""?"":$1][++n[$1]]=$6+0; cc[$1]+=$10; sc[$1]+=$11; sp[$1]=($9+0>sp[$1])?$9+0:sp[$1]
                   procs[$1]=$3; conns[$1]=$4; thr[$1]=$5
                   if (!(($1 SUBSEP $6) in seenval)) { seenval[$1 SUBSEP $6]=1; distinct[$1]++ } }
  function stats(a,c,  i,j,t,tmp){for(i=1;i<=c;i++)tmp[i]=a[i];for(i=1;i<c;i++)for(j=i+1;j<=c;j++)if(tmp[j]<tmp[i]){t=tmp[i];tmp[i]=tmp[j];tmp[j]=t}lo=tmp[1];hi=tmp[c];med=(c%2)?tmp[int(c/2)+1]:(tmp[c/2]+tmp[c/2+1])/2}
  END {
    if (!(ctl in n)) { print "  control leg " ctl " produced nothing -- nothing here is interpretable"; exit }
    stats(v[ctl], n[ctl]); clo=lo; chi=hi; cmed=med
    nl=asorti(n, order, "@ind_str_asc")
    for (i=1;i<=nl;i++) {
      l=order[i]; stats(v[l], n[l])
      # n>=3 before ANY range claim. A single pass has no range, and this rig has already certified a
      # one-pass cell as "clean" once (RESULTS.md, the quantisation audit that lied); a two-pass
      # "disjoint" is barely better. Below that the cell reports its own insufficiency.
      rel = (l==ctl) ? "(CONTROL: the status quo)" \
          : (n[l] < 3 || n[ctl] < 3) ? sprintf("only %d/%d passes -- NO RANGE CLAIM", n[l], n[ctl]) \
          : (lo > chi) ? sprintf("DISJOINT AHEAD of control  %+.1f%%", (med-cmed)/cmed*100) \
          : (clo > hi) ? sprintf("DISJOINT BEHIND control    %+.1f%%", (med-cmed)/cmed*100) \
          : "overlapping -- UNPROVEN, quote no delta"
      printf "  %-9s %2dp x %3dc t%-2d  %10.0f  (%.0f-%.0f)  client %.2f  server %.2f  %s\n",
             l, procs[l], conns[l]/procs[l], thr[l], med, lo, hi, cc[l]/n[l], sc[l]/n[l], rel
      # Quantisation: redis-benchmark reports rps against a coarse timer, so a test that finishes too
      # fast returns a handful of repeated values that look like a tight, trustworthy range. Distinct
      # values below half the pass count is that signature, and it is why TOTAL_REQUESTS must keep each
      # leg running ~8s. The rig header of run-proxy-ab.sh has the full story.
      if (n[l] >= 3 && distinct[l] * 2 <= n[l])
        printf "  %-9s   ^ QUANTISED: only %d distinct values in %d passes -- the range is timer granularity, not spread\n", l, distinct[l], n[l]
      if (sp[l] > 1.0) printf "  %-9s   ^ WALL SPREAD %.2fs between this leg'"'"'s processes -- the SUM overstates an instantaneous rate; do not quote it\n", l, sp[l]
    }
  }' "$CSV"

echo
echo "Ranges overlap => no delta may be quoted (house rule 5). The client/server columns are cores BUSY:"
echo "a raised ceiling shows client cores climbing; a MOVED ceiling shows server cores pegging instead."
echo "raw: $CSV"
