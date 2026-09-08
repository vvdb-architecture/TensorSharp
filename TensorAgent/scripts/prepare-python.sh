#!/usr/bin/env bash
# Stage the embedded CPython runtime TensorAgent ships for in-process script
# execution (skills scripts, the shell tool's `python3`).
#
# Why a staging step: an iOS app may only load code that is signed inside its own
# bundle, and every dynamic library has to be a .framework under Frameworks/.
# CPython's standard library ships ~68 extension modules as bare .so files and
# binary wheels (numpy, Pillow) ship more. CPython 3.13's iOS import system
# understands a `.fwork` placeholder next to where the .so would have been: a
# text file holding the bundle-relative path of the framework binary. This script
# produces, per platform slice:
#
#   TensorAgent/python-runtime/<slice>/
#     python/lib/python3.13/...           stdlib (.py, .fwork placeholders)
#     python/app_packages/...             pre-bundled packages (numpy, Pillow, pure wheels)
#     Frameworks/<dotted.module>.framework/{<dotted.module>, Info.plist}
#
# which TensorAgent.Maui.csproj embeds (Frameworks/* as NativeReference
# Kind=Framework, python/** as bundle resources). Nothing here runs at app
# runtime; runtime installs are limited to pure-Python wheels for this reason,
# and a compiled package the app needs has to be staged here -- from BeeWare's
# index when it publishes one (numpy, Pillow), or built by build-lxml-ios.sh
# when nothing does (lxml).
#
# Usage: prepare-python.sh [device|simulator|all]   (default: all)
# Env:   TENSORAGENT_PYTHON_PACKAGES  space-separated extra pure-Python packages
#        TENSORAGENT_PYTHON_NO_WHEELS=1 skip binary/pure wheel downloads (stdlib only)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AGENT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${AGENT_ROOT}/.." && pwd)"
PY_SRC="${REPO_ROOT}/ExternalProjects/python-ios/Python.xcframework"
OUT_ROOT="${AGENT_ROOT}/python-runtime"
WHEEL_CACHE="${REPO_ROOT}/ExternalProjects/python-ios/wheels"
PYVER="3.13"
BEEWARE_INDEX="https://pypi.anaconda.org/beeware/simple"

bash "${REPO_ROOT}/eng/fetch-python-ios.sh" >/dev/null
[[ -d "${PY_SRC}" ]] || { echo "prepare-python: ${PY_SRC} missing" >&2; exit 1; }

SLICES="${1:-all}"
[[ "${SLICES}" == "all" ]] && SLICES="device simulator"

# Binary wheels from BeeWare's index (built against the same CPython these
# frameworks come from). Versions are pinned so a rebuild is reproducible.
BINARY_PACKAGES=(
  "numpy==2.5.2.post1"
  "pillow==10.4.0"
)
# Binary wheels this repository builds itself, because no index publishes them.
# lxml is a C extension over libxml2/libxslt; scripts/build-lxml-ios.sh
# cross-compiles it against the same CPython, and the wheel lands in the same
# cache under the same name shape BeeWare uses, so the staging below treats the
# two lists alike. Missing wheels are built on demand (a few minutes, needs a
# host python3.13, CMake and Ninja).
LOCAL_BINARY_PACKAGES=(
  "lxml==6.1.3"
)
# Pure-Python packages from PyPI that the bundled skills import, or that a model
# reaches for by habit. Every entry here has to be importable with no compiler and
# no C extension of its own AND no dependency that has one, unless that dependency
# is on one of the two binary lists above. That is what rules out:
#
#   pdfplumber, pdfminer.six   both ship py3-none-any wheels, but every
#                              pdfminer.six release back to 20220524 has an
#                              unguarded `from cryptography.hazmat...` at the top
#                              of pdfminer/pdfdocument.py, and `cryptography`
#                              publishes no pure wheel at all.
#
# python-docx and python-pptx used to be on that list too (`from lxml import etree`
# at module scope, in docx/oxml/xmlchemy.py and pptx/oxml/__init__.py); with lxml
# built above they ship, because a model asked for a .pptx reaches for python-pptx
# before it reads any skill, and "lxml cannot be installed" was where that turn
# died. The documents skill's own writers still use zipfile + xml.etree and read
# PDFs with pypdf. See TensorAgent/skills/documents/SKILL.md.
PURE_PACKAGES=(
  "pypdf==6.16.2"
  "openpyxl==3.1.5"
  "et_xmlfile==2.0.0"
  "reportlab==5.0.1"
  "imageio==2.37.4"
  # python-pptx imports xlsxwriter (pptx/chart/xlsx.py) and typing_extensions
  # (pptx/types.py) at module scope; python-docx needs typing_extensions the same
  # way. All four are py3-none-any. Names are spelled the way the index files them
  # (lower case), which is what resolve_wheel matches on.
  "python-pptx==1.0.2"
  "python-docx==1.2.0"
  "xlsxwriter==3.2.9"
  "typing_extensions==4.16.0"
  # reportlab declares charset-normalizer and reaches for it from
  # reportlab/lib/rparsexml.py; without it that path raises ImportError on the
  # phone and nowhere else. Its py3-none-any wheel is the pure fallback build.
  "charset-normalizer==3.5.1"
  # Hardened parsers for XML that arrived from outside the app — an uploaded
  # .docx is a zip of XML a stranger wrote, and xml.etree will happily expand a
  # billion-laughs entity on the user's phone.
  "defusedxml==0.7.1"
  # The CA bundle. _ssl.framework ships, so Python CAN speak TLS on the phone --
  # but OpenSSL has no trust store there, and macOS's is not it. Without this every
  # https:// from a skill or from generated code fails certificate verification,
  # which is why the research skill could search from a laptop and never from a
  # device. The bootstrap points SSL_CERT_FILE at this.
  "certifi"
  "pyyaml"          # sdist: the pure-Python yaml package is extracted below
  ${TENSORAGENT_PYTHON_PACKAGES:-}
)

mkdir -p "${WHEEL_CACHE}"

# Resolve a wheel URL from a PEP 503 simple index page: pick the newest file that
# matches the requested version (if any) and platform tag.
resolve_wheel() {  # index_url name version_or_empty tag_regex
    local index="$1" name="$2" version="$3" tagre="$4"
    local page
    page="$(curl -sSL --fail "${index}/${name}/" )"
    # Anchor hrefs, decoded of the '#sha256=' fragment.
    echo "${page}" | grep -oE 'href="[^"]+"' | sed -E 's/^href="//; s/"$//' \
      | grep -E "\.whl" | grep -E "${tagre}" \
      | { if [[ -n "${version}" ]]; then grep -E "/${name//-/_}-${version}-|/${name}-${version}-" || true; else cat; fi; } \
      | tail -n 1
}

download() {  # url dest
    local url="$1" dest="$2"
    [[ -f "${dest}" ]] && return 0
    echo "  fetching $(basename "${dest}")"
    curl -sSL --fail -o "${dest}.part" "${url%%#*}"
    mv "${dest}.part" "${dest}"
}

stage_slice() {
    local slice="$1"
    local src_slice tag_re lib_arch plat
    case "${slice}" in
        device)    src_slice="ios-arm64";               lib_arch="lib-arm64"; tag_re="ios_13_0_arm64_iphoneos";        plat="iphoneos" ;;
        simulator) src_slice="ios-arm64_x86_64-simulator"; lib_arch="lib-arm64"; tag_re="ios_13_0_arm64_iphonesimulator"; plat="iphonesimulator" ;;
        *) echo "unknown slice ${slice}" >&2; exit 2 ;;
    esac
    local out="${OUT_ROOT}/${slice}"
    echo "==> Staging ${slice} (${src_slice})"
    rm -rf "${out}"
    mkdir -p "${out}/python/lib" "${out}/python/app_packages" "${out}/Frameworks"

    # 1. Standard library: shared pure-Python part + slice-specific part (sysconfig data, lib-dynload).
    rsync -a --delete "${PY_SRC}/lib/" "${out}/python/lib/" --exclude 'libpython*.dylib'
    rsync -a "${PY_SRC}/${src_slice}/${lib_arch}/" "${out}/python/lib/" --exclude 'libpython*.dylib'
    # Trim what an app never runs (34 MB of test suites etc.); keep everything else.
    rm -rf "${out}/python/lib/python${PYVER}"/{test,idlelib,tkinter,turtledemo,ensurepip,pydoc_data,lib2to3,__phello__}
    find "${out}/python/lib" -name "__pycache__" -type d -prune -exec rm -rf {} +
    # Test-only extension modules are dead weight and their frameworks would need signing too.
    rm -f "${out}/python/lib/python${PYVER}/lib-dynload"/{_test*,_xxtestfuzz,xxlimited*,xxsubtype,_ctypes_test}.*.so

    # 2. Packages.
    if [[ -z "${TENSORAGENT_PYTHON_NO_WHEELS:-}" ]]; then
        local spec name ver url whl
        for spec in "${BINARY_PACKAGES[@]}"; do
            name="${spec%%==*}"; ver="${spec#*==}"
            url="$(resolve_wheel "${BEEWARE_INDEX}" "${name}" "${ver}" "cp313-cp313-${tag_re}")"
            [[ -n "${url}" ]] || { echo "prepare-python: no ${name}==${ver} wheel for ${tag_re} on ${BEEWARE_INDEX}" >&2; exit 1; }
            # The index publishes host-relative hrefs (/beeware/simple/...).
            [[ "${url}" == http* ]] || url="https://pypi.anaconda.org${url}"
            whl="${WHEEL_CACHE}/$(basename "${url%%#*}")"
            download "${url}" "${whl}"
            unzip -q -o "${whl}" -d "${out}/python/app_packages"
        done
        for spec in "${LOCAL_BINARY_PACKAGES[@]}"; do
            name="${spec%%==*}"; ver="${spec#*==}"
            whl="${WHEEL_CACHE}/${name}-${ver}-cp313-cp313-${tag_re}.whl"
            if [[ ! -f "${whl}" ]]; then
                echo "  building $(basename "${whl}")"
                bash "${SCRIPT_DIR}/build-lxml-ios.sh" "${slice}"
            fi
            [[ -f "${whl}" ]] || { echo "prepare-python: ${whl} was not produced" >&2; exit 1; }
            unzip -q -o "${whl}" -d "${out}/python/app_packages"
        done
        for spec in "${PURE_PACKAGES[@]}"; do
            [[ -z "${spec}" ]] && continue
            name="${spec%%==*}"; ver=""; [[ "${spec}" == *==* ]] && ver="${spec#*==}"
            if [[ "${name}" == "pyyaml" ]]; then
                # PyYAML publishes no py3-none-any wheel; its sdist carries the pure
                # package under lib/yaml which works without the C accelerator.
                local sdist_url
                sdist_url="$(curl -sSL --fail https://pypi.org/pypi/pyyaml/json | python3 -c 'import json,sys; d=json.load(sys.stdin); print([u["url"] for u in d["urls"] if u["packagetype"]=="sdist"][0])')"
                local sdist="${WHEEL_CACHE}/$(basename "${sdist_url}")"
                download "${sdist_url}" "${sdist}"
                local tmp; tmp="$(mktemp -d)"
                tar xzf "${sdist}" -C "${tmp}"
                rm -rf "${out}/python/app_packages/yaml"
                cp -R "${tmp}"/pyyaml-*/lib/yaml "${out}/python/app_packages/yaml"
                # A dist-info as well, or the app's bundle scan (BundledPackages) never
                # sees PyYAML: `pip install pyyaml` would go to the index and be refused
                # as compiled while `import yaml` works. The sdist's PKG-INFO is already
                # in METADATA format; there is no RECORD, which is right for a pure copy.
                local pyyaml_src pyyaml_ver
                pyyaml_src="$(echo "${tmp}"/pyyaml-*)"
                pyyaml_ver="${pyyaml_src##*/pyyaml-}"
                rm -rf "${out}/python/app_packages"/[Pp][Yy][Yy][Aa][Mm][Ll]-*.dist-info
                mkdir -p "${out}/python/app_packages/PyYAML-${pyyaml_ver}.dist-info"
                cp "${pyyaml_src}/PKG-INFO" "${out}/python/app_packages/PyYAML-${pyyaml_ver}.dist-info/METADATA"
                rm -rf "${tmp}"
                continue
            fi
            url="$(resolve_wheel "https://pypi.org/simple" "${name}" "${ver}" "py3-none-any|py2\.py3-none-any")"
            [[ -n "${url}" ]] || { echo "prepare-python: no pure wheel for ${name}" >&2; exit 1; }
            whl="${WHEEL_CACHE}/$(basename "${url%%#*}")"
            download "${url}" "${whl}"
            unzip -q -o "${whl}" -d "${out}/python/app_packages"
        done
    fi

    # 3. Turn every .so (stdlib lib-dynload + app_packages) into a framework with a
    #    .fwork placeholder, exactly as Python.xcframework/build/utils.sh does for Xcode.
    local template="${PY_SRC}/build/iOS-dylib-Info-template.plist"
    local count=0
    while IFS= read -r so; do
        local rel="${so#${out}/}"                       # python/lib/python3.13/lib-dynload/_json.cpython-313-iphoneos.so
        local base
        if [[ "${rel}" == python/lib/python${PYVER}/lib-dynload/* ]]; then
            base="python/lib/python${PYVER}/lib-dynload/"
        elif [[ "${rel}" == python/app_packages/* ]]; then
            base="python/app_packages/"
        else
            continue
        fi
        local ext="${rel#${base}}"                      # numpy/core/_multiarray_umath.cpython-313-iphoneos.so
        local modname; modname="$(echo "${ext}" | cut -d. -f1 | tr '/' '.')"   # numpy.core._multiarray_umath
        local fw="Frameworks/${modname}.framework"
        mkdir -p "${out}/${fw}"
        cp "${template}" "${out}/${fw}/Info.plist"
        plutil -replace CFBundleExecutable -string "${modname}" "${out}/${fw}/Info.plist"
        plutil -replace CFBundleIdentifier -string "ai.tensorsharp.tensoragent.python.$(echo "${modname}" | tr '_' '-')" "${out}/${fw}/Info.plist"
        plutil -replace MinimumOSVersion -string "17.0" "${out}/${fw}/Info.plist" 2>/dev/null || true
        mv "${so}" "${out}/${fw}/${modname}"
        # Frameworks must be relocatable: give the binary the install name iOS expects.
        install_name_tool -id "@rpath/${modname}.framework/${modname}" "${out}/${fw}/${modname}" 2>/dev/null || true
        echo "${fw}/${modname}" > "${so%.so}.fwork"
        echo "${rel%.so}.fwork" > "${out}/${fw}/${modname}.origin"
        count=$((count+1))
    done < <(find "${out}/python" -name "*.so" -type f)
    echo "    ${count} extension modules converted to frameworks"

    # 4. The interpreter itself.
    rsync -a --delete "${PY_SRC}/${src_slice}/Python.framework/" "${out}/Frameworks/Python.framework/"
    # A slim marker the app reads to locate PYTHONHOME and to know the layout version.
    cat > "${out}/python/tensoragent-python.json" <<JSON
{ "version": "${PYVER}", "platform": "${plat}", "stdlib": "python/lib/python${PYVER}", "packages": "python/app_packages", "layout": 1 }
JSON
    du -sh "${out}/python" "${out}/Frameworks" | sed 's/^/    /'
}

for s in ${SLICES}; do stage_slice "${s}"; done
echo "Done: ${OUT_ROOT}"
