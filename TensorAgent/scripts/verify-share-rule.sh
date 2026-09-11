#!/usr/bin/env bash
# Validate the public Share-extension activation contract that actually ships.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLIST="${SCRIPT_DIR}/../src/TensorAgent.ShareExtension/Info.plist"
EXTENSION_DIR="$(dirname "${PLIST}")"
BASE=":NSExtension:NSExtensionAttributes"
RULE="${BASE}:NSExtensionActivationRule"

fail() { echo "FAIL: $*" >&2; exit 1; }
read_key() { /usr/libexec/PlistBuddy -c "Print $1" "${PLIST}" 2>/dev/null || true; }
expect() {
    local path="$1" expected="$2" label="$3" actual
    actual="$(read_key "${path}")"
    [[ "${actual}" == "${expected}" ]] || fail "${label} is '${actual}', expected '${expected}'"
}

[[ -f "${PLIST}" ]] || fail "no share-extension Info.plist at ${PLIST}"
plutil -lint "${PLIST}" >/dev/null
expect ":NSExtension:NSExtensionPointIdentifier" "com.apple.share-services" "extension point"
expect "${RULE}:NSExtensionActivationDictionaryVersion" "2" "activation dictionary version"
expect "${RULE}:NSExtensionActivationSupportsAttachmentsWithMaxCount" "20" "attachment cap"
expect "${RULE}:NSExtensionActivationSupportsFileWithMaxCount" "20" "file cap"
expect "${RULE}:NSExtensionActivationSupportsImageWithMaxCount" "20" "image cap"
expect "${RULE}:NSExtensionActivationSupportsMovieWithMaxCount" "10" "movie cap"
expect "${RULE}:NSExtensionActivationSupportsText" "true" "text support"
expect "${RULE}:NSExtensionActivationSupportsWebURLWithMaxCount" "20" "URL cap"
# Apple requires this nonzero dictionary key before Safari runs preprocessing JS.
expect "${RULE}:NSExtensionActivationSupportsWebPageWithMaxCount" "1" "webpage cap"

JS="$(read_key "${BASE}:NSExtensionJavaScriptPreprocessingFile")"
[[ -n "${JS}" ]] || fail "NSExtensionJavaScriptPreprocessingFile is missing"
[[ "${JS}" != *.js ]] || fail "preprocessing name must omit .js"
JS_PATH="${EXTENSION_DIR}/${JS}.js"
[[ -f "${JS_PATH}" ]] || fail "${JS_PATH} is missing"
grep -q 'var ExtensionPreprocessingJS = new TensorAgentPreprocessor' "${JS_PATH}" \
    || fail "preprocessing script has no ExtensionPreprocessingJS instance"
if command -v node >/dev/null 2>&1; then
    node --check "${JS_PATH}"
fi

if grep -q 'TRUEPREDICATE' "${PLIST}"; then
    fail "TRUEPREDICATE is forbidden in App Store builds"
fi

echo "ok  Share activation supports text, URLs, Safari pages, images, movies and files"
