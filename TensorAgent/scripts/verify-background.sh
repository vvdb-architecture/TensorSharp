#!/usr/bin/env bash
# The one check that needs a real lifecycle transition: a generation that survives the
# app being sent to the background and brought back.
#
# iOS refuses GPU work from an app that is not frontmost, and ggml-metal answers a
# refused command buffer by latching an error that only recreating the backend clears
# -- so leaving TensorAgent mid-answer used to fail that answer AND every answer after
# it. Nothing off a device shows the refusal, and no unit test can fire the UIKit
# notifications the fix is driven by. What this script can do is the transition itself:
# launch a Debug build with TENSORAGENT_BACKGROUND_CHECK=1 (the app asks its own model
# for a long answer and traces what happens, see MainPage.RunBackgroundProbeAsync),
# bring ANOTHER app to the front once tokens are flowing, wait, bring TensorAgent back,
# and read the trace.
#
# On a SIMULATOR there is no Metal, so the refusal cannot occur; what is proved there is
# the gate -- the engine's step count stops while the app is away and the answer finishes
# afterwards. On a DEVICE the same run also proves recovery: whether a step was in flight
# when the GPU went away (which poisons the backend) or not, the answer must finish and
# the next one must work.
#
# Usage:
#   verify-background.sh sim     [seconds-away]   # the booted simulator, Debug build
#   verify-background.sh device  [seconds-away]   # the connected iPhone, Debug build
#
# Env:
#   TENSORAGENT_USE_MODEL   catalog id to load (default: the remembered choice)
#   AWAY_SECONDS            how long to stay away (default: 20)
#   LEAVE_DURING            decode (default): leave once tokens are flowing.
#                           prefill: leave the moment the answer is asked for, with a
#                           long prompt, so a prefill chunk is in flight when the GPU
#                           goes away -- the case that poisons the backend and needs
#                           the rebuild. On a device this is the recovery test.
#   OTHER_APP               what to bring to the front instead (default: Settings)
#   DEVICE_ID / SIM_UDID    as deploy-device.sh / run-sim.sh
#   SKIP_INSTALL=1          the Debug build is already installed; launch only
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
TARGET="${1:?usage: verify-background.sh sim|device [seconds-away]}"
AWAY_SECONDS="${2:-${AWAY_SECONDS:-20}}"
OTHER_APP="${OTHER_APP:-com.apple.Preferences}"
LEAVE_DURING="${LEAVE_DURING:-decode}"
if [[ "${LEAVE_DURING}" == "prefill" && -z "${TENSORAGENT_BACKGROUND_PROMPT:-}" ]]; then
    # Long enough that the prefill takes several seconds even with the shared prompt
    # cached: about 3,000 tokens of text the model has to read before it can answer.
    TENSORAGENT_BACKGROUND_PROMPT="$(printf 'The quick brown fox jumps over the lazy dog near the old stone bridge while the river runs quietly below. %.0s' $(seq 1 180))Summarise the passage above in three sentences, then count from one to one hundred in words, one per line."
    export TENSORAGENT_BACKGROUND_PROMPT
fi
BUNDLE_ID="ai.tensorsharp.tensoragent"
LOGS="${TMPDIR:-/tmp}/tensoragent-bgcheck.$$"
mkdir -p "${LOGS}"
CONSOLE="${LOGS}/console.log"
# Stamped into the trace by the probe as its first line, so this run's lines can be
# told from the previous launches' without comparing the Mac's clock to the phone's.
RUN_ID="$(date '+%Y%m%d-%H%M%S')-$$"

fail() { echo "verify-background: $*" >&2; exit 1; }

case "${TARGET}" in
    sim)
        APP="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/bin/Debug/net10.0-ios/iossimulator-arm64/TensorAgent.Maui.app"
        [[ -d "${APP}" ]] || fail "no Debug simulator bundle at ${APP}; run build-sim.sh"
        SIM_NAME="${SIM_NAME:-iPhone 17 Pro}"
        if [[ -z "${SIM_UDID:-}" ]]; then
            SIM_UDID="$(xcrun simctl list devices available | grep -F "${SIM_NAME} (" | head -1 | sed -E 's/.*\(([0-9A-F-]{36})\).*/\1/')"
        fi
        [[ -n "${SIM_UDID}" ]] || fail "no simulator named '${SIM_NAME}'"
        xcrun simctl boot "${SIM_UDID}" 2>/dev/null || true
        xcrun simctl bootstatus "${SIM_UDID}" -b >/dev/null
        if [[ "${SKIP_INSTALL:-0}" != "1" ]]; then
            echo "==> Installing ${APP}"
            xcrun simctl install "${SIM_UDID}" "${APP}"
        fi
        xcrun simctl terminate "${SIM_UDID}" "${BUNDLE_ID}" 2>/dev/null || true

        export SIMCTL_CHILD_TENSORAGENT_BACKGROUND_CHECK=1
        export SIMCTL_CHILD_TENSORAGENT_BACKGROUND_RUN="${RUN_ID}"
        [[ -n "${TENSORAGENT_USE_MODEL:-}" ]] && export SIMCTL_CHILD_TENSORAGENT_USE_MODEL="${TENSORAGENT_USE_MODEL}"
        [[ -n "${TENSORAGENT_BACKGROUND_PROMPT:-}" ]] && export SIMCTL_CHILD_TENSORAGENT_BACKGROUND_PROMPT="${TENSORAGENT_BACKGROUND_PROMPT}"
        [[ -n "${TENSORAGENT_BACKGROUND_TOKENS:-}" ]] && export SIMCTL_CHILD_TENSORAGENT_BACKGROUND_TOKENS="${TENSORAGENT_BACKGROUND_TOKENS}"
        echo "==> Launching ${BUNDLE_ID} with the background check on"
        xcrun simctl launch --console-pty "${SIM_UDID}" "${BUNDLE_ID}" > "${CONSOLE}" 2>&1 &
        CONSOLE_PID=$!

        send_away() { xcrun simctl launch "${SIM_UDID}" "${OTHER_APP}" >/dev/null; }
        bring_back() { xcrun simctl launch "${SIM_UDID}" "${BUNDLE_ID}" >/dev/null; }
        trace_file() {
            local container
            container="$(xcrun simctl get_app_container "${SIM_UDID}" "${BUNDLE_ID}" data)"
            echo "${container}/Library/Caches/TensorAgent/logs/background.log"
        }
        read_trace() { cat "$(trace_file)" 2>/dev/null || true; }
        ;;
    device)
        APP="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/bin/Debug/net10.0-ios/ios-arm64/TensorAgent.Maui.app"
        [[ -d "${APP}" ]] || fail "no Debug device bundle at ${APP}; run CONFIGURATION=Debug build-device.sh"
        if [[ -z "${DEVICE_ID:-}" ]]; then
            # Apple's supported scripting interface is the JSON output; the table's
            # columns are a device NAME with spaces in it, so no awk on it is safe.
            xcrun devicectl --quiet --timeout 30 --json-output "${LOGS}/devices.json" list devices \
                --filter "hardwareProperties.platform == 'iOS' AND hardwareProperties.reality == 'physical' AND connectionProperties.tunnelState == 'connected'" \
                >/dev/null 2>&1 || true
            DEVICE_ID="$(plutil -extract result.devices.0.hardwareProperties.udid raw -o - "${LOGS}/devices.json" 2>/dev/null || true)"
            [[ "$(plutil -extract result.devices raw -o - "${LOGS}/devices.json" 2>/dev/null || echo 0)" == "1" ]] \
                || fail "connect exactly one iPhone or set DEVICE_ID"
        fi
        [[ -n "${DEVICE_ID:-}" ]] || fail "no connected iPhone; set DEVICE_ID"
        if [[ "${SKIP_INSTALL:-0}" != "1" ]]; then
            echo "==> Installing ${APP} on ${DEVICE_ID} (app data is preserved)"
            xcrun devicectl --timeout 300 device install app --device "${DEVICE_ID}" "${APP}" >/dev/null
        fi

        ENV_JSON="{\"TENSORAGENT_BACKGROUND_CHECK\":\"1\",\"TENSORAGENT_BACKGROUND_RUN\":\"${RUN_ID}\""
        [[ -n "${TENSORAGENT_USE_MODEL:-}" ]] && ENV_JSON+=",\"TENSORAGENT_USE_MODEL\":\"${TENSORAGENT_USE_MODEL}\""
        [[ -n "${TENSORAGENT_BACKGROUND_PROMPT:-}" ]] && ENV_JSON+=",\"TENSORAGENT_BACKGROUND_PROMPT\":$(python3 -c 'import json,os; print(json.dumps(os.environ["TENSORAGENT_BACKGROUND_PROMPT"]))')"
        [[ -n "${TENSORAGENT_BACKGROUND_TOKENS:-}" ]] && ENV_JSON+=",\"TENSORAGENT_BACKGROUND_TOKENS\":\"${TENSORAGENT_BACKGROUND_TOKENS}\""
        ENV_JSON+="}"
        echo "==> Launching ${BUNDLE_ID} with the background check on (the phone must be unlocked)"
        # NOT --console: a console-attached launch owns the app, and ends it when the
        # console goes away -- which is exactly what sending the app to the background
        # would do to this script's evidence. The trace file is the record instead.
        xcrun devicectl --timeout 60 device process launch --terminate-existing \
            --environment-variables "${ENV_JSON}" --device "${DEVICE_ID}" "${BUNDLE_ID}" > "${CONSOLE}" 2>&1
        CONSOLE_PID=""

        send_away() { xcrun devicectl --timeout 60 device process launch --device "${DEVICE_ID}" "${OTHER_APP}" >/dev/null 2>&1; }
        bring_back() { xcrun devicectl --timeout 60 device process launch --device "${DEVICE_ID}" "${BUNDLE_ID}" >/dev/null 2>&1; }
        read_trace() {
            rm -f "${LOGS}/device-background.log"
            xcrun devicectl --timeout 60 device copy from --device "${DEVICE_ID}" \
                --domain-type appDataContainer --domain-identifier "${BUNDLE_ID}" \
                --source Library/Caches/TensorAgent/logs/background.log \
                --destination "${LOGS}/device-background.log" >/dev/null 2>&1 || true
            cat "${LOGS}/device-background.log" 2>/dev/null || true
        }
        ;;
    *)
        fail "unknown target '${TARGET}'; use sim or device"
        ;;
esac

cleanup() { [[ -n "${CONSOLE_PID:-}" ]] && kill "${CONSOLE_PID}" 2>/dev/null || true; }
trap cleanup EXIT

# The probe starts at the marker line below and heartbeats every few seconds. Wait for
# tokens to actually be flowing before leaving: leaving during the prefill is the
# harder case and is exercised separately (a prefill step in flight when the GPU goes
# away is the one that poisons the backend), but the FIRST thing to prove is the
# ordinary one.
since_launch() { read_trace | awk -v run="bgcheck run ${RUN_ID}" 'found { print } index($0, run) { found = 1 }'; }

if [[ "${LEAVE_DURING}" == "prefill" ]]; then
    STARTED='bgcheck asking for a long answer'
    echo "==> Waiting for the model to load and the answer to be ASKED FOR (up to 10 minutes)"
else
    STARTED='bgcheck [1-9][0-9]* tokens after'
    echo "==> Waiting for the model to load and the answer to start (up to 10 minutes)"
fi
for _ in $(seq 1 1200); do
    if since_launch | grep -qE "${STARTED}"; then break; fi
    if since_launch | grep -q 'bgcheck FAIL'; then since_launch | sed 's/^/    /'; fail "the probe failed before the app was sent away"; fi
    sleep 0.5
done
since_launch | grep -qE "${STARTED}" || { since_launch | sed 's/^/    /'; fail "the answer never started within 10 minutes"; }

echo "==> Sending ${BUNDLE_ID} to the background for ${AWAY_SECONDS}s (bringing ${OTHER_APP} to the front)"
send_away
sleep "${AWAY_SECONDS}"
echo "==> Bringing ${BUNDLE_ID} back"
bring_back

echo "==> Waiting for the probe to finish (up to 15 minutes)"
for _ in $(seq 1 900); do
    if since_launch | grep -q 'bgcheck done'; then break; fi
    sleep 1
done

echo
echo "==> background.log for this run (console: ${CONSOLE}; trace copy: ${LOGS}/background.log):"
since_launch | tee "${LOGS}/background.log" | sed 's/^/    /'
echo

TRACE="$(since_launch)"
grep -q 'bgcheck done' <<<"${TRACE}" || fail "the probe never finished"
grep -q 'bgcheck FAIL' <<<"${TRACE}" && fail "the probe reported a failure"
grep -q 'leaving the foreground' <<<"${TRACE}" || fail "the app was never sent to the background (no resign-active in the trace)"
grep -q 'back in front' <<<"${TRACE}" || fail "the app never came back to the front"
grep -qE 'bgcheck ok [0-9]+ tokens' <<<"${TRACE}" || fail "the long answer did not finish"
grep -qE 'paused [1-9]' <<<"${TRACE}" || fail "the turn was never paused while the app was away"
# Held by the gate, OR the step in flight was refused and the engine was rebuilt (a new
# engine's counters start at zero, so a rebuilt turn legitimately reports held 0).
grep -qE 'engine held [1-9]|rebuilding the GPU backend' <<<"${TRACE}" \
    || fail "the engine's step loop was never held by the gate and nothing was rebuilt"
grep -q 'the next answer after coming back worked' <<<"${TRACE}" || fail "the next answer after coming back failed"

if grep -q 'rebuilding the GPU backend' <<<"${TRACE}"; then
    echo "ok  a step was in flight when the GPU went away; the backend was rebuilt and the answer carried on"
else
    echo "ok  the model stopped cleanly while the app was away and carried on when it came back"
fi
echo "ok  the next answer after coming back worked"
