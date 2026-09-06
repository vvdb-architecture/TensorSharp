#!/usr/bin/env bash
# Builds TensorAgent.Maui for a physical iPhone (ios-arm64).
#
# Same two traps as build-sim.sh (user-local SDK carries the maui-ios workload;
# the xcframework is gitignored and built on demand), plus the one a device adds:
# a device build must be SIGNED, so a codesigning identity and a provisioning
# profile for the app id have to exist. Simulator builds sign with "-" and need
# neither.
#
# Env:
#   CONFIGURATION   Debug (default) | Release
#   CODESIGN_KEY    signing identity, e.g. "Apple Development: you@example.com (XXXXXXXXXX)"
#   CODESIGN_PROVISION  profile NAME or UUID for ai.tensorsharp.tensoragent
#   SKIP_SIGNING=1  build without signing (compile/link/AOT check only; NOT installable)
#   NO_INCREMENTAL=1  force a non-incremental build (used by deploy-device.sh so
#                     a stale unsigned bundle can never be reused)
#   TENSORAGENT_REBUILD_XCFRAMEWORK=1  rebuild the device + simulator native
#                     slices even when the xcframework already exists
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Debug}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="${DOTNET_ROOT}:${PATH}"
export TENSORSHARP_GGML_NO_UPDATE=1
export TENSORSHARP_GGML_NATIVE_SKIP=true
export TENSORSHARP_MLX_NATIVE_SKIP=true

XCFRAMEWORK="${REPO_ROOT}/TensorSharp.GGML.Native/build-ios/GgmlOps.xcframework"
if [[ ! -d "${XCFRAMEWORK}/ios-arm64" || "${TENSORAGENT_REBUILD_XCFRAMEWORK:-0}" == "1" ]]; then
    if [[ ! -d "${REPO_ROOT}/ExternalProjects/ggml" ]]; then
        echo "ExternalProjects/ggml is missing; run eng/fetch-ggml.sh once before building the xcframework." >&2
        exit 1
    fi
    echo "==> Building ${XCFRAMEWORK}"
    TENSORSHARP_IOS_SLICES="device sim" \
        bash "${REPO_ROOT}/TensorSharp.GGML.Native/build-ios.sh"
fi

ARGS=(
  "${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/TensorAgent.Maui.csproj"
  -f net10.0-ios
  -p:RuntimeIdentifier=ios-arm64
  -c "${CONFIGURATION}"
  -p:TensorSharpIosTargets=true
  -nologo
)
if [[ "${SKIP_SIGNING:-0}" == "1" ]]; then
    # Enough to prove the slice compiles, links against the device xcframework and
    # passes AOT. The bundle it leaves cannot be installed on a phone.
    ARGS+=( -p:_RequireCodeSigning=false -p:CodesignKey= -p:EnableCodeSigning=false )
else
    [[ -n "${CODESIGN_KEY:-}" ]] || { echo "CODESIGN_KEY is required (or SKIP_SIGNING=1)" >&2; exit 1; }
    ARGS+=( -p:CodesignKey="${CODESIGN_KEY}" )
    [[ -n "${CODESIGN_PROVISION:-}" ]] && ARGS+=( -p:CodesignProvision="${CODESIGN_PROVISION}" )
fi
[[ "${NO_INCREMENTAL:-0}" == "1" ]] && ARGS+=( --no-incremental )

echo "==> dotnet $(dotnet --version): TensorAgent.Maui (${CONFIGURATION}, ios-arm64)"
dotnet build "${ARGS[@]}"

APP="${REPO_ROOT}/TensorAgent/src/TensorAgent.Maui/bin/${CONFIGURATION}/net10.0-ios/ios-arm64/TensorAgent.Maui.app"
[[ -d "${APP}" ]] || { echo "Build finished but the bundle is not at ${APP}" >&2; exit 1; }
echo "Bundle: ${APP}"
