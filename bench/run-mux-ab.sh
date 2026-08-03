#!/usr/bin/env bash
# SE.REDIS-OVER-SOCKETSET vs CLASSIC — the client-seat A/B the transport-mode work exists to answer.
#
# One ConnectionMultiplexer, D concurrent awaiting workers, identical generator code both legs; the ONLY
# difference is ConfigurationOptions.Tunnel. Server is STOCK Garnet (GarnetDemo --stock, SAEA +
# SslStream) so the server is neutral and identical for every leg. Legs interleaved within each pass,
# order reshuffled per pass, 6 passes, ranges-not-medians. The tunnel legs are gated on the counting
# tunnel having been ASKED for transports (mux-ab exits 2 -> NORESULT otherwise).
#
# PRE-REGISTERED (2026-08-03, before the first full run — the smoke run showed tunnel ahead at depth 4
# but 2s windows prove nothing):
#   P1: depth-1 (sequential await): parity to modest tunnel win (<= +10%). The RTT chain is one
#       round-trip either way; SE.Redis's own overhead should dominate. A DISJOINT tunnel LOSS > 5%
#       falsifies "the engine hop is free" and implicates staging+loop-wake per op.
#   P2: depth-64: tunnel wins >= +15% (push-parse on the loop thread + single-writer batching vs
#       reader-Task + per-op syscalls). If NOT, the prime suspect is the receive copy
#       (transport span -> CycleBuffer), and receive-into-caller-buffer gets promoted from deferred to
#       next.
#   P3: TLS: the tunnel advantage WIDENS vs the plaintext delta at the same depth (in-transport OpenSSL
#       vs SslStream; the server-side precedent was +9.5-24.2%). If TLS does NOT widen it, the
#       client-seat record pattern differs from the server seat and that is the finding.
#
# usage: bench/run-mux-ab.sh
#        REPS=6 DEPTHS="1 64" OPS="get set" LEGS="classic tunnel classic-tls tunnel-tls" bench/run-mux-ab.sh
set -uo pipefail

REPS=${REPS:-6}
DEPTHS=${DEPTHS:-"1 64"}
OPS=${OPS:-"get set"}
LEGS=${LEGS:-"classic tunnel classic-tls tunnel-tls"}
SECONDS_PER=${SECONDS_PER:-10}
PLAIN_PORT=${PLAIN_PORT:-7379}
TLS_PORT=${TLS_PORT:-7391}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MA="$REPO/bench/mux-ab/bin/Release/net10.0/mux-ab"
GD="$REPO/GarnetDemo/bin/Release/net10.0/GarnetDemo"
for f in "$MA" "$GD"; do [[ -x "$f" ]] || { echo "missing: $f (build first)"; exit 1; }; done

# Client-heavy topology split (physical-core aware; the client is the thing under test and its
# scheduling jitter is the tail). Server gets the last quarter; middle quarter left idle as isolation.
read -r CLIENT_CPUS SPARE_CPUS SERVER_CPUS <<<"$(lscpu -p=CPU,CORE | grep -v '^#' | awk -F, '
  { cpu[NR]=$1; core[NR]=$2; if (!($2 in seen)) { seen[$2]=1; order[++n]=$2 } }
  END { c1=int(n/2); c2=int(n/4); if (c1<1) c1=1; if (c2<1) c2=1
    for (i=1;i<=n;i++) { c=order[i]; grp[c]=(i<=c1)?1:(i<=c1+c2?2:3) }
    for (i=1;i<=NR;i++) { g=grp[core[i]]; s[g]=(s[g]==""?cpu[i]:s[g] "," cpu[i]) }
    print s[1], s[2], s[3] }')"

STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$REPO/bench/results/mux-ab-$STAMP"
mkdir -p "$OUT"
CSV="$OUT/results.csv"
echo "pass,leg,op,depth,ops_per_sec,p50_ms,p99_ms,p999_ms,samples" > "$CSV"

echo "mux-ab: legs={$LEGS} ops={$OPS} depths={$DEPTHS} reps=$REPS window=${SECONDS_PER}s"
echo "  governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)"
echo "  client=$CLIENT_CPUS server=$SERVER_CPUS (spare=$SPARE_CPUS)"
echo "  -> $OUT"

pkill -x GarnetDemo 2>/dev/null; sleep 1
taskset -c "$SERVER_CPUS" "$GD" --stock --port "$PLAIN_PORT" >"$OUT/garnet-plain.log" 2>&1 &
taskset -c "$SERVER_CPUS" "$GD" --stock --tls --port "$TLS_PORT" >"$OUT/garnet-tls.log" 2>&1 &
cleanup() { pkill -x GarnetDemo 2>/dev/null; }
trap cleanup EXIT INT TERM
sleep 3
grep -q "transport=garnet-saea tls=off" "$OUT/garnet-plain.log" || { echo "PLAIN SERVER BANNER MISSING"; exit 1; }
grep -q "transport=garnet-saea tls=sslstream" "$OUT/garnet-tls.log" || { echo "TLS SERVER BANNER MISSING"; exit 1; }

for pass in $(seq 1 "$REPS"); do
  # reshuffle leg order each pass
  SHUFFLED=$(tr ' ' '\n' <<<"$LEGS" | shuf | tr '\n' ' ')
  echo "--- pass $pass: order = $SHUFFLED"
  for leg in $SHUFFLED; do
    port=$PLAIN_PORT; [[ "$leg" == *-tls ]] && port=$TLS_PORT
    for op in $OPS; do
      for depth in $DEPTHS; do
        line=$(taskset -c "$CLIENT_CPUS" "$MA" "$leg" 127.0.0.1 "$port" "$op" "$depth" "$SECONDS_PER" 2>"$OUT/$leg-$op-$depth-p$pass.err" | grep "^RESULT," || true)
        if [[ -n "$line" ]]; then
          echo "$pass,${line#RESULT,}" >> "$CSV"
          echo "  $pass $leg $op d$depth: ${line#RESULT,$leg,$op,$depth,}"
        else
          echo "$pass,$leg,$op,$depth,NORESULT,,,," >> "$CSV"
          echo "  $pass $leg $op d$depth: NORESULT (see .err)"
        fi
      done
    done
  done
done

echo
echo "=== SUMMARY (min-max across $REPS passes; verdict DISJOINT only if ranges do not overlap) ==="
awk -F, 'NR>1 && $5 != "NORESULT" {
  k=$2","$3","$4
  if (!(k in min) || $5+0 < min[k]) min[k]=$5+0
  if (!(k in max) || $5+0 > max[k]) max[k]=$5+0
  if (!(k in p99min) || $7+0 < p99min[k]) p99min[k]=$7+0
  if (!(k in p99max) || $7+0 > p99max[k]) p99max[k]=$7+0
  n[k]++
}
END {
  printf "%-14s %-4s %-6s %-26s %-20s %s\n", "leg", "op", "depth", "ops/s (min-max)", "p99 ms (min-max)", "n"
  for (k in min) {
    split(k, a, ",")
    printf "%-14s %-4s %-6s %12d - %-12d %8.3f - %-8.3f %d\n", a[1], a[2], a[3], min[k], max[k], p99min[k], p99max[k], n[k]
  }
  print ""
  print "pairwise (tunnel vs classic, same op/depth/tls):"
  for (k in min) {
    split(k, a, ",")
    if (a[1] == "tunnel" || a[1] == "tunnel-tls") {
      base = (a[1] == "tunnel") ? "classic" : "classic-tls"
      bk = base","a[2]","a[3]
      if (bk in min) {
        delta = (min[k]+max[k]) / (min[bk]+max[bk]) * 100 - 100
        verdict = (min[k] > max[bk]) ? "DISJOINT tunnel ahead" : (max[k] < min[bk]) ? "DISJOINT classic ahead" : "overlapping"
        printf "  %s %s d%s: %+.1f%% (midpoint) — %s\n", a[1], a[2], a[3], delta, verdict
      }
    }
  }
}' "$CSV" | tee "$OUT/summary.txt"
echo "csv: $CSV"
