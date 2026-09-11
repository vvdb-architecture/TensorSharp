#!/usr/bin/env bash
# Fetch the CPython iOS distribution (BeeWare Python-Apple-support) that TensorAgent
# embeds for in-process script execution, into ExternalProjects/python-ios.
#
# The tarball carries Python.xcframework (device + simulator slices, ~115 MB
# unpacked), the pure-Python standard library and the lib-dynload extension
# modules. TensorAgent/scripts/prepare-python.sh turns it into the bundle layout
# iOS can load (stdlib as bundle resources, each extension module as a framework).
#
# Environment overrides:
#   TENSORAGENT_PYTHON_VERSION  (default 3.13)
#   TENSORAGENT_PYTHON_BUILD    (default b14 — the release tag is <version>-<build>)
#   TENSORAGENT_PYTHON_NO_UPDATE if set and the framework is already unpacked, do nothing
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEST="${REPO_ROOT}/ExternalProjects/python-ios"
VERSION="${TENSORAGENT_PYTHON_VERSION:-3.13}"
BUILD="${TENSORAGENT_PYTHON_BUILD:-b14}"
TARBALL="Python-${VERSION}-iOS-support.${BUILD}.tar.gz"
URL="https://github.com/beeware/Python-Apple-support/releases/download/${VERSION}-${BUILD}/${TARBALL}"

if [[ -n "${TENSORAGENT_PYTHON_NO_UPDATE:-}" && -d "${DEST}/Python.xcframework" ]]; then
    echo "python-ios: TENSORAGENT_PYTHON_NO_UPDATE set; using ${DEST}/Python.xcframework"
    exit 0
fi

mkdir -p "${DEST}"
if [[ ! -f "${DEST}/${TARBALL}" ]]; then
    echo "python-ios: downloading ${URL}"
    curl -sSL --fail -o "${DEST}/${TARBALL}.part" "${URL}"
    mv "${DEST}/${TARBALL}.part" "${DEST}/${TARBALL}"
fi
if [[ ! -d "${DEST}/Python.xcframework" ]]; then
    echo "python-ios: unpacking ${TARBALL}"
    tar xzf "${DEST}/${TARBALL}" -C "${DEST}"
fi
cat "${DEST}/VERSIONS"
echo "python-ios: ready at ${DEST}/Python.xcframework"
