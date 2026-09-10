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
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$ExternalProjectsDir = Join-Path $RepoRoot "ExternalProjects"
$GgmlDir = Join-Path $ExternalProjectsDir "ggml"

$GitUrl = if ([string]::IsNullOrWhiteSpace($env:TENSORSHARP_GGML_GIT_URL)) { "https://github.com/ggml-org/ggml.git" } else { $env:TENSORSHARP_GGML_GIT_URL }
$GitRef = if ([string]::IsNullOrWhiteSpace($env:TENSORSHARP_GGML_GIT_REF)) { "master" } else { $env:TENSORSHARP_GGML_GIT_REF }

New-Item -ItemType Directory -Force -Path $ExternalProjectsDir | Out-Null
$Sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $HashBytes = $Sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($RepoRoot.ToLowerInvariant()))
}
finally {
    $Sha256.Dispose()
}
$HashText = -join ($HashBytes[0..7] | ForEach-Object { $_.ToString("x2") })
$FetchMutex = [System.Threading.Mutex]::new($false, "TensorSharpGgmlFetch_$HashText")
$HasFetchMutex = $false

try {
    $HasFetchMutex = $FetchMutex.WaitOne([TimeSpan]::FromMinutes(10))
    if (-not $HasFetchMutex) {
        throw "Timed out waiting for ggml fetch lock."
    }

# `git apply --check` returns non-zero while probing the direction that does not
# match. Windows PowerShell 5.1 turns a native command's redirected stderr into a
# terminating NativeCommandError when ErrorActionPreference is Stop, so isolate
# those expected failures without weakening error handling for the real apply.
function Test-GitPatchApplies([string] $PatchPath, [switch] $Reverse) {
    $GitArgs = @("-C", $GgmlDir, "apply")
    if ($Reverse) { $GitArgs += "--reverse" }
    $GitArgs += @("--check", $PatchPath)

    $PreviousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & git @GitArgs 2>$null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        $ErrorActionPreference = $PreviousErrorActionPreference
    }
}

# Local patches under eng/ggml-patches, applied in name order after a clone, after an
# update and on the no-update path; already-applied patches are recognised by applying
# in reverse, and one that fits neither way is an error (see fetch-ggml.sh).
function Invoke-LocalPatches {
    $patchDir = Join-Path $PSScriptRoot "ggml-patches"
    if (-not (Test-Path $patchDir)) { return }
    foreach ($patch in (Get-ChildItem -Path $patchDir -Filter *.patch | Sort-Object Name)) {
        if (Test-GitPatchApplies -PatchPath $patch.FullName -Reverse) { continue }
        if (Test-GitPatchApplies -PatchPath $patch.FullName) {
            git -C $GgmlDir apply $patch.FullName
            if ($LASTEXITCODE -ne 0) { throw "git apply $($patch.Name) failed" }
            Write-Host "ggml: applied $($patch.Name)"
            continue
        }
        $sha = (git -C $GgmlDir rev-parse --short HEAD)
        throw "ggml: $($patch.Name) applies to neither direction of the checkout at $sha; update the patch under $patchDir or pin TENSORSHARP_GGML_GIT_REF to a revision it fits."
    }
}

function Test-Truthy([string] $Value) {
    return $Value -match '^(1|ON|on|On|TRUE|true|True|YES|yes|Yes)$'
}

if (Test-Path (Join-Path $GgmlDir ".git")) {
    if (Test-Truthy $env:TENSORSHARP_GGML_NO_UPDATE) {
        Write-Host "ggml: TENSORSHARP_GGML_NO_UPDATE set; using existing checkout at $GgmlDir"
        Invoke-LocalPatches
        exit 0
    }

    Write-Host "ggml: updating existing checkout to $GitRef ($GitUrl)"
    # Do not redirect git's stderr (no `2>$null`): git writes normal progress and
    # summaries ("From github.com/...") to stderr, and under
    # $ErrorActionPreference='Stop' redirecting a native command's stderr turns that
    # output into a terminating NativeCommandError even on success (Windows
    # PowerShell 5.1). Letting it pass through keeps the $LASTEXITCODE checks below
    # working for both the success and offline (non-zero exit) paths.
    git -C $GgmlDir remote set-url origin $GitUrl
    git -C $GgmlDir fetch --depth 1 origin $GitRef
    if ($LASTEXITCODE -eq 0) {
        git -C $GgmlDir reset --hard FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw "git reset failed" }
        $sha = (git -C $GgmlDir rev-parse --short HEAD).Trim()
        Write-Host "ggml: now at $sha"
    }
    else {
        Write-Warning "ggml: could not fetch $GitRef (offline?); using existing checkout"
    }
    Invoke-LocalPatches
    exit 0
}

# No checkout yet: clear a partial/empty directory left by a failed clone.
if (Test-Path $GgmlDir) {
    Remove-Item -Recurse -Force $GgmlDir
}

Write-Host "ggml: cloning $GitUrl ($GitRef) into $GgmlDir"
# No `2>$null` here either: git clone reports progress on stderr, which would
# otherwise become a terminating error under $ErrorActionPreference='Stop'.
git clone --depth 1 --branch $GitRef $GitUrl $GgmlDir
if ($LASTEXITCODE -ne 0) {
    # --branch only accepts a branch or tag; fall back to fetching an explicit
    # commit ref shallowly.
    if (Test-Path $GgmlDir) { Remove-Item -Recurse -Force $GgmlDir }
    git init -q $GgmlDir
    if ($LASTEXITCODE -ne 0) { throw "git init failed" }
    git -C $GgmlDir remote add origin $GitUrl
    git -C $GgmlDir fetch --depth 1 origin $GitRef
    if ($LASTEXITCODE -ne 0) { throw "git fetch '$GitRef' failed" }
    git -C $GgmlDir checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed" }
}
$sha = (git -C $GgmlDir rev-parse --short HEAD).Trim()
Write-Host "ggml: cloned at $sha"
Invoke-LocalPatches
}
finally {
    if ($HasFetchMutex) {
        $FetchMutex.ReleaseMutex()
    }
    $FetchMutex.Dispose()
}
