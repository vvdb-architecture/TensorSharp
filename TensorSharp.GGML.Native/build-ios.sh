#!/usr/bin/env bash
# Cross-compiles GgmlOps (TensorSharp custom kernels + statically-linked ggml) for
# iOS/iPadOS and packages the slices as build-ios/GgmlOps.xcframework:
#   - device slice    (arm64, iphoneos):        ggml-metal ENABLED, shaders embedded
#                                               as source and compiled on-device at
#                                               first launch (no metallib toolchain)
#   - simulator slice (arm64, iphonesimulator): CPU-only. The simulator's Metal
#                                               device only advertises the Apple2
#                                               feature set, so ggml-metal's
#                                               simdgroup kernels would all be
#                                               unsupported; a clean CPU build is
#                                               strictly better there.
#
# Consumed by the TensorAgent MAUI app via <NativeReference Kind="Static"
# ForceLoad="True"> — the managed layer resolves DllImport("GgmlOps") to the main
# program handle on iOS (see GgmlNative.ImportResolver).
#
# Env overrides:
#   TENSORSHARP_IOS_DEPLOYMENT_TARGET  (default 17.0)
#   TENSORSHARP_IOS_ARM_ARCH           (default armv8.2-a+dotprod+fp16 — A13 and
#                                       later; every GPU ggml-metal can use is
#                                       A14+, so this never excludes a device that
#                                       could have run on the GPU. +i8mm needs A15+)
#   TENSORSHARP_IOS_SIM_ARM_ARCH       (default armv8.2-a+dotprod+fp16 — the
#                                       arm64 simulator executes natively on M1+)
#   TENSORSHARP_IOS_SLICES             (default "device sim"; "device" or "sim"
#                                       builds one slice only — the xcframework
#                                       is still produced from what was built)
#   TENSORSHARP_GGML_GIT_REF / TENSORSHARP_GGML_NO_UPDATE — forwarded to
#                                       fetch-ggml.sh. NO_UPDATE defaults to 1 here
#                                       so the iOS slices are built from the same
#                                       ggml checkout as the desktop build.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export TENSORSHARP_GGML_NO_UPDATE="${TENSORSHARP_GGML_NO_UPDATE:-1}"
bash "${SCRIPT_DIR}/../eng/fetch-ggml.sh"

IOS_MIN="${TENSORSHARP_IOS_DEPLOYMENT_TARGET:-17.0}"
DEV_ARCH="${TENSORSHARP_IOS_ARM_ARCH:-armv8.2-a+dotprod+fp16}"
SIM_ARCH="${TENSORSHARP_IOS_SIM_ARM_ARCH:-armv8.2-a+dotprod+fp16}"
SLICES="${TENSORSHARP_IOS_SLICES:-device sim}"
OUT="${SCRIPT_DIR}/build-ios"

GENERATOR="Ninja"
if ! command -v ninja >/dev/null 2>&1; then
    GENERATOR="Unix Makefiles"
fi

COMMON=(
  -G "${GENERATOR}"
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_SYSTEM_NAME=iOS
  -DCMAKE_OSX_ARCHITECTURES=arm64
  -DCMAKE_OSX_DEPLOYMENT_TARGET="${IOS_MIN}"
  -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY
  -DGGML_NATIVE=OFF
  -DGGML_OPENMP=OFF
  -DTENSORSHARP_GGML_NATIVE_IOS=ON
)

build_slice() {
    local slice="$1" sysroot="$2" metal="$3" arch="$4"
    echo "==> Configuring + building ${slice} slice (arm64, ${sysroot}, Metal ${metal}, -march=${arch}, minos ${IOS_MIN})"
    cmake -S "${SCRIPT_DIR}" -B "${OUT}/${slice}" "${COMMON[@]}" \
      -DCMAKE_OSX_SYSROOT="${sysroot}" \
      -DTENSORSHARP_GGML_NATIVE_ENABLE_METAL="${metal}" \
      -DGGML_CPU_ARM_ARCH="${arch}"
    cmake --build "${OUT}/${slice}" --target GgmlOps --parallel

    echo "==> Merging static archives for ${slice}"
    # GgmlOps + every ggml archive land flat in the build dir because CMakeLists.txt
    # pins ARCHIVE/LIBRARY/RUNTIME output dirs to CMAKE_BINARY_DIR.
    libtool -static -no_warning_for_no_symbols \
      -o "${OUT}/${slice}/libGgmlOpsMerged.a" \
      "${OUT}/${slice}"/libGgmlOps.a \
      "${OUT}/${slice}"/libggml*.a
}

LIBS=()
for s in ${SLICES}; do
    case "$s" in
        device) build_slice device iphoneos        ON  "${DEV_ARCH}" ;;
        sim)    build_slice sim    iphonesimulator OFF "${SIM_ARCH}" ;;
        *) echo "unknown slice '$s' (expected device|sim)" >&2; exit 2 ;;
    esac
    LIBS+=( -library "${OUT}/${s}/libGgmlOpsMerged.a" )
done

echo "==> Assembling GgmlOps.xcframework"
rm -rf "${OUT}/GgmlOps.xcframework"
xcodebuild -create-xcframework "${LIBS[@]}" -output "${OUT}/GgmlOps.xcframework"

echo "==> Validating slices"
# nm/grep pipelines: grep -c prints 0 and exits 1 when nothing matches (hence the
# || true, or set -e would abort on the simulator's zero Metal symbols), and a
# closed pipe makes otool exit non-zero, so this block runs without pipefail.
set +o pipefail
for s in ${SLICES}; do
    LIB="${OUT}/${s}/libGgmlOpsMerged.a"
    TSG=$(nm -gU "${LIB}" 2>/dev/null | grep -c ' _TSGgml_' || true)
    # ggml embeds one shader library per kernel family, each as a
    # ggml_metallib_<kind>_start/_end pair (ggml-metal/CMakeLists.txt).
    METALLIB=$(nm -gU "${LIB}" 2>/dev/null | grep -c '_ggml_metallib_.*_start$' || true)
    METALINIT=$(nm -gU "${LIB}" 2>/dev/null | grep -c ' _ggml_backend_metal_init$' || true)
    PLATFORM=$(otool -l "${LIB}" 2>/dev/null | grep -A3 LC_BUILD_VERSION | awk '/platform/ {print $2; exit}')
    echo "    ${s}: ${TSG} TSGgml_* exports, embedded metallib families: ${METALLIB}, metal init: ${METALINIT}, LC_BUILD_VERSION platform: ${PLATFORM:-?} (2=iOS, 7=iOS simulator)"
    if [ "${TSG}" -eq 0 ]; then
        echo "ERROR: ${s} slice exports no TSGgml_* symbols" >&2; exit 1
    fi
    if [ "$s" = device ] && [ "${METALLIB}" -eq 0 ]; then
        echo "ERROR: device slice is missing the embedded Metal shader blobs" >&2; exit 1
    fi
    if [ "$s" = device ] && [ "${PLATFORM}" != "2" ]; then
        echo "ERROR: device slice is not built for the iOS platform (got '${PLATFORM}')" >&2; exit 1
    fi
    if [ "$s" = sim ] && [ "${METALINIT}" -ne 0 ]; then
        echo "ERROR: simulator slice must not contain ggml-metal" >&2; exit 1
    fi
    if [ "$s" = sim ] && [ "${PLATFORM}" != "7" ]; then
        echo "ERROR: simulator slice is not built for the iOS simulator platform (got '${PLATFORM}')" >&2; exit 1
    fi
done
set -o pipefail

echo "Done: ${OUT}/GgmlOps.xcframework"
