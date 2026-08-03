#!/usr/bin/env bash
# TLS min-version floor gate, LINUX EDITION (port of Verify-TlsFloor.ps1): proves the floor is APPLIED,
# not merely configured. The discriminating cell is the one that must be REFUSED — a TLS1.2 client
# against the default TLS1.3 floor. "TLS still works" cannot distinguish a floor that took from one
# that did nothing; only the refusal can. --tls-min12 cells prove the knob moves the floor (and the
# banner names the lowered floor, so rigs can gate the config too).
#
# 8 cells: {io-uring, epoll} x {default-floor, --tls-min12} x {tls1_3 client, tls1_2 client}.
# usage: bench/verify-tls-floor.sh
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXE="$REPO/AspNetDemo/bin/Release/net10.0/AspNetDemo"
[[ -x "$EXE" ]] || { echo "build AspNetDemo first"; exit 1; }
command -v openssl >/dev/null || { echo "needs openssl s_client"; exit 1; }

port=5480
pass=0; fail=0

# probe <port> <proto-flag> -> echoes ACCEPT or REFUSE
probe() {
  local out
  out=$(echo | timeout 10 openssl s_client -connect "127.0.0.1:$1" "$2" -brief 2>&1)
  if grep -qE "Protocol version:|New, TLSv" <<<"$out"; then echo ACCEPT; else echo REFUSE; fi
}

check() { # cell expected actual
  if [[ "$2" == "$3" ]]; then pass=$((pass+1)); printf "  %-38s PASS (%s)\n" "$1" "$3"
  else fail=$((fail+1)); printf "  %-38s FAIL (want %s, got %s)\n" "$1" "$2" "$3"; fi
}

for be in io-uring epoll; do
  for floor in default min12; do
    port=$((port+1))
    fargs=""; fbanner="tls=openssl"
    [[ "$floor" == min12 ]] && { fargs="--tls-min12"; fbanner="tls=openssl (min=tls12)"; }
    "$EXE" "--$be" --tls $fargs --port "$port" >/tmp/tlsfloor-$be-$floor.log 2>&1 &
    pid=$!
    for i in $(seq 1 100); do
      curl -sk --http1.1 --max-time 5 "https://127.0.0.1:$port/config" >/tmp/tlsfloor-cfg 2>/dev/null && break
      kill -0 $pid 2>/dev/null || break
      sleep 0.2
    done
    if ! grep -q "$fbanner" /tmp/tlsfloor-cfg 2>/dev/null; then
      fail=$((fail+1)); printf "  %-38s FAIL (banner missing '%s')\n" "$be/$floor" "$fbanner"
    else
      check "$be/$floor tls1_3 client" ACCEPT "$(probe $port -tls1_3)"
      if [[ "$floor" == default ]]; then
        # THE cell: the default TLS1.3 floor must REFUSE a TLS1.2 client
        check "$be/$floor tls1_2 client (REFUSAL)" REFUSE "$(probe $port -tls1_2)"
      else
        check "$be/$floor tls1_2 client" ACCEPT "$(probe $port -tls1_2)"
      fi
    fi
    kill $pid 2>/dev/null; wait $pid 2>/dev/null
    sleep 0.3
  done
done

echo
if [[ $fail -gt 0 ]]; then echo "$fail FAILED"; exit 1; fi
echo "all $pass cells PASS (including the refusal cells)"
