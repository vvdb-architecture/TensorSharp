#!/usr/bin/env bash
# Clone (or update) the upstream ggml-org/ggml repository into ExternalProjects/ggml.
#
# TensorSharp builds ggml from source: both the GGML native ops library
# (TensorSharp.GGML.Native/CMakeLists.txt) and the CUDA PTX kernels
# (TensorSharp.Backends.Cuda/native/kernels/tensorsharp_kernels.cu) consume the
# sources at ExternalProjects/ggml. The directory is not committed; it is fetched
# here at build time so the repo tracks upstream ggml.
#
# Environment overrides:
#   TENSORSHARP_GGML_GIT_URL   git URL                (default: ggml-org/ggml)
#   TENSORSHARP_GGML_GIT_REF   branch/tag/commit      (default: master, the ggml default branch)
#   TENSORSHARP_GGML_NO_UPDATE if set to 1/ON/true and a checkout already exists,
#                              skip the network fetch and use what is on disk.
#
# Local patches: every eng/ggml-patches/*.patch is applied to the checkout, in name
# order, after a clone, after an update, and on the no-update path (idempotently:
# a patch that already applies in reverse is already in). A patch that fits neither
# way is an ERROR, not a warning -- a build that silently ran without a patch it was
# written to carry would reintroduce the exact behaviour the patch removes (the
# first one exists because ggml-metal reserved an F16 copy of the whole K/V window
# per attention layer in every persistent graph, gigabytes on a phone). When
# upstream moves under a patch, update the patch or pin TENSORSHARP_GGML_GIT_REF.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
EXTERNAL_PROJECTS_DIR="${REPO_ROOT}/ExternalProjects"
GGML_DIR="${REPO_ROOT}/ExternalProjects/ggml"
LOCK_FILE="${EXTERNAL_PROJECTS_DIR}/.ggml-fetch.lock"

GIT_URL="${TENSORSHARP_GGML_GIT_URL:-https://github.com/ggml-org/ggml.git}"
GIT_REF="${TENSORSHARP_GGML_GIT_REF:-master}"
NO_UPDATE_RAW="${TENSORSHARP_GGML_NO_UPDATE:-}"

mkdir -p "${EXTERNAL_PROJECTS_DIR}"

# Solution builds can invoke this from multiple projects at once.
if command -v flock >/dev/null 2>&1; then
    exec 9>"${LOCK_FILE}"
    flock 9
else
    LOCK_DIR="${LOCK_FILE}.d"
    until mkdir "${LOCK_DIR}" 2>/dev/null; do
        sleep 1
    done
    trap 'rmdir "${LOCK_DIR}" 2>/dev/null || true' EXIT
fi

is_truthy() {
    case "${1:-}" in
        1|ON|on|On|TRUE|true|True|YES|yes|Yes) return 0 ;;
        *) return 1 ;;
    esac
}

PATCH_DIR="${SCRIPT_DIR}/ggml-patches"
apply_local_patches() {
    local patch applied=0
    shopt -s nullglob
    for patch in "${PATCH_DIR}"/*.patch; do
        if git -C "${GGML_DIR}" apply --reverse --check "${patch}" >/dev/null 2>&1; then
            : # already carried by this checkout
        elif git -C "${GGML_DIR}" apply --check "${patch}" >/dev/null 2>&1; then
            git -C "${GGML_DIR}" apply "${patch}"
            echo "ggml: applied $(basename "${patch}")"
            applied=$((applied + 1))
        else
            echo "ggml: ERROR - $(basename "${patch}") applies to neither direction of the checkout at" \
                 "$(git -C "${GGML_DIR}" rev-parse --short HEAD); update the patch under ${PATCH_DIR}" \
                 "or pin TENSORSHARP_GGML_GIT_REF to a revision it fits." >&2
            exit 1
        fi
    done
    shopt -u nullglob
    return 0
}

if [[ -d "${GGML_DIR}/.git" ]]; then
    if is_truthy "${NO_UPDATE_RAW}"; then
        echo "ggml: TENSORSHARP_GGML_NO_UPDATE set; using existing checkout at ${GGML_DIR}"
        apply_local_patches
        exit 0
    fi

    echo "ggml: updating existing checkout to ${GIT_REF} (${GIT_URL})"
    git -C "${GGML_DIR}" remote set-url origin "${GIT_URL}" 2>/dev/null || true
    if git -C "${GGML_DIR}" fetch --depth 1 origin "${GIT_REF}" 2>/dev/null; then
        git -C "${GGML_DIR}" reset --hard FETCH_HEAD
        git -C "${GGML_DIR}" rev-parse --short HEAD | sed 's/^/ggml: now at /'
    else
        echo "ggml: WARNING - could not fetch ${GIT_REF} (offline?); using existing checkout" >&2
    fi
    apply_local_patches
    exit 0
fi

# No checkout yet: a partial/empty directory left by a failed clone would make
# 'git clone' fail, so clear it first.
if [[ -e "${GGML_DIR}" ]]; then
    rm -rf "${GGML_DIR}"
fi

echo "ggml: cloning ${GIT_URL} (${GIT_REF}) into ${GGML_DIR}"
if git clone --depth 1 --branch "${GIT_REF}" "${GIT_URL}" "${GGML_DIR}" 2>/dev/null; then
    :
else
    # --branch only accepts a branch or tag; fall back to fetching an explicit
    # commit ref shallowly.
    rm -rf "${GGML_DIR}"
    git init -q "${GGML_DIR}"
    git -C "${GGML_DIR}" remote add origin "${GIT_URL}"
    git -C "${GGML_DIR}" fetch --depth 1 origin "${GIT_REF}"
    git -C "${GGML_DIR}" checkout -q FETCH_HEAD
fi
git -C "${GGML_DIR}" rev-parse --short HEAD | sed 's/^/ggml: cloned at /'
apply_local_patches
