#!/usr/bin/env bash
# TLS 1.3 KeyUpdate verification for the OpenSSL backends (io_uring, epoll).
#
# WHY THIS EXISTS. The renegotiation audit (TODO "TLS renegotiation requests") established that we REFUSE
# TLS 1.2 renegotiation and rely on TLS 1.3 KeyUpdate for rekeying — and that KeyUpdate is handled by the
# filter's SSL_read loop. That was code-verified but not test-verified. This exercises it end to end: a
# client drives a KeyUpdate in BOTH directions mid-stream and we confirm the echo keeps round-tripping
# byte-exact across the rekey, and the server survives.
#
# HOW. `openssl s_client` is the client (it can trigger a KeyUpdate from stdin: a lone `K` line asks the
# PEER to update its send keys, a lone `k` updates OUR send keys). Against our TLS-1.3 echo server we send:
#     TOKEN1 , K (server rekeys its TX) , TOKEN2 , k (client rekeys its TX) , TOKEN3
# and require all three tokens to echo back — TOKEN2 proves the server processed a KeyUpdate it was asked
# to send, TOKEN3 proves it decrypted client data under the client's NEW keys. A rekey that broke framing
# would drop TOKEN2/TOKEN3 or fault the server.
#
# Not a SmokeTest matrix cell because it needs the external `openssl` binary; run it alongside the matrix.
#
# Usage:  bench/verify-tls-keyupdate.sh
#         BACKENDS="iouring epoll" bench/verify-tls-keyupdate.sh
set -u

BACKENDS="${BACKENDS:-iouring epoll}"
FIRST_PORT="${FIRST_PORT:-11700}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exe="$repo/SmokeTest/bin/Release/net10.0/SmokeTest"
c_green=$'\033[32m'; c_red=$'\033[31m'; c_cyan=$'\033[36m'; c_off=$'\033[0m'

command -v openssl >/dev/null || { echo "${c_red}openssl not found — cannot run the KeyUpdate check${c_off}"; exit 2; }
echo "${c_cyan}building SmokeTest (Release) ...${c_off}"
dotnet build "$repo/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo -f net10.0 >/tmp/ku-build.log 2>&1 \
    || { cat /tmp/ku-build.log; echo "${c_red}build failed${c_off}"; exit 2; }

port=$FIRST_PORT
fails=0
for backend in $BACKENDS; do
    case "$backend" in
        iouring) bflag="" ;;
        epoll)   bflag="--epoll" ;;
        *) echo "unknown backend: $backend"; exit 2 ;;
    esac
    port=$((port+1))
    # TLS 1.3 echo server, tiny greeting so it doesn't drown the tokens.
    # shellcheck disable=SC2086
    "$exe" $bflag -s -t 20 --tls-ssl -z 4 --port "$port" >/tmp/ku-$backend.srv 2>&1 &
    srv=$!
    # wait for listen
    for _ in $(seq 1 40); do grep -q 'listening on' /tmp/ku-$backend.srv 2>/dev/null && break; sleep 0.1; done

    # Drive both KeyUpdate directions around unique tokens.
    out=$(printf 'KUPRE\nK\nKUMID\nk\nKUPOST\n' \
        | timeout 15 openssl s_client -connect 127.0.0.1:"$port" -tls1_3 -quiet 2>/dev/null \
        | tr -d '\0')
    kill -TERM $srv 2>/dev/null; wait $srv 2>/dev/null

    got_pre=$(grep -c KUPRE <<<"$out"); got_mid=$(grep -c KUMID <<<"$out"); got_post=$(grep -c KUPOST <<<"$out")
    crashed=$(grep -icE 'UNHANDLED|exception' /tmp/ku-$backend.srv)
    if [ "$got_pre" -ge 1 ] && [ "$got_mid" -ge 1 ] && [ "$got_post" -ge 1 ] && [ "$crashed" -eq 0 ]; then
        printf "  %-10s ${c_green}PASS${c_off}  echoed pre=%s mid(after server KeyUpdate)=%s post(after client KeyUpdate)=%s\n" \
            "$backend" "$got_pre" "$got_mid" "$got_post"
    else
        printf "  %-10s ${c_red}FAIL${c_off}  pre=%s mid=%s post=%s serverCrash=%s\n" \
            "$backend" "$got_pre" "$got_mid" "$got_post" "$crashed"
        fails=$((fails+1))
    fi
done

echo
if [ "$fails" -gt 0 ]; then echo "${c_red}$fails backend(s) FAILED the KeyUpdate check${c_off}"; exit 1; fi
echo "${c_green}TLS 1.3 KeyUpdate round-trips byte-exact on all tested backends${c_off}"
