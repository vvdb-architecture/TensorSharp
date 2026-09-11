#!/usr/bin/env bash
# Cross-compile lxml for iOS, against the CPython TensorAgent embeds, into a wheel
# that prepare-python.sh stages exactly as it stages numpy and Pillow.
#
# Why this exists: lxml is a C extension (Cython over libxml2 and libxslt), and no
# index publishes an iOS build of it -- not PyPI, not BeeWare's. python-docx and
# python-pptx import it at module scope, so a model that reaches for either on the
# phone was told "pip install lxml" is impossible, and it was. An iOS app can only
# load native code that was signed into its own bundle, so the only place an lxml
# can come from is here, at build time.
#
# What it produces, per slice:
#
#   ExternalProjects/python-ios/wheels/lxml-<v>-cp313-cp313-ios_13_0_arm64_iphoneos.whl
#   ExternalProjects/python-ios/wheels/lxml-<v>-cp313-cp313-ios_13_0_arm64_iphonesimulator.whl
#
# Those file names are the same shape BeeWare's index uses, so prepare-python.sh
# picks them out of its wheel cache with no special case beyond "look locally first".
#
# How: libxml2 and libxslt are built as static archives with CMake's iOS support
# (the SDK's own zlib and iconv, no lzma, no ICU, no dynamic modules), and lxml is
# built with the cross-compilation environment Python-Apple-support ships beside
# each slice (platform-config/<arch>-<sdk>/make_cross_venv.py, the same mechanism
# cibuildwheel uses for iOS): a macOS venv whose sysconfig answers with the iOS
# slice's compiler, flags, EXT_SUFFIX and platform tag. The two libraries are linked
# into each extension module statically, which is how lxml's own macOS wheels are
# built, so the .so files depend only on Python.framework and the system.
#
# Prerequisites: Xcode with the iOS SDKs, CMake >= 3.18, Ninja, and a host CPython of
# the SAME minor version as the embedded one (python3.13; Homebrew's is fine). The host
# interpreter's minor version decides the wheel's cp tag and the venv's layout, which
# is why it must match.
#
# Usage: build-lxml-ios.sh [device|simulator|all]   (default: all)
# Env:   TENSORAGENT_LXML_FORCE=1   rebuild even if the wheel is already in the cache
#        HOST_PYTHON               the python3.13 to drive the build (default: found on PATH)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AGENT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${AGENT_ROOT}/.." && pwd)"
PY_IOS="${REPO_ROOT}/ExternalProjects/python-ios"
PY_SRC="${PY_IOS}/Python.xcframework"
SOURCES="${PY_IOS}/sources"
WORK="${PY_IOS}/build-lxml"
WHEEL_CACHE="${PY_IOS}/wheels"
PYVER="3.13"
DEPLOYMENT_TARGET="13.0"

# Pinned so a rebuild is reproducible, and hashed so a mirror cannot swap a tarball.
# libxslt 1.1.45 requires libxml2 >= 2.15.1; lxml 6.1.x carries the 2.15 error
# constants and build fixes (see its CHANGES.txt), so these three go together.
LXML_VERSION="6.1.3"
LXML_SHA256="45222d94ddd511536f3b2f7d9deae3b2339b4ce0f075f1ca25703b07cad9dd21"
LXML_URL="https://files.pythonhosted.org/packages/23/ad/28ecd7cb894d172f3c9c80a075eeeb2017ac62e3632cee05a5f9493547eb/lxml-${LXML_VERSION}.tar.gz"
LIBXML2_VERSION="2.15.4"
LIBXML2_SHA256="98087fd181d9070724f3fbc65c7377db03038eb92bd882374daff44940138821"
LIBXML2_URL="https://download.gnome.org/sources/libxml2/2.15/libxml2-${LIBXML2_VERSION}.tar.xz"
LIBXSLT_VERSION="1.1.45"
LIBXSLT_SHA256="9acfe68419c4d06a45c550321b3212762d92f41465062ca4ea19e632ee5d216e"
LIBXSLT_URL="https://download.gnome.org/sources/libxslt/1.1/libxslt-${LIBXSLT_VERSION}.tar.xz"

SLICES="${1:-all}"
[[ "${SLICES}" == "all" ]] && SLICES="device simulator"

fail() { echo "build-lxml-ios: $*" >&2; exit 1; }

command -v cmake >/dev/null 2>&1 || fail "cmake is required (brew install cmake)"
command -v ninja >/dev/null 2>&1 || fail "ninja is required (brew install ninja)"
xcrun --sdk iphoneos --show-sdk-path >/dev/null 2>&1 || fail "the iOS SDK is missing; install Xcode"

HOST_PYTHON="${HOST_PYTHON:-$(command -v "python${PYVER}" || true)}"
[[ -n "${HOST_PYTHON}" && -x "${HOST_PYTHON}" ]] \
    || fail "a host python${PYVER} is required (brew install python@${PYVER}); set HOST_PYTHON to point at one"
HOST_MINOR="$("${HOST_PYTHON}" -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
[[ "${HOST_MINOR}" == "${PYVER}" ]] \
    || fail "HOST_PYTHON is ${HOST_MINOR}; it must be ${PYVER}, the version the app embeds"

TENSORAGENT_PYTHON_NO_UPDATE=1 bash "${REPO_ROOT}/eng/fetch-python-ios.sh" >/dev/null
[[ -d "${PY_SRC}" ]] || fail "${PY_SRC} is missing"

mkdir -p "${SOURCES}" "${WORK}" "${WHEEL_CACHE}"

fetch() {  # url dest sha256
    local url="$1" dest="$2" sha="$3"
    if [[ ! -f "${dest}" ]]; then
        echo "  fetching $(basename "${dest}")"
        curl -sSL --fail -o "${dest}.part" "${url}"
        mv "${dest}.part" "${dest}"
    fi
    local actual
    actual="$(shasum -a 256 "${dest}" | cut -d' ' -f1)"
    [[ "${actual}" == "${sha}" ]] \
        || fail "$(basename "${dest}") has sha256 ${actual}, expected ${sha}; delete it and retry"
}

fetch "${LXML_URL}" "${SOURCES}/lxml-${LXML_VERSION}.tar.gz" "${LXML_SHA256}"
fetch "${LIBXML2_URL}" "${SOURCES}/libxml2-${LIBXML2_VERSION}.tar.xz" "${LIBXML2_SHA256}"
fetch "${LIBXSLT_URL}" "${SOURCES}/libxslt-${LIBXSLT_VERSION}.tar.xz" "${LIBXSLT_SHA256}"

unpack() {  # archive parent
    local archive="$1" parent="$2"
    mkdir -p "${parent}"
    tar xf "${archive}" -C "${parent}"
}

build_slice() {
    local slice="$1"
    local xc_slice config sdk sim_suffix plat tag
    case "${slice}" in
        device)
            xc_slice="ios-arm64"; config="arm64-iphoneos"; sdk="iphoneos"
            sim_suffix=""; plat="iphoneos"; tag="ios_13_0_arm64_iphoneos" ;;
        simulator)
            xc_slice="ios-arm64_x86_64-simulator"; config="arm64-iphonesimulator"; sdk="iphonesimulator"
            sim_suffix="-simulator"; plat="iphonesimulator"; tag="ios_13_0_arm64_iphonesimulator" ;;
        *) fail "unknown slice '${slice}' (device|simulator|all)" ;;
    esac

    local wheel="${WHEEL_CACHE}/lxml-${LXML_VERSION}-cp313-cp313-${tag}.whl"
    if [[ -f "${wheel}" && "${TENSORAGENT_LXML_FORCE:-0}" != "1" ]]; then
        echo "==> ${slice}: $(basename "${wheel}") is already built (TENSORAGENT_LXML_FORCE=1 to rebuild)"
        return 0
    fi

    local platform_config="${PY_SRC}/${xc_slice}/platform-config/${config}"
    [[ -f "${platform_config}/make_cross_venv.py" ]] \
        || fail "${platform_config} has no make_cross_venv.py; the Python-Apple-support build is too old"

    local root="${WORK}/${slice}"
    local prefix="${root}/prefix"
    echo "==> ${slice}: building libxml2 ${LIBXML2_VERSION}, libxslt ${LIBXSLT_VERSION} and lxml ${LXML_VERSION} (${tag})"
    rm -rf "${root}"
    mkdir -p "${root}" "${prefix}"

    # The CMake flags every native library shares. iOS cannot run a test executable,
    # so try_compile is told to build static libraries; the deployment target is the
    # one CPython itself was built for, which is also what the wheel tag promises.
    local -a cmake_ios=(
        -G Ninja
        -DCMAKE_BUILD_TYPE=Release
        -DCMAKE_SYSTEM_NAME=iOS
        -DCMAKE_OSX_SYSROOT="${sdk}"
        -DCMAKE_OSX_ARCHITECTURES=arm64
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${DEPLOYMENT_TARGET}"
        -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY
        -DCMAKE_INSTALL_PREFIX="${prefix}"
        -DCMAKE_PREFIX_PATH="${prefix}"
        -DCMAKE_FIND_ROOT_PATH="${prefix}"
        -DCMAKE_FIND_ROOT_PATH_MODE_PACKAGE=BOTH
        -DCMAKE_FIND_ROOT_PATH_MODE_LIBRARY=BOTH
        -DCMAKE_FIND_ROOT_PATH_MODE_INCLUDE=BOTH
        -DBUILD_SHARED_LIBS=OFF
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    )

    # 1. libxml2: zlib and iconv from the SDK, nothing that would need a bundled
    #    dependency of its own (lzma, ICU) or a dlopen (modules).
    unpack "${SOURCES}/libxml2-${LIBXML2_VERSION}.tar.xz" "${root}/src"
    cmake -S "${root}/src/libxml2-${LIBXML2_VERSION}" -B "${root}/libxml2" "${cmake_ios[@]}" \
        -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_PROGRAMS=OFF \
        -DLIBXML2_WITH_LZMA=OFF -DLIBXML2_WITH_ZLIB=ON -DLIBXML2_WITH_ICONV=ON -DLIBXML2_WITH_ICU=OFF \
        -DLIBXML2_WITH_MODULES=OFF -DLIBXML2_WITH_HTTP=OFF -DLIBXML2_WITH_LEGACY=OFF \
        -DLIBXML2_WITH_TLS=ON >/dev/null
    cmake --build "${root}/libxml2" >/dev/null
    cmake --install "${root}/libxml2" >/dev/null

    # 2. libxslt (and libexslt) against that libxml2.
    unpack "${SOURCES}/libxslt-${LIBXSLT_VERSION}.tar.xz" "${root}/src"
    cmake -S "${root}/src/libxslt-${LIBXSLT_VERSION}" -B "${root}/libxslt" "${cmake_ios[@]}" \
        -DLIBXSLT_WITH_PYTHON=OFF -DLIBXSLT_WITH_TESTS=OFF -DLIBXSLT_WITH_PROGRAMS=OFF \
        -DLIBXSLT_WITH_CRYPTO=OFF -DLIBXSLT_WITH_MODULES=OFF >/dev/null
    cmake --build "${root}/libxslt" >/dev/null
    cmake --install "${root}/libxslt" >/dev/null

    for lib in libxml2 libxslt libexslt; do
        [[ -f "${prefix}/lib/${lib}.a" ]] || fail "${slice}: ${prefix}/lib/${lib}.a was not produced"
    done

    # 3. The two config scripts lxml's setup asks for. CMake's libxslt installs none,
    #    so both are written here, and they say only what lxml reads: the version, the
    #    include directories and the library directory. Which libraries to link is
    #    lxml's own list (xslt, exslt, xml2, z, m); iconv is added through LDFLAGS below
    #    because that list does not know about it.
    mkdir -p "${prefix}/bin"
    cat > "${prefix}/bin/xml2-config" <<EOF
#!/bin/sh
case "\$1" in
  --version) echo "${LIBXML2_VERSION}" ;;
  --cflags) echo "-I${prefix}/include/libxml2" ;;
  --libs) echo "-L${prefix}/lib -lxml2 -lz -liconv -lm" ;;
  *) exit 1 ;;
esac
EOF
    cat > "${prefix}/bin/xslt-config" <<EOF
#!/bin/sh
case "\$1" in
  --version) echo "${LIBXSLT_VERSION}" ;;
  --cflags) echo "-I${prefix}/include -I${prefix}/include/libxml2" ;;
  --libs) echo "-L${prefix}/lib -lxslt -lexslt -lxml2 -lz -liconv -lm" ;;
  *) exit 1 ;;
esac
EOF
    chmod +x "${prefix}/bin/xml2-config" "${prefix}/bin/xslt-config"

    # 4. A venv that believes it is the iOS slice. The build tools go in BEFORE the
    #    conversion, while pip still resolves wheels for the Mac it is running on.
    local venv="${root}/venv"
    "${HOST_PYTHON}" -m venv "${venv}"
    "${venv}/bin/pip" install -q --disable-pip-version-check "setuptools>=70" "wheel" "Cython>=3.2.9,<3.3"
    "${venv}/bin/python" "${platform_config}/make_cross_venv.py" "${venv}" "${platform_config}" >/dev/null

    # 5. lxml itself. Its sdist carries the Cython-generated C, so nothing is
    #    regenerated; STATIC_DEPS is deliberately NOT set (that would make lxml try to
    #    ./configure libxml2 on its own), the libraries come from the two scripts above.
    #    The compiler is the slice's shim from Python.xcframework/<slice>/bin, which is
    #    the CC sysconfig names; IPHONEOS_DEPLOYMENT_TARGET completes its -target.
    unpack "${SOURCES}/lxml-${LXML_VERSION}.tar.gz" "${root}/src"
    local staging="${root}/wheelhouse"
    mkdir -p "${staging}"
    (
        export PATH="${PY_SRC}/${xc_slice}/bin:${PATH}"
        export IPHONEOS_DEPLOYMENT_TARGET="${DEPLOYMENT_TARGET}"
        # lxml's setupinfo looks for the libraries in this order: the --with-*-config
        # options (which it also reads from the environment as WITH_*_CONFIG), then
        # pkg-config, then the XML2_CONFIG/XSLT_CONFIG variables. Only the first is
        # safe here: a Mac with Homebrew's pkgconf would otherwise be handed its own
        # macOS libxml2 and the build would fail at link time, or worse, not fail.
        export WITH_XML2_CONFIG="${prefix}/bin/xml2-config"
        export WITH_XSLT_CONFIG="${prefix}/bin/xslt-config"
        export XML2_CONFIG="${prefix}/bin/xml2-config"
        export XSLT_CONFIG="${prefix}/bin/xslt-config"
        export PKG_CONFIG=/usr/bin/false
        export LDFLAGS="-liconv"
        export WITHOUT_CYTHON="true"
        cd "${root}/src/lxml-${LXML_VERSION}"
        "${venv}/bin/pip" wheel -q --disable-pip-version-check --no-build-isolation --no-deps \
            -w "${staging}" . > "${root}/lxml-build.log" 2>&1 \
            || { tail -n 40 "${root}/lxml-build.log" >&2; fail "${slice}: the lxml build failed; see ${root}/lxml-build.log"; }
    )

    local built
    built="$(ls "${staging}"/lxml-*.whl 2>/dev/null | head -n 1)"
    [[ -n "${built}" ]] || fail "${slice}: pip wheel produced no wheel in ${staging}"
    [[ "$(basename "${built}")" == "$(basename "${wheel}")" ]] \
        || fail "${slice}: built $(basename "${built}") but expected $(basename "${wheel}"); the cross environment did not take"

    # 6. Prove the binaries are what the tag says before anything downstream trusts
    #    them: every extension module must be arm64 Mach-O for the right platform
    #    (LC_BUILD_VERSION platform 2 is iOS, 7 is the iOS simulator), must link
    #    Python.framework by its @rpath name like the numpy build does, and must not
    #    carry a dynamic reference to libxml2 or libxslt.
    local check="${root}/check"
    rm -rf "${check}"; mkdir -p "${check}"
    unzip -q "${built}" -d "${check}"
    local want_platform; want_platform=$([[ "${slice}" == "device" ]] && echo 2 || echo 7)
    local count=0
    while IFS= read -r so; do
        local platform
        platform="$(otool -l "${so}" | awk '/LC_BUILD_VERSION/{f=1} f&&/platform/{print $2; exit}')"
        [[ "${platform}" == "${want_platform}" ]] \
            || fail "${slice}: $(basename "${so}") is built for platform ${platform}, expected ${want_platform}"
        lipo -info "${so}" | grep -q "arm64" || fail "${slice}: $(basename "${so}") is not arm64"
        otool -L "${so}" | grep -q "@rpath/Python.framework/Python" \
            || fail "${slice}: $(basename "${so}") does not link Python.framework"
        if otool -L "${so}" | grep -qE "libxml2|libxslt|libexslt"; then
            fail "${slice}: $(basename "${so}") links libxml2/libxslt dynamically; they must be static"
        fi
        count=$((count + 1))
    done < <(find "${check}/lxml" -name "*.so" -type f)
    [[ "${count}" -ge 2 ]] || fail "${slice}: only ${count} extension modules in the wheel; lxml.etree and lxml.objectify are expected"

    cp "${built}" "${wheel}"
    echo "    $(basename "${wheel}") (${count} extension modules, $(du -h "${wheel}" | cut -f1 | tr -d ' '))"
}

for s in ${SLICES}; do build_slice "${s}"; done
echo "Done: ${WHEEL_CACHE}"
