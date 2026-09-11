#!/usr/bin/env bash
# Plain vs speculative decoding, measured ON THE PHONE.
#
# Deploys the app (Release by default, so the numbers are the shipping ones), launches
# it with the on-device benchmark switched on (SpeculationBench: the same four turns
# under plain, then speculative decoding, twice each, switching the engine's policy in
# place), waits for it to finish, pulls the log back and prints the summary.
#
#   TENSORAGENT_USE_MODEL=gemma-4-e2b-q8 bash TensorAgent/scripts/bench-spec-device.sh
#
# Environment:
#   TENSORAGENT_USE_MODEL   the catalog id to load (required: the remembered model may be none)
#                           (honoured by a Release build only while TENSORAGENT_SPEC_BENCH=1,
#                           which this script sets; a plain Release launch ignores it)
#   CONFIGURATION           Release (default) | Debug
#   SKIP_DEPLOY=1           the installed build is current; only launch and collect
#   BENCH_TOKENS            answer budget for the long turns (default 160)
#   BENCH_MODES             comma list of passes (default plain,spec,plain,spec)
#   WAIT_SECONDS            how long to wait for the benchmark (default 1500)
#   DEVICE_ID               the phone, when more than one is connected
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUNDLE_ID="ai.tensorsharp.tensoragent"
CONFIGURATION="${CONFIGURATION:-Release}"
WAIT_SECONDS="${WAIT_SECONDS:-1500}"
RUN_ID="$(date '+%Y%m%d-%H%M%S')-$$"
OUT="${OUT:-${TMPDIR:-/tmp}/tensoragent-specbench.${RUN_ID}}"
mkdir -p "${OUT}"
fail() { echo "bench-spec-device: $*" >&2; exit 1; }

[[ -n "${TENSORAGENT_USE_MODEL:-}" ]] || fail "set TENSORAGENT_USE_MODEL=<catalog id> (the model must already be on the phone)"

if [[ -z "${DEVICE_ID:-}" ]]; then
    xcrun devicectl --quiet --timeout 30 --json-output "${OUT}/devices.json" list devices \
        --filter "hardwareProperties.platform == 'iOS' AND hardwareProperties.reality == 'physical' AND connectionProperties.tunnelState == 'connected'" \
        >/dev/null 2>&1 || true
    [[ "$(plutil -extract result.devices raw -o - "${OUT}/devices.json" 2>/dev/null || echo 0)" == "1" ]] \
        || fail "connect exactly one iPhone (unlocked) or set DEVICE_ID"
    DEVICE_ID="$(plutil -extract result.devices.0.hardwareProperties.udid raw -o - "${OUT}/devices.json")"
fi

if [[ "${SKIP_DEPLOY:-0}" != "1" ]]; then
    echo "==> Deploying ${CONFIGURATION} build to ${DEVICE_ID} (app data preserved)"
    CONFIGURATION="${CONFIGURATION}" SKIP_LAUNCH=1 DEVICE_ID="${DEVICE_ID}" bash "${SCRIPT_DIR}/deploy-device.sh"
fi

ENV_JSON="{\"TENSORAGENT_SPEC_BENCH\":\"1\",\"TENSORAGENT_SPEC_BENCH_RUN\":\"${RUN_ID}\",\"TENSORAGENT_USE_MODEL\":\"${TENSORAGENT_USE_MODEL}\""
[[ -n "${BENCH_TOKENS:-}" ]] && ENV_JSON+=",\"TENSORAGENT_SPEC_BENCH_TOKENS\":\"${BENCH_TOKENS}\""
[[ -n "${BENCH_MODES:-}" ]] && ENV_JSON+=",\"TENSORAGENT_SPEC_BENCH_MODES\":\"${BENCH_MODES}\""
ENV_JSON+="}"

echo "==> Launching ${BUNDLE_ID} with the benchmark on (the phone must be unlocked and stay in front)"
xcrun devicectl --timeout 60 device process launch --terminate-existing \
    --environment-variables "${ENV_JSON}" --device "${DEVICE_ID}" "${BUNDLE_ID}" > "${OUT}/launch.log" 2>&1 \
    || { cat "${OUT}/launch.log" >&2; fail "launch failed (locked phone?)"; }

pull_log() {
    xcrun devicectl --quiet --timeout 60 device copy from --device "${DEVICE_ID}" \
        --domain-type appDataContainer --domain-identifier "${BUNDLE_ID}" \
        --source "Library/Caches/TensorAgent/logs/specbench.log" --destination "${OUT}/specbench.log" >/dev/null 2>&1 || true
}

echo "==> Waiting for the benchmark (up to ${WAIT_SECONDS}s); run ${RUN_ID}"
deadline=$(( $(date +%s) + WAIT_SECONDS ))
while (( $(date +%s) < deadline )); do
    sleep 20
    pull_log
    if [[ -f "${OUT}/specbench.log" ]] && grep -q "specbench run ${RUN_ID}" "${OUT}/specbench.log" \
        && sed -n "/specbench run ${RUN_ID}/,\$p" "${OUT}/specbench.log" | grep -qE "specbench (done|FAIL|cancelled)"; then
        break
    fi
done
[[ -f "${OUT}/specbench.log" ]] || fail "no specbench.log came back; is the model installed and the phone unlocked?"

echo "==> Results (${OUT}/specbench.log)"
sed -n "/specbench run ${RUN_ID}/,\$p" "${OUT}/specbench.log" | sed -E 's/^[0-9-]+ [0-9:.]+ //'
