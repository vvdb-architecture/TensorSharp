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
AUTH=(-H "X-TensorAgent-Token: ${TOKEN}")
echo "==> ${BASE} (token ${TOKEN:0:6}…)"

fail() { echo "FAIL: $*" >&2; exit 1; }

# 1. index.html is the Server's, unmodified.
TMP="$(mktemp)"
trap 'rm -f "${TMP}"' EXIT
curl -fsS -o "${TMP}" "${BASE}"
cmp -s "${TMP}" "${REPO_ROOT}/TensorSharp.Server/wwwroot/index.html" || fail "GET / differs from TensorSharp.Server/wwwroot/index.html"
echo "ok  GET / is TensorSharp.Server/wwwroot/index.html ($(wc -c < "${TMP}") bytes)"

# 2. Token gate.
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}api/models")"
[[ "${CODE}" == "403" ]] || fail "GET /api/models without the token returned ${CODE}, expected 403"
echo "ok  /api without the token -> 403"

# 3. Engine probe: the static link works and no P/Invoke threw.
ENGINE="$(curl -fsS "${AUTH[@]}" "${BASE}api/engine")"
echo "    ${ENGINE}"
grep -q '"mainProgramHandleResolved":true' <<<"${ENGINE}" || fail "TSGgml_* not resolvable from the main program image"
grep -q '"cpu":true' <<<"${ENGINE}" || fail "ggml CPU backend not available"
grep -q '"ggmlVersionOrError":"ok:' <<<"${ENGINE}" || fail "engine probe reported an error"
echo "ok  GET /api/engine: GgmlOps linked, ggml cpu available"

# 4. The rest of the stub surface the Web UI calls at load.
curl -fsS "${AUTH[@]}" "${BASE}api/models" | grep -q '"defaultMaxTokens":4096' || fail "/api/models shape"
curl -fsS "${AUTH[@]}" "${BASE}api/queue/status" | grep -q '"pending_requests":0' || fail "/api/queue/status shape"
# curl sends no Content-Length for a body-less POST and HttpListener answers 411;
# WKWebView's fetch() sends Content-Length: 0, so -d '' mirrors the browser.
curl -fsS "${AUTH[@]}" -X POST -d '' "${BASE}api/sessions" | grep -q '"sessionId"' || fail "POST /api/sessions"
curl -fsS "${AUTH[@]}" -X DELETE "${BASE}api/sessions/x" | grep -q '"ok":true' || fail "DELETE /api/sessions/{id}"
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}uploads/x.png")"
[[ "${CODE}" == "404" ]] || fail "GET /uploads/x.png returned ${CODE}, expected 404"
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}images/assistant_logo.png")"
[[ "${CODE}" == "200" ]] || fail "GET /images/assistant_logo.png returned ${CODE}"
echo "ok  /api/models, /api/queue/status, /api/sessions, /uploads (404), /images"

# 5. The demo SSE round trip.
STREAM="$(curl -fsS -N --max-time 30 "${AUTH[@]}" -H 'Content-Type: application/json' \
    -d '{"messages":[{"role":"user","content":"ping from verify-sim.sh"}],"maxTokens":4096,"think":false}' \
    "${BASE}api/chat")"
TOKENS="$(grep -c '^data: {"token":' <<<"${STREAM}" || true)"
[[ "${TOKENS}" -gt 10 ]] || fail "POST /api/chat streamed ${TOKENS} token frames"
grep -q '^data: {"done":true' <<<"${STREAM}" || fail "POST /api/chat did not end with a done frame"
echo "ok  POST /api/chat streamed ${TOKENS} token frames and a done frame"

# 6. The media probe. TensorSharp.Models/Media/Apple compiles only for net10.0-ios, so
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
