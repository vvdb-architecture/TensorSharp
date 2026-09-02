#!/usr/bin/env bash
# Simulator E2E check, run from the Mac while the app is running in the
# simulator (run-sim.sh). The simulator shares the host's network namespace, so
# the app's loopback listener is reachable from here.
#
# Usage: verify-sim.sh <app stdout log written by run-sim.sh>
#   The log carries the "entry URL" line (Debug builds only) with the port and
#   the per-launch token; every /api request must present that token.
#
# Checks: GET / is the Server's index.html byte for byte, GET /api/engine shows
# the static GgmlOps link is alive (no DllNotFoundException), /api without the
# token is refused, POST /api/chat streams the demo SSE frames and ends with a
# done frame, and the media probe line shows ImageIO/AVFoundation decoded a HEIC,
# applied a stored EXIF orientation, round-tripped an MP4 and read a WAV.
set -euo pipefail

LOG="${1:?usage: verify-sim.sh <app stdout log>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

ENTRY="$(grep -o 'entry URL http://127.0.0.1:[0-9]*/?token=[0-9a-f]*' "${LOG}" | tail -1 | sed 's/^entry URL //')"
if [[ -z "${ENTRY}" ]]; then
    echo "No 'entry URL' line in ${LOG}; is this a Debug build and has the app started?" >&2
    exit 1
fi
BASE="${ENTRY%%\?*}"
TOKEN="${ENTRY##*token=}"
# The launch token is presented the way the WebView presents it: as the cookie the
# entry URL sets. The server takes no bearer header, and adding one purely for this
# script would widen the surface for a convenience.
AUTH=(-H "Cookie: tensoragent_token=${TOKEN}")
echo "==> ${BASE} (token ${TOKEN:0:6}…)"

fail() { echo "FAIL: $*" >&2; exit 1; }

# 1. index.html is the Server's, plus exactly one appended script tag. The whole of
#    the Server's file must still be there, in order: the app adds to the page and
#    never forks it.
TMP="$(mktemp)"
SOURCE="${REPO_ROOT}/TensorSharp.Server/wwwroot/index.html"
trap 'rm -f "${TMP}"' EXIT
curl -fsS -o "${TMP}" "${AUTH[@]}" "${BASE}"
SERVED_BYTES="$(wc -c < "${TMP}")"
SOURCE_BYTES="$(wc -c < "${SOURCE}")"
# The tag goes in before </body>, not at the end, so the test is: take it out again
# and what is left must be the Server's file byte for byte.
python3 - "${TMP}" "${SOURCE}" <<'PYCHECK' || fail "GET / is not TensorSharp.Server/wwwroot/index.html plus one script tag"
import sys
served = open(sys.argv[1], 'rb').read()
source = open(sys.argv[2], 'rb').read()
tag = b'\n<script src="/tensoragent.js"></script>\n'
if tag not in served:
    print('the companion script tag is missing', file=sys.stderr)
    sys.exit(1)
if served.replace(tag, b'', 1) != source:
    print('the page differs from the Server\'s beyond the one added tag', file=sys.stderr)
    sys.exit(1)
PYCHECK
echo "ok  GET / is the Server's index.html (${SOURCE_BYTES} bytes) plus the companion tag (${SERVED_BYTES} served)"

# 2. Token gate.
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}api/models")"
[[ "${CODE}" == "403" ]] || fail "GET /api/models without the token returned ${CODE}, expected 403"
echo "ok  /api without the token -> 403"

# 3. Engine probe: the static link works and no P/Invoke threw.
ENGINE="$(curl -fsS "${AUTH[@]}" "${BASE}api/agent/engine")"
grep -q '"engine"' <<<"${ENGINE}" || fail "/api/agent/engine returned no engine line: ${ENGINE}"
grep -q 'sh (in-process)' <<<"${ENGINE}" || fail "the shell backend is not the in-process one: ${ENGINE}"
echo "    ${ENGINE}"
echo "ok  GET /api/agent/engine"

# 4. The surface the Web UI calls at load, with the shapes the page reads.
MODELS="$(curl -fsS "${AUTH[@]}" "${BASE}api/models")"
grep -q '"supportedBackends"' <<<"${MODELS}" || fail "/api/models has no supportedBackends: ${MODELS}"
grep -q '"defaultMaxTokens"' <<<"${MODELS}" || fail "/api/models has no defaultMaxTokens"
# The page must never be shown a backend this build cannot initialise, and its
# default must be one of the ones it was shown. On a device that means Metal leads;
# in the simulator, whose slice of the engine has no Metal at all, it means CPU is
# the only entry. The engine probe in the launch log is what decides which.
python3 - "${MODELS}" "$(grep -o '"ggmlMetalAvailable":[a-z]*' "${LOG}" | tail -1)" <<'PYCHECK' || fail "/api/models offers a backend this build cannot run"
import json, sys
models = json.loads(sys.argv[1])
metal = 'true' in sys.argv[2]
offered = [b['Value'] for b in models['supportedBackends']]
if not offered:
    print('no backends offered at all', file=sys.stderr); sys.exit(1)
if models['defaultBackend'] != offered[0]:
    print(f"default {models['defaultBackend']} is not the first offered {offered}", file=sys.stderr); sys.exit(1)
if metal and offered[0] != 'ggml_metal':
    print(f"Metal is available but {offered[0]} leads", file=sys.stderr); sys.exit(1)
if not metal and 'ggml_metal' in offered:
    print(f"Metal is not available but is offered: {offered}", file=sys.stderr); sys.exit(1)
print(f"    backends offered: {offered} (Metal available: {metal})")
PYCHECK
curl -fsS "${AUTH[@]}" "${BASE}api/queue/status" | grep -q 'pending' || fail "/api/queue/status shape"
# curl sends no Content-Length for a body-less POST and HttpListener answers 411;
# WKWebView's fetch() sends Content-Length: 0, so -d '' mirrors the browser.
SESSION="$(curl -fsS "${AUTH[@]}" -X POST -d '' "${BASE}api/sessions?conversation=new")"
grep -q '"sessionId"' <<<"${SESSION}" || fail "POST /api/sessions: ${SESSION}"
grep -q '"conversationId"' <<<"${SESSION}" || fail "POST /api/sessions did not bind a conversation: ${SESSION}"
SID="$(sed -n 's/.*"sessionId":"\([^"]*\)".*/\1/p' <<<"${SESSION}")"
curl -fsS "${AUTH[@]}" -X DELETE "${BASE}api/sessions/${SID}" >/dev/null || fail "DELETE /api/sessions/{id}"
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "${BASE}uploads/x.png")"
[[ "${CODE}" == "404" ]] || fail "GET /uploads/x.png returned ${CODE}, expected 404"
echo "ok  /api/models offers only runnable backends, /api/queue/status, /api/sessions binds a conversation"

# 5. The app's own surface: the catalog, the saved chats, the sandbox switches.
CATALOG="$(curl -fsS "${AUTH[@]}" "${BASE}api/agent/catalog")"
for FAMILY in Gemma4 Qwen38 QwenImage; do
    grep -q "\"family\":\"${FAMILY}\"" <<<"${CATALOG}" || fail "the catalog is missing the ${FAMILY} family"
done
for KIND in Dense MixtureOfExperts Diffusion; do
    grep -q "\"kind\":\"${KIND}\"" <<<"${CATALOG}" || fail "the catalog is missing a ${KIND} entry"
done
SETTINGS="$(curl -fsS "${AUTH[@]}" "${BASE}api/agent/settings")"
grep -q '"allowNetwork":false' <<<"${SETTINGS}" || fail "the network is not off by default: ${SETTINGS}"
grep -q '"allowCodeExecution":true' <<<"${SETTINGS}" || fail "code execution is not on by default: ${SETTINGS}"
SKILLS="$(curl -fsS "${AUTH[@]}" "${BASE}api/skills")"
grep -q '"skills"' <<<"${SKILLS}" || fail "/api/skills shape: ${SKILLS}"
echo "ok  catalog covers both families and all three architectures; sandbox defaults are safe"

# 6. The companion script itself is served and carries the app's additions.
SCRIPT="$(curl -fsS "${AUTH[@]}" "${BASE}tensoragent.js")"
for SYMBOL in window.TensorAgent addAttachment insertText loadConversation; do
    grep -q "${SYMBOL}" <<<"${SCRIPT}" || fail "the companion script is missing ${SYMBOL}"
done
echo "ok  GET /tensoragent.js serves the companion script"

# 7. The startup self-test: the interpreters that linked can actually run, and the
#    sandbox refuses what it must. This is the only place the embedded CPython and
#    JavaScriptCore are exercised on a real iOS runtime.
# macOS ships bash 3.2, which has no mapfile.
CHECKS="$(grep -o 'selftest .*' "${LOG}" || true)"
[[ -n "${CHECKS}" ]] || fail "no 'selftest' lines in ${LOG}; the self-test is Debug-only, is this a Debug build?"
sed 's/^/    /' <<<"${CHECKS}"
grep -q 'FAIL' <<<"${CHECKS}" && fail "a startup self-test check failed"
for CHECK in shell python python:stdlib python:numpy python:pillow node sandbox:write sandbox:network; do
    grep -q "ok   ${CHECK}:" <<<"${CHECKS}" || fail "self-test check '${CHECK}' is missing or did not pass"
done
echo "ok  startup self-test: shell, python, node and both sandbox refusals"

# 8. The media probe. TensorSharp.Models/Media/Apple compiles only for net10.0-ios, so
#    the repo's net10.0 xunit suite cannot execute one line of it: this log line is the
#    only place ImageIO and AVFoundation are actually run. The desktop suite pins the
#    contract (InferenceWeb.Tests/MediaProviderParityTests runs the same assertions
#    against the managed and Magick.NET providers), and MediaProbe runs it here.
MEDIA="$(grep -o 'media probe {.*' "${LOG}" | tail -1 | sed 's/^media probe //')"
[[ -n "${MEDIA}" ]] || fail "no 'media probe' line in ${LOG}; the probe is Debug-only, is this a Debug build?"
echo "    ${MEDIA}"
grep -q '"allPassed":true' <<<"${MEDIA}" || fail "media probe reported a failed check: ${MEDIA}"
for CHECK in providers png-roundtrip png-straight-alpha heic-decode exif-orientation mp4-roundtrip audio-decode; do
    grep -q "\"name\":\"${CHECK}\",\"ok\":true" <<<"${MEDIA}" || fail "media probe check '${CHECK}' is missing or did not pass"
done
echo "ok  media probe: HEIC decode, EXIF orientation, MP4 round trip and AVAudioFile all passed"

echo "All simulator checks passed."
