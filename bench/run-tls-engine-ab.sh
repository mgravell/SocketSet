#!/usr/bin/env bash
# TLS engine A/B: the in-box .NET 11 TlsBufferSession provider ("tls-session") vs OpenSSL memory-BIOs
# ("openssl"), same SmokeTest cell, same process shape, managed backend — TODO.md 2026-10-13 item 1.
#
# METHOD (the house rules, bench/README.md):
#  * One binary (net11.0 Release), legs differ ONLY by TLS flag; never a cross-run comparison.
#  * REPS passes, the FIRST is discarded (cold-boost transient spans a whole pass); leg order is
#    RESHUFFLED every pass; six scored passes by default, not three.
#  * Every run is gated on the "set:" BANNER (tls=openssl / tls=tls-session / absent), never the flag.
#    An un-gated TLS leg whose flag was ignored measures exactly like plaintext — which is why the
#    plaintext control doubles as the tripwire: a "TLS" leg scoring inside the plaintext range is a
#    rig failure, not a fast engine.
#  * CONTROL IN THE MATRIX, READ FIRST: the plain leg's answer is known (it must beat both TLS legs).
#  * Output is per-pass values + min/median/max ranges. If the two TLS ranges OVERLAP, the result is
#    "indistinguishable on this rig" and no delta may be quoted.
#
# PRE-REGISTERED (before the first full run, 2026-10-16):
#  * Prediction: tls-session TRAILS openssl in both regimes — its filter pays a carry-buffer pass and a
#    drain-before-feed check per wake, and the engine encrypts one record per Write call; OpenSSL pays
#    BIO copies but is the tuned incumbent. Falsified if tls-session's six-pass range sits wholly ABOVE
#    openssl's in either regime.
#    [OUTCOME, first scored run: held — openssl +10.7% / +11.5%, disjoint. See RESULTS.md.]
#  * Prediction for the sslstream leg (pre-registered 2026-10-16 before its first scored run): the full
#    ordering is openssl > tls-session > sslstream in both regimes — the bridge charges a staging copy
#    each way, pool hops, and a block-until-quiescent on every inbound wake; prior art is Kestrel's union
#    A/B (dotnet/aspnetcore#69565, buffer-session ~8% above SslStream). Falsified if sslstream's range
#    sits wholly ABOVE tls-session's in either regime.
#  * Falsifies the RIG: plain not beating every TLS leg; any banner mismatch; any pass where TLS legs
#    and plain all agree within noise (uniformity across cells that should differ).
#
# TWO REGIMES, because the engines can rank differently in each:
#  * 4k-w64  — record-sized messages, deep window: per-record loop/filter overhead dominates, the
#    cipher is rounding error. This is where the wrapper (carry/drain/stash) shows up.
#  * 256k-w4 — 256KB messages (house standard size): bulk crypto, ~16 records per message; this is
#    where the PAL's cipher throughput shows up. Needs --page 262144 (SmokeTest clamps -z to the page).
#
# Usage:  bench/run-tls-engine-ab.sh
#         REPS=7 DURATION=5 FILTER='*256k*' bench/run-tls-engine-ab.sh
set -uo pipefail

REPS=${REPS:-7}            # scored passes = REPS - 1
DURATION=${DURATION:-5}
CONNS=${CONNS:-8}
PORT=${PORT:-12100}        # incremented every run; holds 8 keep-alive conns, so TIME_WAIT is not a factor
FILTER=${FILTER:-*}
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXE="$REPO/SmokeTest/bin/Release/net11.0/SmokeTest"
OUT="${OUT:-$REPO/bench/results}"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/tls-engine-ab-$STAMP.csv"
LOGS="$OUT/logs-tls-engine-ab-$STAMP"
mkdir -p "$OUT" "$LOGS"

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_red=$'\033[31m'; c_off=$'\033[0m'

echo "${c_cyan}building SmokeTest (Release, net11.0) ...${c_off}"
dotnet build "$REPO/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo -f net11.0 >/tmp/tls-ab-build.log 2>&1 \
    || { cat /tmp/tls-ab-build.log; echo "${c_red}build failed${c_off}"; exit 2; }
[ -x "$EXE" ] || { echo "${c_red}no SmokeTest at $EXE${c_off}"; exit 2; }

# name|extra args|banner regex the run MUST print (rule 1: trust the banner, never the flag)
LEGS=(
  "plain||tls=off"
  "openssl|--tls-ssl|tls=openssl \(Both"
  "tls-session|--tls-session|tls=tls-session \(Both"
  "sslstream|--tls-sslstream|tls=sslstream \(Both"
)
WORKLOADS=(
  "4k-w64|-z 4096 --window 64"
  # --recv-buffer 262144 is LOAD-BEARING: at the default 4KB receive buffer the PLAINTEXT leg of this
  # shape WEDGES (round-trips freeze ~35 messages in; observed 2026-10-16, managed backend, net11) —
  # the control caught it, exactly its job. The TLS legs sail at 4KB because records are 16KB. Recorded
  # in TODO.md as an open observation; same flag on every leg here, so the comparison stays fair.
  "256k-w4|-z 262144 --window 4 --page 262144 --recv-buffer 262144"
)

echo "leg,workload,pass,scored,mibps,bytes,secs" > "$CSV"
run_no=0

run_leg() { # leg_name extra_args banner_re workload_name workload_args pass scored
    local name="$1" extra="$2" banner_re="$3" wname="$4" wargs="$5" pass="$6" scored="$7"
    run_no=$((run_no + 1))
    local port=$((PORT + run_no))
    local log="$LOGS/$wname-$name-p$pass.log"
    # shellcheck disable=SC2086
    "$EXE" -m -s -c "$CONNS" -t "$DURATION" --port "$port" $wargs $extra >"$log" 2>&1
    local rc=$?
    if (( rc != 0 )); then echo "${c_red}  $wname/$name pass $pass: exit $rc${c_off}"; cat "$log"; exit 3; fi
    # Banner gates: the managed backend AND the expected tls= (or its absence) must be SAID by the process.
    grep -Eq 'set: EchoServer \[managed\]' "$log" || { echo "${c_red}  $wname/$name: backend banner missing/wrong${c_off}"; cat "$log"; exit 3; }
    grep -Eq "$banner_re" "$log" || { echo "${c_red}  $wname/$name: tls banner mismatch (wanted /$banner_re/)${c_off}"; cat "$log"; exit 3; }
    if [[ "$name" == "plain" ]] && grep -Eq 'tls=(openssl|tls-session|sslstream|schannel)' "$log"; then
        echo "${c_red}  $wname/plain: control unexpectedly ran TLS${c_off}"; cat "$log"; exit 3; fi
    local done_line bytes secs mibps
    done_line=$(grep -E 'done: [0-9,]+ round-trip bytes' "$log") || { echo "${c_red}  $wname/$name: no done line${c_off}"; cat "$log"; exit 3; }
    bytes=$(sed -E 's/done: ([0-9,]+) round-trip.*/\1/' <<<"$done_line" | tr -d ,)
    secs=$(sed -E 's/.*over ([0-9.]+)s .*/\1/' <<<"$done_line")
    mibps=$(awk -v b="$bytes" -v s="$secs" 'BEGIN{printf "%.1f", b/s/1048576}')
    echo "$name,$wname,$pass,$scored,$mibps,$bytes,$secs" >> "$CSV"
    printf '  %-12s %-9s pass %s%s  %8s MiB/s\n' "$wname" "$name" "$pass" "$([ "$scored" = 1 ] && echo '  ' || echo ' D')" "$mibps"
}

for w in "${WORKLOADS[@]}"; do
    IFS='|' read -r wname wargs <<<"$w"
    # shellcheck disable=SC2254
    case "$wname" in $FILTER) ;; *) continue ;; esac
    echo "${c_cyan}workload $wname ($wargs): $REPS passes, first discarded, order shuffled per pass${c_off}"
    for pass in $(seq 1 "$REPS"); do
        scored=$([ "$pass" -gt 1 ] && echo 1 || echo 0)
        while read -r idx; do
            IFS='|' read -r name extra banner_re <<<"${LEGS[$idx]}"
            run_leg "$name" "$extra" "$banner_re" "$wname" "$wargs" "$pass" "$scored"
        done < <(shuf -e 0 1 2 3)
    done
done

echo
echo "${c_cyan}=== scored ranges (passes 2..$REPS; MiB/s) ===${c_off}"
awk -F, -v red="$c_red" -v green="$c_green" -v off="$c_off" '
NR>1 && $4==1 { k=$2 "/" $1; n[k]++; v[k]=v[k] " " $5 }
function verdict(w, a, b,   ka, kb) {
    ka = w "/" a; kb = w "/" b
    if (!(ka in lo) || !(kb in lo)) return
    if (lo[ka] > hi[kb])
        printf "  verdict: %s ABOVE %s, ranges DISJOINT (med %+.1f%%)\n", a, b, (medv[ka]/medv[kb]-1)*100
    else if (lo[kb] > hi[ka])
        printf "  verdict: %s ABOVE %s, ranges DISJOINT (med %+.1f%%) - check the pre-registered ordering\n", b, a, (medv[kb]/medv[ka]-1)*100
    else
        printf "  verdict: %s vs %s ranges OVERLAP - indistinguishable on this rig; quote NO delta\n", a, b
}
END {
    split("4k-w64 256k-w4", ws, " "); split("plain openssl tls-session sslstream", ls, " ")
    for (wi=1; wi<=2; wi++) {
        w=ws[wi]; if (!((w "/plain") in v) && !((w "/openssl") in v)) continue
        printf "%s:\n", w
        for (li=1; li<=4; li++) {
            k=w "/" ls[li]; if (!(k in v)) continue
            m=split(substr(v[k],2), a, " "); mn=a[1]; mx=a[1]
            for (i=1;i<=m;i++){ if (a[i]+0<mn+0) mn=a[i]; if (a[i]+0>mx+0) mx=a[i] }
            for (i=1;i<m;i++) for (j=i+1;j<=m;j++) if (a[j]+0<a[i]+0){t=a[i];a[i]=a[j];a[j]=t}
            med = (m%2) ? a[(m+1)/2] : (a[m/2]+a[m/2+1])/2
            printf "  %-12s n=%d  min=%8.1f  med=%8.1f  max=%8.1f  passes:%s\n", ls[li], m, mn, med, mx, v[k]
            lo[k]=mn+0; hi[k]=mx+0; medv[k]=med+0
        }
        # control first: plain must beat every TLS leg outright
        if ((w"/plain") in lo) {
            ok = 1
            for (li=2; li<=4; li++) { k=w "/" ls[li]; if ((k in lo) && lo[w"/plain"] <= hi[k]) ok = 0 }
            printf "  control: plain beats every TLS leg disjointly: %s\n", ok ? green "YES" off : red "NO - RIG SUSPECT" off
        }
        verdict(w, "openssl", "tls-session")
        verdict(w, "tls-session", "sslstream")
        verdict(w, "openssl", "sslstream")
    }
}' "$CSV"
echo
echo "csv: $CSV"
echo "logs: $LOGS"
