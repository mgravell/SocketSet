#!/usr/bin/env bash
# Runtime correctness gate for the ASP.NET bridge, LINUX EDITION — the port of Verify-AspNet.ps1 that
# TODO's "both want a Linux equivalent" note asked for. run-smoke-matrix.sh gates the TRANSPORT; this
# gates the BRIDGE: /config banner, byte-exact /payload and /echo, /stats counters, across
# backend x bridge-mode x TLS. 18 cells: {io-uring,epoll,managed} x {byo,classic,half-pipe} x {plain,tls}.
#
# House rule 1 (trust the banner, not the flag): /config is gated FIRST and a cell fails outright when
# the banner does not name the backend/mode/tls that were asked for — a flag that parses and is ignored
# produces byte-exact payloads too.
# House rule 2 (confirm the path was TAKEN): /stats gates on accepts > 0; a transport that never
# accepted anything cannot have served the payloads.
#
# usage: bench/verify-aspnet.sh                # all 18 cells
#        FILTER="epoll/*" bench/verify-aspnet.sh
#        KEEP_LOGS=1 bench/verify-aspnet.sh
set -uo pipefail

FILTER=${FILTER:-"*"}
FIRST_PORT=${FIRST_PORT:-5400}
KEEP_LOGS=${KEEP_LOGS:-0}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXE="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"

echo "building AspNetDemo (Release) ..."
dotnet build "$REPO/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }
[[ -x "$EXE" ]] || { echo "no AspNetDemo at $EXE"; exit 1; }

STAMP=$(date +%Y%m%d-%H%M%S)
LOGDIR="$REPO/bench/results/aspnet-$STAMP"
mkdir -p "$LOGDIR"

# Sizes bracket the interesting boundaries (same set as the Windows gate): 1 byte, either side of the
# ~4KB pipe block, 64KB, and 8MB (the far side of the old io_uring IovMax prefix boundary).
PAYLOAD_SIZES="1 2 100 1024 4095 4096 4097 8192 65536 100000 1048576 4194304 8388608"
ECHO_SIZES="1 4096 1048576"

# Expected payload bodies, generated once ('x' = 0x78, what /payload fills with).
EXPECT_DIR="$LOGDIR/.expected"
mkdir -p "$EXPECT_DIR"
for n in $PAYLOAD_SIZES; do head -c "$n" /dev/zero | tr '\0' 'x' > "$EXPECT_DIR/$n"; done

BACKENDS="io-uring:socketset/io_uring epoll:socketset/epoll managed:socketset/managed"
MODES="byo:byo=pipe classic:byo=off half-pipe:half-pipe=1"

port=$FIRST_PORT
pass=0; fail=0; failed_cells=""

for be in $BACKENDS; do
  bname="${be%%:*}"; bbanner="${be##*:}"
  for mo in $MODES; do
    mname="${mo%%:*}"; mbanner="${mo##*:}"
    for tls in "" "+tls"; do
      cell="$bname/$mname$tls"
      [[ "$cell" == $FILTER ]] || continue
      port=$((port+1))
      scheme="http"; targs=""
      tbanner="tls=off"
      if [[ -n "$tls" ]]; then scheme="https"; targs="--tls"; tbanner="tls=openssl"; fi
      safe="${cell//\//-}"
      log="$LOGDIR/$safe.log"

      "$EXE" "--$bname" "--$mname" $targs --port "$port" > "$log" 2>&1 &
      pid=$!
      base="$scheme://127.0.0.1:$port"
      # -k: the demo cert is throwaway self-signed; verification is not the property under test here
      CURL="curl -sk --http1.1 --max-time 30"

      ok=1; detail=""
      cfg=""
      for i in $(seq 1 100); do
        kill -0 $pid 2>/dev/null || break
        cfg=$($CURL "$base/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
        sleep 0.2
      done

      if ! kill -0 $pid 2>/dev/null; then
        ok=0; detail="server exited: $(head -1 "$log")"
      elif [[ -z "$cfg" ]]; then
        ok=0; detail="no /config after 20s"
      else
        # gate 1: the banner, not the flag (+ resolved geometry with no zeros)
        for want in "$bbanner" "$mbanner" "$tbanner"; do
          if [[ "$cfg" != *"$want"* ]]; then ok=0; detail="banner missing '$want'"; break; fi
        done
        if [[ $ok == 1 ]] && ! grep -q '"geometry"' <<<"$cfg"; then ok=0; detail="no resolved geometry"; fi
        if [[ $ok == 1 ]] && grep -Eq '=0[" ]' <<<"$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read()).get("geometry",""))' <<<"$cfg")"; then
          ok=0; detail="geometry has a 0"
        fi
      fi

      # gate 2: byte-exact outbound
      if [[ $ok == 1 ]]; then
        for n in $PAYLOAD_SIZES; do
          if ! $CURL -o "$LOGDIR/.got" "$base/payload?n=$n" || ! cmp -s "$LOGDIR/.got" "$EXPECT_DIR/$n"; then
            ok=0; detail="/payload?n=$n not byte-exact (got $(stat -c%s "$LOGDIR/.got" 2>/dev/null || echo '?') bytes)"; break
          fi
        done
      fi

      # gate 3: byte-exact inbound ('y' fill — distinct from outbound)
      if [[ $ok == 1 ]]; then
        for n in $ECHO_SIZES; do
          head -c "$n" /dev/zero | tr '\0' 'y' > "$LOGDIR/.post"
          reply=$($CURL --data-binary "@$LOGDIR/.post" -H "Content-Type: application/octet-stream" "$base/echo")
          if [[ "$reply" != *"echoed $n bytes"* ]]; then ok=0; detail="/echo $n: '$reply'"; break; fi
        done
      fi

      # gate 4: the transport actually served this
      if [[ $ok == 1 ]]; then
        stats=$($CURL "$base/stats")
        accepts=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read()).get("accepts",0))' <<<"$stats" 2>/dev/null || echo 0)
        writefail=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read()).get("writeFail",0))' <<<"$stats" 2>/dev/null || echo 0)
        if [[ "$accepts" -le 0 ]]; then ok=0; detail="stats.accepts=$accepts -- transport never accepted"
        elif [[ "$writefail" -gt 0 ]]; then ok=0; detail="stats.writeFail=$writefail"
        else detail="accepts=$accepts"; fi
      fi

      kill $pid 2>/dev/null; wait $pid 2>/dev/null
      if [[ $ok == 1 ]]; then
        pass=$((pass+1)); [[ "$KEEP_LOGS" == 1 ]] || rm -f "$log"
        printf "  %-24s PASS  %s\n" "$cell" "$detail"
      else
        fail=$((fail+1)); failed_cells="$failed_cells $cell"
        printf "  %-24s FAIL  %s\n" "$cell" "$detail"
      fi
      sleep 0.4  # let teardown settle so a wedge is attributable to its own cell
    done
  done
done

echo
if [[ $fail -gt 0 ]]; then
  echo "$fail/$((pass+fail)) FAILED:$failed_cells -- logs in $LOGDIR"
  exit 1
fi
echo "all $pass cells PASS"
