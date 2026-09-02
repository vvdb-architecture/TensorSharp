#!/usr/bin/env bash
# Installs and launches the simulator build of TensorAgent on an iOS simulator
# and streams the app's stdout (the loopback URL, the engine probe, request log)
# to this terminal. Build first with build-sim.sh.
#
# Env:
#   CONFIGURATION   Debug (default) | Release - must match build-sim.sh
#   SIM_UDID        simulator to use (default: iPhone 17 Pro, iOS 26.5 on this Mac)
#   SIM_NAME        used to look the UDID up when SIM_UDID is unset
#   TENSORAGENT_DEMO_PROMPT   Debug builds only: once the Web UI has loaded the app
#                   types this prompt into the composer and sends it, so the
#                   canned /api/chat stream renders on screen without anyone
#                   tapping the simulator (simctl cannot type into a WebView).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Debug}"
BUNDLE_ID="ai.tensorsharp.tensoragent"
# MAUI names the bundle after the assembly, not the ApplicationTitle.
APP="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/bin/${CONFIGURATION}/net10.0-ios/iossimulator-arm64/TensorAgent.Maui.app"

if [[ ! -d "${APP}" ]]; then
    echo "No bundle at ${APP}; run TensorAgent/scripts/build-sim.sh first." >&2
    exit 1
fi

SIM_NAME="${SIM_NAME:-iPhone 17 Pro}"
if [[ -z "${SIM_UDID:-}" ]]; then
    SIM_UDID="$(xcrun simctl list devices available | grep -F "${SIM_NAME} (" | head -1 | sed -E 's/.*\(([0-9A-F-]{36})\).*/\1/')"
fi
if [[ -z "${SIM_UDID}" ]]; then
    echo "No simulator named '${SIM_NAME}' found; set SIM_UDID or SIM_NAME." >&2
    exit 1
fi

# boot is a no-op error when already booted; ignore that one case.
xcrun simctl boot "${SIM_UDID}" 2>/dev/null || true
open -a Simulator
xcrun simctl bootstatus "${SIM_UDID}" -b >/dev/null

echo "==> Installing ${APP} on ${SIM_UDID}"
xcrun simctl install "${SIM_UDID}" "${APP}"

echo "==> Launching ${BUNDLE_ID} (stdout follows; the 'entry URL' line carries the token to curl /api from this Mac)"
# simctl forwards SIMCTL_CHILD_* variables to the app with the prefix stripped.
if [[ -n "${TENSORAGENT_DEMO_PROMPT:-}" ]]; then
    export SIMCTL_CHILD_TENSORAGENT_DEMO_PROMPT="${TENSORAGENT_DEMO_PROMPT}"
fi
exec xcrun simctl launch --console --terminate-running-process "${SIM_UDID}" "${BUNDLE_ID}"
