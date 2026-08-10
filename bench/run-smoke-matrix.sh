#!/usr/bin/env bash
# The Linux correctness gate: SmokeTest across io_uring / epoll / managed, plaintext and TLS.
#
# This is the .sh counterpart of Run-SmokeMatrix.ps1. There are no unit tests in this repo; SmokeTest IS
# the correctness gate, and AGENTS.md asks for it on every backend touched. On Windows that has been one
# command since 2026-07-29; on Linux it was still by hand, which is exactly how a gate gets skipped
# between OSes. This runs the whole matrix and reduces it to one PASS/FAIL line per cell.
#
# Written 2026-07-31, cold-starting Linux after three days of Windows-only work in which SHARED code
# changed underneath epoll and io_uring (see TODO.md's "READ FIRST IF YOU ARE ON LINUX"). The sharpest
# cells here are:
#   - the --verify (out-of-band Connection.Send / Flush) legs, because PipeIoBridge's outbound pump was
#     rewritten for prefix sends and both Linux backends run it;
#   - the --verify-echo --pipe legs, because the pipe path is the one that changed;
#   - the @abstract UDS legs, because SocketSet.Listen/Connect gained endpoint validation and the guard
#     must NOT fire on Linux (io_uring maps a leading '@' to the abstract namespace — a real feature here).
#
# Backends: io_uring is the auto-detected default on this bare-metal box (no flag); --epoll and -m force
# the other two. TLS legs use --tls-ssl (real OpenSSL), NOT --tls (the no-crypto identity filter), because
# TLS is where the out-of-band flush path does most of its work.
#
# Usage:
#   bench/run-smoke-matrix.sh                 # whole matrix
#   bench/run-smoke-matrix.sh '*iouring*'     # filter by cell name (glob)
#   FIRST_PORT=11000 CHURN_REPS=3 KEEP_LOGS=1 bench/run-smoke-matrix.sh
set -u

FILTER="${1:-*}"
FIRST_PORT="${FIRST_PORT:-10500}"
TIMEOUT_SEC="${TIMEOUT_SEC:-90}"     # per-cell wall-clock ceiling; a wedge must be reported, not hung on
CHURN_REPS="${CHURN_REPS:-5}"        # churn cells run N times, WORST outcome wins: an intermittent slot-reuse
                                     # fault a single run would call PASS is exactly how such bugs survive
KEEP_LOGS="${KEEP_LOGS:-0}"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$repo/SmokeTest/SmokeTest.csproj"
exe="$repo/SmokeTest/bin/Release/net10.0/SmokeTest"

c_cyan=$'\033[36m'; c_green=$'\033[32m'; c_red=$'\033[31m'; c_off=$'\033[0m'

echo "${c_cyan}building SmokeTest (Release, net10.0) ...${c_off}"
if ! dotnet build "$proj" -c Release -v q --nologo -f net10.0 >/tmp/smoke-build.log 2>&1; then
    cat /tmp/smoke-build.log; echo "${c_red}build failed${c_off}"; exit 2
fi
[ -x "$exe" ] || { echo "${c_red}no SmokeTest at $exe${c_off}"; exit 2; }

stamp="$(date +%Y%m%d-%H%M%S)"
log_dir="$repo/bench/results/smoke-$stamp"
mkdir -p "$log_dir"

# --- Backend x TLS. Managed is included because it derives from the same OutboundConnection base and is
#     what actually runs wherever io_uring is unavailable (old kernel, the disable sysctl, a container
#     seccomp profile). It has its own per-connection TLS gate, so it is worth its own column. ---
backend_names=(iouring epoll managed)
backend_args=(""       "--epoll" "-m")

tls_suffix=(""     "+tls")
tls_args=(""       "--tls-ssl")

# --- Tests, in increasing order of what they would catch. verify-oob-* are the Flush hand-off cells:
#     7000-byte segments (the --verify default) straddle the 4096 page boundary, and the three payload
#     sizes bracket the accumulator's doubling, so 64KB/1MB/4MB leave the handed-over array substantially
#     larger than `length` — the exact condition the rewritten pump must handle. echo-pipe-* exercise the
#     path that actually changed. ---
test_names=(verify-oob-64k verify-oob-1m verify-oob-4m echo-cb-64k echo-pipe-64k echo-pipe-4k poke churn)
declare -A test_args=(
    [verify-oob-64k]="--verify 65536"
    [verify-oob-1m]="--verify 1048576"
    [verify-oob-4m]="--verify 4194304"
    [echo-cb-64k]="--verify-echo 1048576 -z 65536"
    [echo-pipe-64k]="--verify-echo 1048576 -z 65536 --pipe"
    [echo-pipe-4k]="--verify-echo 1048576 -z 4096 --pipe"
    [poke]="-s -c 8 -t 5 --poke -z 4096"
    [churn]="-s -c 64 --churn 10 --close-after 4 --sockets 128 --reset-close"
)
# Which judging rule each test uses (the test name's prefix already implies it, but be explicit).
rule_of() {
    case "$1" in
        verify-oob-*) echo verify ;;
        echo-*)       echo echo ;;
        uds-*)        echo echo ;;
        poke)         echo poke ;;
        churn)        echo churn ;;
    esac
}

# --- Judge a cell's output + exit code. Prints "<ok 0|1>|<detail>". A hard crash must be NAMED rather
#     than fall through as "no result line": on Linux SIGSEGV=139, SIGABRT=134, SIGBUS=135, SIGFPE=136
#     (128 + signo); timeout's TERM/KILL show as 124/137. ---
judge() {
    local rule="$1" file="$2" rc="$3" text
    text="$(cat "$file" 2>/dev/null)"
    case "$rc" in
        124|137) echo "0|TIMEOUT after ${TIMEOUT_SEC}s (wedged)"; return ;;
        139) echo "0|SEGFAULT (SIGSEGV)"; return ;;
        134) echo "0|ABORT (SIGABRT)"; return ;;
        135) echo "0|BUS ERROR (SIGBUS)"; return ;;
        136) echo "0|FPE (SIGFPE)"; return ;;
    esac
    if grep -q '### UNHANDLED ###' <<<"$text"; then echo "0|unhandled exception"; return; fi
    case "$rule" in
        verify)
            if [[ "$text" =~ verify:\ received=([0-9]+)/([0-9]+)\ mismatches=([0-9]+)\ =\>\ (PASS|FAIL) ]]; then
                local got="${BASH_REMATCH[1]}" exp="${BASH_REMATCH[2]}" mm="${BASH_REMATCH[3]}" v="${BASH_REMATCH[4]}"
                [[ "$v" == PASS ]] && echo "1|received=$got/$exp mismatches=$mm" || echo "0|received=$got/$exp mismatches=$mm"
            else echo "0|no verify line"; fi ;;
        echo)
            if [[ "$text" =~ verify-echo:\ roundtripped=([0-9]+)/([0-9]+)\ clientMismatch=([0-9]+)\ serverMismatch=([0-9]+)\ =\>\ (PASS|FAIL) ]]; then
                local rt="${BASH_REMATCH[1]}" exp="${BASH_REMATCH[2]}" cm="${BASH_REMATCH[3]}" sm="${BASH_REMATCH[4]}" v="${BASH_REMATCH[5]}"
                [[ "$v" == PASS ]] && echo "1|rt=$rt/$exp cm=$cm sm=$sm" || echo "0|rt=$rt/$exp cm=$cm sm=$sm"
            else echo "0|no verify-echo line"; fi ;;
        churn)
            # The done line carries an em dash and ends in "=> PASS" or "=> FAIL (wedged)".
            if [[ "$text" =~ churn:\ done.*live=([0-9]+).*=\>\ (PASS|FAIL) ]]; then
                [[ "${BASH_REMATCH[2]}" == PASS ]] && echo "1|live=${BASH_REMATCH[1]}" || echo "0|live=${BASH_REMATCH[1]} WEDGED"
            else echo "0|no churn result line"; fi ;;
        poke)
            if [[ "$text" =~ done:\ ([0-9,]+)\ round-trip\ bytes ]]; then
                local b="${BASH_REMATCH[1]//,/}"
                [[ "$b" -gt 0 ]] 2>/dev/null && echo "1|${BASH_REMATCH[1]} round-trip bytes" || echo "0|zero round-trip bytes"
            else echo "0|no round-trip total"; fi ;;
        *) echo "0|no rule" ;;
    esac
}

# --- Build the cell list: backend x tls x test, plus a dedicated @abstract-UDS block for the two native
#     backends (io_uring + epoll), which is where the endpoint-validation guard and the '@' mapping live.
#     The managed backend uses .NET's UnixDomainSocketEndPoint directly and does NOT map '@' to abstract,
#     so an abstract-UDS managed cell would test a different thing; it is deliberately excluded. ---
cell_names=(); cell_args=(); cell_rules=()
add_cell() { cell_names+=("$1"); cell_args+=("$2"); cell_rules+=("$(rule_of "$3")"); }

for bi in "${!backend_names[@]}"; do
    for ti in "${!tls_suffix[@]}"; do
        for t in "${test_names[@]}"; do
            name="${backend_names[$bi]}${tls_suffix[$ti]}/$t"
            args="${backend_args[$bi]} ${tls_args[$ti]} ${test_args[$t]}"
            add_cell "$name" "$args" "$t"
        done
    done
done
# @abstract UDS: the handover's explicit "it must still work here" case. Plaintext and TLS, echo over the
# pipe path (the one that changed). The abstract name is made unique per cell from its port, below.
for bi in 0 1; do   # iouring, epoll
    for ti in "${!tls_suffix[@]}"; do
        name="${backend_names[$bi]}${tls_suffix[$ti]}/uds-echo-pipe"
        args="${backend_args[$bi]} ${tls_args[$ti]} --verify-echo 1048576 -z 65536 --pipe -u @socketset-smoke"
        add_cell "$name" "$args" "uds-echo-pipe"
    done
done
# kTLS (kernel TLS offload) — io_uring and epoll only; needs the `tls` kernel module (modprobe tls) and,
# for RX offload, OpenSSL 3.2+ (TX-only on older, which the [ktls] banner reports). Managed/RIO/IOCP have no
# kTLS path, so no cells for them. Covers the out-of-band Flush leg (verify) plus callback and pipe echo.
if command -v modprobe >/dev/null && [ -d /proc/net ] && (lsmod 2>/dev/null | grep -q '^tls' || [ -e /proc/net/tls_stat ]); then
    for bi in 0 1; do   # iouring, epoll
        add_cell "${backend_names[$bi]}+ktls/verify-oob-4m" "${backend_args[$bi]} --ktls --verify 4194304" verify-oob-4m
        add_cell "${backend_names[$bi]}+ktls/echo-cb-64k"   "${backend_args[$bi]} --ktls --verify-echo 1048576 -z 65536" echo-cb-64k
        add_cell "${backend_names[$bi]}+ktls/echo-pipe-4k"  "${backend_args[$bi]} --ktls --verify-echo 1048576 -z 4096 --pipe" echo-pipe-4k
    done
else
    echo "note: kTLS cells skipped — the 'tls' kernel module is not loaded (modprobe tls to enable)"
fi
# Big-pipe echo: 8MB over the pipe with a deep pipeline window, so the outbound pipe accumulates far more
# than IovMax (1024) segments in flight. This is the io_uring zero-copy PREFIX path — a >IovMax sequence is
# sent as several zero-copy writevs rather than falling back to a full copy (proven separately on the demo:
# an 8MB response goes 100% zero-copy). Byte-exact here is the correctness guard for the prefix boundary
# math. Both native backends (epoll's pipe path exercises the same many-segment send without zero-copy).
#
# DEFAULT cap, restored 2026-08-10 the same day it was opted out — this cell is soft parking's gate flip.
# History, because this cell has flipped twice in one day and the reason matters: the 2048x4KB window
# legitimately puts 8 MiB in flight, and since D3 (8dfbe6c, 2026-08-04) MaxInboundBufferBytes drops a
# connection that runs >4 MiB ahead of a consumer the backend cannot park for. io_uring could not park, so
# this cell failed INTERMITTENTLY (~2/3) from D3 onwards (D3's own "smoke 60/60" was a lucky pass) and
# briefly ran --max-inbound 0. With TODO 0a's soft parking landed, an async flush parks the receive,
# staged inbound stays small, and this cell passing WITH the default cap and an 8 MiB window is precisely
# the proof the soft park holds under echo load. If it ever flakes here again, parking regressed — do NOT
# quiet it with --max-inbound 0; that knob is for shapes whose purpose excludes the bound.
for bi in 0 1; do   # iouring, epoll
    add_cell "${backend_names[$bi]}/echo-pipe-8m-deep" "${backend_args[$bi]} --verify-echo 8388608 -z 4096 --pipe --window 2048" echo-cb-64k
done

# Filter (glob).
sel_names=(); sel_args=(); sel_rules=()
for i in "${!cell_names[@]}"; do
    # shellcheck disable=SC2053
    if [[ "${cell_names[$i]}" == $FILTER ]]; then
        sel_names+=("${cell_names[$i]}"); sel_args+=("${cell_args[$i]}"); sel_rules+=("${cell_rules[$i]}")
    fi
done
[ "${#sel_names[@]}" -gt 0 ] || { echo "${c_red}no cells matched filter '$FILTER'${c_off}"; exit 2; }

echo
echo "${c_cyan}smoke matrix: ${#sel_names[@]} cells -> $log_dir${c_off}"
echo

port=$FIRST_PORT
fail_count=0
declare -a summary
for i in "${!sel_names[@]}"; do
    name="${sel_names[$i]}"; base_args="${sel_args[$i]}"; rule="${sel_rules[$i]}"
    reps=1; [[ "$name" == */churn ]] && reps=$CHURN_REPS
    ok=1; detail=""; worst=0; failed=0
    for ((rep=1; rep<=reps; rep++)); do
        port=$((port+1))
        args="$base_args"
        # UDS cells ignore --port for the socket, but a unique abstract name avoids collisions between
        # cells and any lingering peer from a previous run.
        if [[ "$base_args" == *"-u @socketset-smoke"* ]]; then
            args="${base_args/@socketset-smoke/@socketset-smoke-$port}"
        else
            args="$base_args --port $port"
        fi
        safe="${name//\//-}"
        out="$log_dir/$safe.r$rep.log"
        t0=$(date +%s.%N)
        # shellcheck disable=SC2086
        timeout --kill-after=5 -s TERM "$TIMEOUT_SEC" "$exe" $args >"$out" 2>&1
        rc=$?
        t1=$(date +%s.%N)
        secs=$(awk "BEGIN{printf \"%.1f\", $t1-$t0}")
        awk "BEGIN{exit !($secs>$worst)}" && worst=$secs
        res="$(judge "$rule" "$out" "$rc")"
        rep_ok="${res%%|*}"; rep_detail="${res#*|}"
        if [[ "$rep_ok" != 1 ]]; then
            failed=$((failed+1)); [[ "$ok" == 1 ]] && detail="$rep_detail"; ok=0
        else
            [[ "$reps" == 1 ]] && detail="$rep_detail"
            [[ "$KEEP_LOGS" == 1 ]] || rm -f "$out"
        fi
    done
    if [[ "$reps" -gt 1 ]]; then
        [[ "$ok" == 1 ]] && detail="$reps/$reps clean" || detail="$failed/$reps FAILED - $detail"
    fi
    if [[ "$ok" == 1 ]]; then
        printf "  %-30s ${c_green}PASS${c_off} %6.1fs  %s\n" "$name" "$worst" "$detail"
        summary+=("PASS|$name|$worst|$detail")
    else
        printf "  %-30s ${c_red}FAIL${c_off} %6.1fs  %s\n" "$name" "$worst" "$detail"
        summary+=("FAIL|$name|$worst|$detail")
        fail_count=$((fail_count+1))
    fi
    # Give teardown a moment so a wedge is attributable to the cell that caused it.
    sleep 0.3
done

echo
if [[ "$fail_count" -gt 0 ]]; then
    echo "${c_red}$fail_count/${#sel_names[@]} FAILED - logs in $log_dir${c_off}"
    for s in "${summary[@]}"; do [[ "$s" == FAIL* ]] && echo "  ${s//|/  }"; done
    exit 1
fi
echo "${c_green}all ${#sel_names[@]} cells PASS${c_off}"
[[ "$KEEP_LOGS" == 1 ]] && echo "logs in $log_dir" || rmdir "$log_dir" 2>/dev/null
exit 0
