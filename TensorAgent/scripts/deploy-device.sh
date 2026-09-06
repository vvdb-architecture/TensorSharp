#!/usr/bin/env bash
# Builds, signs, installs, and launches a Release build of TensorAgent on a
# connected physical iPhone. Existing app data is preserved: devicectl installs
# the new bundle over the old one and this script never uninstalls the app.
#
# Defaults are deliberately strict. A single connected physical iOS device and
# a single Apple Development identity can be selected automatically; ambiguity
# is an error rather than a reason to deploy to an arbitrary phone.
#
# Env overrides:
#   DEVICE_ID             CoreDevice identifier, hardware UDID, or exact name
#   CODESIGN_KEY          Apple Development identity (auto-selected if unique)
#   CODESIGN_PROVISION    development profile name or UUID (auto-selected)
#   SKIP_LAUNCH=1         install the app without launching it
#   DEVICECTL_TIMEOUT     install timeout in seconds (default: 300)
#   TENSORAGENT_REBUILD_XCFRAMEWORK=0  reuse the existing native xcframework;
#                         the default is 1 so Release includes current sources
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
APP="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/bin/Release/net10.0-ios/ios-arm64/TensorAgent.Maui.app"
BUNDLE_ID="ai.tensorsharp.tensoragent"
ENTITLEMENTS="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/Platforms/iOS/Entitlements.plist"
DEVICECTL_TIMEOUT="${DEVICECTL_TIMEOUT:-300}"
TENSORAGENT_REBUILD_XCFRAMEWORK="${TENSORAGENT_REBUILD_XCFRAMEWORK:-1}"
REQUESTED_DEVICE="${DEVICE_ID:-}"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/tensoragent-deploy.XXXXXX")"

cleanup() {
    # TEMP_DIR is a resolved directory created by mktemp above. Avoid a recursive
    # broad delete: this script creates only flat temporary files here.
    find "${TEMP_DIR}" -type f -delete 2>/dev/null || true
    rmdir "${TEMP_DIR}" 2>/dev/null || true
}
trap cleanup EXIT

fail() {
    echo "deploy-device: $*" >&2
    exit 1
}

plist_value() { # file key-path
    plutil -extract "$2" raw -o - "$1" 2>/dev/null || true
}

require_tool() {
    command -v "$1" >/dev/null 2>&1 || fail "required tool '$1' was not found"
}

for TOOL in xcrun plutil security codesign nm base64 shasum; do
    require_tool "${TOOL}"
done
xcrun --find devicectl >/dev/null 2>&1 || fail "devicectl is unavailable; install/select a current Xcode"

[[ "${DEVICECTL_TIMEOUT}" =~ ^[1-9][0-9]*$ ]] || fail "DEVICECTL_TIMEOUT must be a positive integer"

# Apple's supported scripting interface for devicectl is its versioned JSON
# output. Restrict discovery to physical iOS devices with an active CoreDevice
# tunnel; paired-but-offline phones and watches are not deployment candidates.
DEVICES_JSON="${TEMP_DIR}/devices.json"
xcrun devicectl --quiet --timeout 30 --json-output "${DEVICES_JSON}" \
    list devices \
    --filter "hardwareProperties.platform == 'iOS' AND hardwareProperties.reality == 'physical' AND connectionProperties.tunnelState == 'connected'"

DEVICE_COUNT="$(plist_value "${DEVICES_JSON}" result.devices)"
[[ "${DEVICE_COUNT}" =~ ^[0-9]+$ ]] || fail "could not read devicectl's device list"

print_devices() {
    local index=0 name identifier udid
    while [[ "${index}" -lt "${DEVICE_COUNT}" ]]; do
        name="$(plist_value "${DEVICES_JSON}" "result.devices.${index}.deviceProperties.name")"
        identifier="$(plist_value "${DEVICES_JSON}" "result.devices.${index}.identifier")"
        udid="$(plist_value "${DEVICES_JSON}" "result.devices.${index}.hardwareProperties.udid")"
        printf '  %s (identifier %s, UDID %s)\n' "${name:-unknown}" "${identifier:-unknown}" "${udid:-unknown}" >&2
        index=$((index + 1))
    done
}

SELECTED_INDEX=""
if [[ -n "${REQUESTED_DEVICE}" ]]; then
    INDEX=0
    while [[ "${INDEX}" -lt "${DEVICE_COUNT}" ]]; do
        CANDIDATE_IDENTIFIER="$(plist_value "${DEVICES_JSON}" "result.devices.${INDEX}.identifier")"
        CANDIDATE_UDID="$(plist_value "${DEVICES_JSON}" "result.devices.${INDEX}.hardwareProperties.udid")"
        CANDIDATE_NAME="$(plist_value "${DEVICES_JSON}" "result.devices.${INDEX}.deviceProperties.name")"
        if [[ "${REQUESTED_DEVICE}" == "${CANDIDATE_IDENTIFIER}" ||
              "${REQUESTED_DEVICE}" == "${CANDIDATE_UDID}" ||
              "${REQUESTED_DEVICE}" == "${CANDIDATE_NAME}" ]]; then
            [[ -z "${SELECTED_INDEX}" ]] || fail "DEVICE_ID '${REQUESTED_DEVICE}' matches more than one connected device"
            SELECTED_INDEX="${INDEX}"
        fi
        INDEX=$((INDEX + 1))
    done
    if [[ -z "${SELECTED_INDEX}" ]]; then
        echo "Connected physical iOS devices:" >&2
        print_devices
        fail "DEVICE_ID '${REQUESTED_DEVICE}' is not a connected physical iOS device"
    fi
else
    if [[ "${DEVICE_COUNT}" -ne 1 ]]; then
        echo "Connected physical iOS devices:" >&2
        print_devices
        fail "found ${DEVICE_COUNT}; connect exactly one or set DEVICE_ID"
    fi
    SELECTED_INDEX=0
fi

DEVICE_NAME="$(plist_value "${DEVICES_JSON}" "result.devices.${SELECTED_INDEX}.deviceProperties.name")"
DEVICE_UDID="$(plist_value "${DEVICES_JSON}" "result.devices.${SELECTED_INDEX}.hardwareProperties.udid")"
DEVICE_IDENTIFIER="$(plist_value "${DEVICES_JSON}" "result.devices.${SELECTED_INDEX}.identifier")"
DEVELOPER_MODE="$(plist_value "${DEVICES_JSON}" "result.devices.${SELECTED_INDEX}.deviceProperties.developerModeStatus")"
DDI_AVAILABLE="$(plist_value "${DEVICES_JSON}" "result.devices.${SELECTED_INDEX}.deviceProperties.ddiServicesAvailable")"
DEVICE_ID="${DEVICE_UDID:-${DEVICE_IDENTIFIER}}"

[[ -n "${DEVICE_ID}" ]] || fail "the selected device has no usable identifier"
[[ "${DEVELOPER_MODE}" == "enabled" ]] || fail "Developer Mode is not enabled on '${DEVICE_NAME}'"
[[ "${DDI_AVAILABLE}" == "true" ]] || fail "developer services are unavailable on '${DEVICE_NAME}'; unlock it and reconnect"

# Select a development identity, never a distribution identity. Keep its SHA-1
# alongside the display name so automatic profile selection can prove that the
# certificate embedded in the profile is the certificate we will sign with.
DEVELOPMENT_IDENTITIES="$(security find-identity -v -p codesigning 2>/dev/null | \
    awk '/"Apple Development:/ {
        hash = $2
        name = $0
        sub(/^[^"]*"/, "", name)
        sub(/"[^"]*$/, "", name)
        print hash "\t" name
    }')"
if [[ -z "${CODESIGN_KEY:-}" ]]; then
    IDENTITY_COUNT="$(printf '%s\n' "${DEVELOPMENT_IDENTITIES}" | awk 'NF { count++ } END { print count + 0 }')"
    if [[ "${IDENTITY_COUNT}" -ne 1 ]]; then
        echo "Available Apple Development identities:" >&2
        printf '%s\n' "${DEVELOPMENT_IDENTITIES:-  (none)}" >&2
        fail "found ${IDENTITY_COUNT}; set CODESIGN_KEY to the identity to use"
    fi
    CODESIGN_IDENTITY_HASH="$(printf '%s\n' "${DEVELOPMENT_IDENTITIES}" | awk -F '\t' 'NF { print $1; exit }')"
    CODESIGN_KEY="$(printf '%s\n' "${DEVELOPMENT_IDENTITIES}" | awk -F '\t' 'NF { print $2; exit }')"
else
    CODESIGN_IDENTITY_HASH="$(printf '%s\n' "${DEVELOPMENT_IDENTITIES}" | \
        awk -F '\t' -v requested="${CODESIGN_KEY}" '$1 == requested || $2 == requested { print $1; exit }')"
    [[ -n "${CODESIGN_IDENTITY_HASH}" ]] || fail "CODESIGN_KEY does not identify a valid Apple Development certificate"
fi

# Find the newest unexpired development profile that is exact for this bundle,
# contains the selected phone, and grants every explicit app entitlement. Xcode
# can choose profiles implicitly, but doing it here keeps unattended deployments
# deterministic and gives a useful failure before a long Release/AOT build.
if [[ -z "${CODESIGN_PROVISION:-}" ]]; then
    PROFILE_PLIST="${TEMP_DIR}/profile.plist"
    PROFILE_CERTIFICATE="${TEMP_DIR}/profile-certificate.der"
    NOW_UTC="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    BEST_PROFILE_UUID=""
    BEST_PROFILE_NAME=""
    BEST_PROFILE_EXPIRY=""
    shopt -s nullglob
    PROFILE_FILES=(
        "${HOME}/Library/MobileDevice/Provisioning Profiles"/*.mobileprovision
        "${HOME}/Library/MobileDevice/Provisioning Profiles"/*.provisionprofile
        "${HOME}/Library/Developer/Xcode/UserData/Provisioning Profiles"/*.mobileprovision
        "${HOME}/Library/Developer/Xcode/UserData/Provisioning Profiles"/*.provisionprofile
    )
    shopt -u nullglob

    for PROFILE_FILE in "${PROFILE_FILES[@]}"; do
        security cms -D -i "${PROFILE_FILE}" > "${PROFILE_PLIST}" 2>/dev/null || continue
        PROFILE_APP_ID="$(plist_value "${PROFILE_PLIST}" Entitlements.application-identifier)"
        PROFILE_BUNDLE_ID="${PROFILE_APP_ID#*.}"
        [[ "${PROFILE_BUNDLE_ID}" == "${BUNDLE_ID}" ]] || continue
        [[ "$(plist_value "${PROFILE_PLIST}" Entitlements.get-task-allow)" == "true" ]] || continue

        PROFILE_HAS_CERTIFICATE=0
        PROFILE_CERTIFICATE_COUNT="$(plist_value "${PROFILE_PLIST}" DeveloperCertificates)"
        [[ "${PROFILE_CERTIFICATE_COUNT}" =~ ^[0-9]+$ ]] || continue
        PROFILE_CERTIFICATE_INDEX=0
        while [[ "${PROFILE_CERTIFICATE_INDEX}" -lt "${PROFILE_CERTIFICATE_COUNT}" ]]; do
            if plutil -extract "DeveloperCertificates.${PROFILE_CERTIFICATE_INDEX}" raw -o - "${PROFILE_PLIST}" 2>/dev/null | \
                base64 -D > "${PROFILE_CERTIFICATE}" 2>/dev/null; then
                PROFILE_CERTIFICATE_HASH="$(shasum -a 1 "${PROFILE_CERTIFICATE}" | awk '{ print toupper($1) }')"
                if [[ "${PROFILE_CERTIFICATE_HASH}" == "${CODESIGN_IDENTITY_HASH}" ]]; then
                    PROFILE_HAS_CERTIFICATE=1
                    break
                fi
            fi
            PROFILE_CERTIFICATE_INDEX=$((PROFILE_CERTIFICATE_INDEX + 1))
        done
        [[ "${PROFILE_HAS_CERTIFICATE}" == "1" ]] || continue

        PROFILE_EXPIRY="$(plist_value "${PROFILE_PLIST}" ExpirationDate)"
        [[ -n "${PROFILE_EXPIRY}" && "${PROFILE_EXPIRY}" > "${NOW_UTC}" ]] || continue

        PROFILE_HAS_DEVICE=0
        PROFILE_DEVICE_COUNT="$(plist_value "${PROFILE_PLIST}" ProvisionedDevices)"
        [[ "${PROFILE_DEVICE_COUNT}" =~ ^[0-9]+$ ]] || continue
        PROFILE_DEVICE_INDEX=0
        while [[ "${PROFILE_DEVICE_INDEX}" -lt "${PROFILE_DEVICE_COUNT}" ]]; do
            if [[ "$(plist_value "${PROFILE_PLIST}" "ProvisionedDevices.${PROFILE_DEVICE_INDEX}")" == "${DEVICE_UDID}" ]]; then
                PROFILE_HAS_DEVICE=1
                break
            fi
            PROFILE_DEVICE_INDEX=$((PROFILE_DEVICE_INDEX + 1))
        done
        [[ "${PROFILE_HAS_DEVICE}" == "1" ]] || continue

        PROFILE_HAS_ENTITLEMENTS=1
        while IFS= read -r ENTITLEMENT; do
            [[ -n "${ENTITLEMENT}" ]] || continue
            if [[ "$(/usr/libexec/PlistBuddy -c "Print :Entitlements:${ENTITLEMENT}" "${PROFILE_PLIST}" 2>/dev/null || true)" != "true" ]]; then
                PROFILE_HAS_ENTITLEMENTS=0
                break
            fi
        done < <(/usr/libexec/PlistBuddy -c Print "${ENTITLEMENTS}" | \
            sed -nE 's/^[[:space:]]*([^ =]+)[[:space:]]*=[[:space:]]*true$/\1/p')
        [[ "${PROFILE_HAS_ENTITLEMENTS}" == "1" ]] || continue

        PROFILE_UUID="$(plist_value "${PROFILE_PLIST}" UUID)"
        PROFILE_NAME="$(plist_value "${PROFILE_PLIST}" Name)"
        [[ -n "${PROFILE_UUID}" ]] || continue
        if [[ -z "${BEST_PROFILE_UUID}" || "${PROFILE_EXPIRY}" > "${BEST_PROFILE_EXPIRY}" ]]; then
            BEST_PROFILE_UUID="${PROFILE_UUID}"
            BEST_PROFILE_NAME="${PROFILE_NAME}"
            BEST_PROFILE_EXPIRY="${PROFILE_EXPIRY}"
        fi
    done

    [[ -n "${BEST_PROFILE_UUID}" ]] || fail "no unexpired development profile for ${BUNDLE_ID} contains ${DEVICE_NAME}; set CODESIGN_PROVISION or refresh signing in Xcode"
    CODESIGN_PROVISION="${BEST_PROFILE_UUID}"
    SELECTED_PROFILE_DESCRIPTION="${BEST_PROFILE_NAME} (${BEST_PROFILE_UUID}, expires ${BEST_PROFILE_EXPIRY})"
else
    SELECTED_PROFILE_DESCRIPTION="${CODESIGN_PROVISION}"
fi

PYTHON_FRAMEWORK="${REPO_ROOT}/TensorAgent/python-runtime/device/Frameworks/Python.framework"
PYTHON_STDLIB="${REPO_ROOT}/TensorAgent/python-runtime/device/python"
if [[ ! -d "${PYTHON_FRAMEWORK}" || ! -d "${PYTHON_STDLIB}" ]]; then
    fail "the device Python runtime is not staged; run TensorAgent/scripts/prepare-python.sh device"
fi

echo "==> Target: ${DEVICE_NAME} (${DEVICE_ID})"
echo "==> Signing identity: ${CODESIGN_KEY}"
echo "==> Provisioning profile: ${SELECTED_PROFILE_DESCRIPTION}"
echo "==> Building a fresh TensorAgent Release bundle"
CONFIGURATION=Release \
CODESIGN_KEY="${CODESIGN_KEY}" \
CODESIGN_PROVISION="${CODESIGN_PROVISION}" \
NO_INCREMENTAL=1 \
SKIP_SIGNING=0 \
TENSORAGENT_REBUILD_XCFRAMEWORK="${TENSORAGENT_REBUILD_XCFRAMEWORK}" \
    bash "${SCRIPT_DIR}/build-device.sh"

[[ -d "${APP}" ]] || fail "build completed without producing ${APP}"
codesign --verify --deep --strict --verbose=2 "${APP}"
ACTUAL_BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${APP}/Info.plist" 2>/dev/null || true)"
[[ "${ACTUAL_BUNDLE_ID}" == "${BUNDLE_ID}" ]] || fail "built bundle identifier is '${ACTUAL_BUNDLE_ID}', expected '${BUNDLE_ID}'"

APP_EXECUTABLE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "${APP}/Info.plist" 2>/dev/null || true)"
[[ -n "${APP_EXECUTABLE}" && -f "${APP}/${APP_EXECUTABLE}" ]] || fail "could not locate the built app executable"
nm -gU "${APP}/${APP_EXECUTABLE}" > "${TEMP_DIR}/symbols.txt"
grep -q ' _TSGgml_IsMetalAvailable$' "${TEMP_DIR}/symbols.txt" || fail "Release stripping removed TensorAgent's GGML exports"

echo "==> Installing TensorAgent on ${DEVICE_NAME} (existing app data is preserved)"
xcrun devicectl --timeout "${DEVICECTL_TIMEOUT}" device install app \
    --device "${DEVICE_ID}" "${APP}"

if [[ "${SKIP_LAUNCH:-0}" != "1" ]]; then
    echo "==> Launching ${BUNDLE_ID}"
    xcrun devicectl --timeout 60 device process launch \
        --device "${DEVICE_ID}" --terminate-existing "${BUNDLE_ID}"
fi

echo "==> TensorAgent Release deployed successfully to ${DEVICE_NAME}"
