# TensorAgent (iOS)

TensorAgent is the iPhone/iPad app built on TensorSharp: a .NET MAUI
(`net10.0-ios`) head that links the TensorSharp engine statically
(`GgmlOps.xcframework`) and shows the TensorSharp.Server Web UI in a WKWebView,
served by an in-process loopback HTTP server.

This directory currently holds the **F0 spike**: the app skeleton plus the two
risky pieces everything else depends on, proven end to end in the iOS
simulator. It is not yet a chat app - the API behind the Web UI is a stub (see
"What works" below).

```
TensorAgent/
  TensorAgent.slnx                     solution (only the MAUI head for now)
  README.md
  scripts/build-sim.sh                 xcframework (if missing) + simulator build
  scripts/run-sim.sh                   install + launch in the simulator, stdout to the terminal
  scripts/verify-sim.sh                curl the running app's loopback API from the Mac
  src/TensorAgent.Maui/
    TensorAgent.Maui.csproj            net10.0-ios, MAUI SingleProject
    App.cs / AppShell.cs / MainPage.cs native chrome: status bar + WebView (C#, no XAML)
    MauiProgram.cs                     DI: LoopbackWebHost, MainPage, AppShell
    Compute.cs                         picks GgmlMetal / GgmlCpu once per process
    Hosting/LoopbackWebHost.cs         System.Net.HttpListener on 127.0.0.1, token-gated /api
    Hosting/EngineProbe.cs             proves the static GgmlOps link (GET /api/engine)
    Platforms/iOS/                     Info.plist, Entitlements.plist (device only), AppDelegate, Program
    Resources/                         app icon + splash
```

## What works (verified 2026-09-02, iPhone 17 Pro simulator, iOS 26.5, Xcode 26.6)

- **Static engine link.** `GgmlOps.xcframework` (GgmlOps + ggml + ggml-cpu; the
  device slice also carries ggml-metal with embedded shader source) is linked
  with `NativeReference Kind=Static ForceLoad=True` and
  `-Wl,-exported_symbol,_TSGgml_* -Wl,-exported_symbol,_ggml_*`. The
  simulator executable exports 248 `TSGgml_*` and 878 `ggml_*` symbols;
  `GgmlNative.ImportResolver` resolves `DllImport("GgmlOps")` to the main
  program handle and `GgmlBasicOps.CanInitializeBackend(Cpu)` returns true from
  inside the app - no `DllNotFoundException`, no `EntryPointNotFoundException`.
- **Engine assemblies load** under Mono AOT + `TrimMode=partial`:
  TensorSharp.Core, Runtime, Models, Backends.GGML and AgentHost are all
  referenced, rooted (`TrimmerRootAssembly`) and reported by `/api/engine`.
- **Loopback Web UI.** `LoopbackWebHost` binds `http://127.0.0.1:<random port>/`
  with `System.Net.HttpListener` (its managed socket implementation ships in
  the iOS Mono runtime pack; ASP.NET Core has no iOS runtime pack) and serves
  `TensorSharp.Server/wwwroot` **byte for byte** - the csproj links the Server's
  wwwroot into the bundle as `webui/` rather than copying it, so the UI can
  never fork. The WebView renders it and runs its normal load sequence
  (`GET /`, `GET /api/models`, `POST /api/sessions`, 1 s `/api/queue/status`
  poll).
- **Demo round trip.** `POST /api/chat` streams a canned reply as SSE frames in
  the exact wire format of the desktop `SseWriter` (`data: {json}\n\n`, flushed
  per frame): one `thinking` frame, one `token` frame per word, then `done`. The
  unmodified index.html renders the reasoning block, the streamed text and the
  stats line (see the screenshot the spike was signed off with:
  "52 tokens · 1.9s · 27.0 tok/s").
- **Token gate.** The port is loopback-only but every app on the device can reach
  loopback, so `/api/*` requires a per-launch secret. The WebView's first
  navigation is `/?token=<hex>`, which sets an `HttpOnly; SameSite=Strict`
  cookie; every later `fetch()` carries it automatically. Requests without it
  (or with the wrong one) get `403`. From the Mac, pass it as the
  `X-TensorAgent-Token` header (Debug builds print the entry URL on stdout).
- **Backend selection** (`Compute.cs`): `GgmlMetal` when the linked archive has
  ggml-metal *and* `MTLDevice.SystemDefault.SupportsFamily(Apple7)`, otherwise
  `GgmlCpu`. Both probes are side-effect free (`CanInitializeBackend` is a
  compile-flag check; the Metal family query uses UIKit's device object), which
  matters because GGML latches one backend per process - there is no
  "try Metal, fall back to CPU". The simulator picks `GgmlCpu` because its
  xcframework slice is built CPU-only (the simulator GPU is Apple1/Apple2).

## What does not work yet

- No model loading, no real chat, no uploads, no skills: `/api/models`,
  `/api/queue/status`, `/api/sessions` are stubs and `/api/chat` is the demo
  stream; `/uploads/*`, `/api/upload`, `/api/skills`, `/api/image-edit/*`,
  `/api/video-generate/*` answer 404. The TensorSharp.Chat extraction (work
  package B) and TensorAgent.Core (package E) plug in behind `LoopbackWebHost`.
- `/api/models` reports `loaded: "demo (no model loaded)"` rather than `null`.
  This is deliberate: index.html refuses to send anything while `loaded` is
  null (`sendMessage`, "No model is configured"), and the demo exists to watch
  the UI complete a turn. It goes away with the real model service.
- The physical device has not been run. The `ios-arm64` Release build is covered
  below; the paired iPhone was not connected.
- Metal (ggml-metal) is exercised only on a device: the simulator slice is
  CPU-only by design.

## Prerequisites

1. **Xcode 26.6** with the iOS 26.5 SDK and the iOS 26.5 simulator runtime.
2. **The user-local .NET SDK with the maui-ios workload**: `~/.dotnet` (SDK
   10.0.400, workload set 10.0.400.1: iOS 26.5.10315, MAUI 10.0.20). The system
   `/usr/local/share/dotnet` has no workloads and fails restore with
   `NETSDK1147` - the error looks like a code break but is purely which
   `dotnet` is on `PATH`. There is no `~/.zshrc`, so export per shell:

   ```bash
   export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
   ```

3. **The ggml checkout** at `ExternalProjects/ggml` (gitignored; fetched once by
   `eng/fetch-ggml.sh`). The iOS build never updates it
   (`TENSORSHARP_GGML_NO_UPDATE=1`).
4. **`TensorSharp.GGML.Native/build-ios/GgmlOps.xcframework`** (gitignored, so a
   fresh clone or worktree does not have it):

   ```bash
   TENSORSHARP_GGML_NO_UPDATE=1 bash TensorSharp.GGML.Native/build-ios.sh   # ~4 min
   ```

   Expect `device: 248 TSGgml_* exports, embedded metallib families: 20, metal init: 1`
   and `sim: 248 TSGgml_* exports, ... metal init: 0`. `scripts/build-sim.sh`
   runs this automatically when the xcframework is missing.

## Simulator recipe

```bash
# 1. Build (Debug, iossimulator-arm64). First build of a fresh worktree ~2 min,
#    incremental ~5 s. 0 errors; the ~260 warnings are the engine's own
#    nullable/analyzer noise plus one PublishFolderType warning for the CUDA .ptx.
TensorAgent/scripts/build-sim.sh

# 2. Boot "iPhone 17 Pro", install and launch with stdout attached. Set
#    TENSORAGENT_DEMO_PROMPT to have the app type and send that prompt once the
#    Web UI has loaded (Debug builds only; simctl cannot type into a WebView).
TENSORAGENT_DEMO_PROMPT="Hello from the simulator" \
  TensorAgent/scripts/run-sim.sh | tee /tmp/tensoragent.log

# 3. From another terminal: the simulator shares the Mac's network namespace,
#    so curl the loopback API. The script reads the port + token from the log.
TensorAgent/scripts/verify-sim.sh /tmp/tensoragent.log

# 4. Screenshot
xcrun simctl io booted screenshot /tmp/tensoragent.png
```

The raw commands the scripts wrap:

```bash
dotnet build TensorAgent/src/TensorAgent.Maui/TensorAgent.Maui.csproj \
    -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
xcrun simctl boot F9358123-B03E-4FB3-B87D-33522845B005 && open -a Simulator
xcrun simctl install booted \
    TensorAgent/src/TensorAgent.Maui/bin/Debug/net10.0-ios/iossimulator-arm64/TensorAgent.Maui.app
SIMCTL_CHILD_TENSORAGENT_DEMO_PROMPT="Hello" \
    xcrun simctl launch --console --terminate-running-process booted ai.tensorsharp.tensoragent
```

Note the bundle is **`TensorAgent.Maui.app`** (MAUI names it after the
assembly, not `ApplicationTitle`), and the bundle id is
`ai.tensorsharp.tensoragent`.

What the app prints on launch (Debug):

```
TensorAgent: Web UI listening on http://127.0.0.1:39829/ (webroot .../TensorAgent.Maui.app/webui, index.html 89400 bytes)
TensorAgent: engine probe {"backend":"GgmlCpu","ggmlCpuAvailable":true,"ggmlMetalAvailable":false,"mainProgramHandleResolved":true,"ggmlVersionOrError":"ok: TSGgml_CanInitializeBackend(Cpu)=True, (Metal)=False; TSGgml_* resolved from the main program image.","engineAssemblies":["TensorSharp.Core 2.3.2.0","TensorSharp.Runtime 2.8.6.0","TensorSharp.Models 2.8.6.0","TensorSharp.Backends.GGML 0.0.0.0","TensorSharp.AgentHost 2.8.6.0"],"gpuName":"Apple iOS simulator GPU","reason":"ggml-cpu: the linked GgmlOps archive has no ggml-metal backend (simulator slice or Metal-less build)."}
TensorAgent: entry URL http://127.0.0.1:39829/?token=e45f8efc32091be1fd964af925865eee
TensorAgent: GET / -> 200
TensorAgent: GET /api/models -> 200
TensorAgent: POST /api/sessions -> 200
```

Hand-rolled curl (token from the entry URL line):

```bash
T='X-TensorAgent-Token: e45f8efc32091be1fd964af925865eee'
curl -s http://127.0.0.1:39829/ | head -3                         # index.html
curl -s http://127.0.0.1:39829/api/engine                         # 403 without the token
curl -s -H "$T" http://127.0.0.1:39829/api/engine                 # engine probe JSON
curl -s -N -H "$T" -H 'Content-Type: application/json' \
     -d '{"messages":[{"role":"user","content":"ping"}],"maxTokens":4096,"think":true}' \
     http://127.0.0.1:39829/api/chat                              # SSE frames
curl -s -H "$T" -X POST -d '' http://127.0.0.1:39829/api/sessions # note -d '': see below
```

`POST` without a body: curl sends no `Content-Length` and HttpListener answers
`411 Length Required`. WKWebView's `fetch()` sends `Content-Length: 0`, which is
why the UI's own `POST /api/sessions` is fine; use `-d ''` from curl.

## Loopback API (stub)

| Route | Answer |
| --- | --- |
| `GET /` (`?token=` sets the cookie) | bundled `webui/index.html` |
| `GET /<static>` | files under `webui/` (`/images/assistant_logo.png` etc.), path-traversal safe |
| `GET /api/engine` | `{backend, ggmlAvailable:{cpu,metal}, mainProgramHandleResolved, ggmlVersionOrError, engineAssemblies, gpuName, reason, webRoot}` |
| `GET /api/models` | `{loaded:"demo (no model loaded)", models:[], architecture:"demo", loadedBackend, defaultMaxTokens:4096, skills:null}` |
| `GET /api/queue/status` | `{busy:false, processing:0, pending_requests:0, total_processed:0}` |
| `POST /api/sessions` | `{sessionId}` |
| `DELETE /api/sessions/{id}` | `{ok:true}` |
| `POST /api/chat` | `text/event-stream`: `{thinking}`, `{token}`…, `{done:true, sessionId, tokenCount, elapsed, tokPerSec, promptTokens:0}` |
| `GET /uploads/*`, anything else | `404 {error}` |
| any `/api/*` without the token | `403 {error}` |

## Device build (ios-arm64, Release)

```bash
dotnet build TensorAgent/src/TensorAgent.Maui/TensorAgent.Maui.csproj \
    -f net10.0-ios -p:RuntimeIdentifier=ios-arm64 -c Release
```

stops in 2 s at `_DetectSigningIdentity` ("Could not find any available
provisioning profiles for TensorAgent.Maui on iOS") before anything is compiled,
because this Mac has no development certificate/profile for the bundle id. To
reach the native link without signing:

```bash
dotnet build ... -p:RuntimeIdentifier=ios-arm64 -c Release -p:EnableCodeSigning=false
```

DEVICE_BUILD_RESULT

For a real device build pass `-p:CodesignKey="Apple Development: ..."
-p:CodesignProvision=<profile>`; the profile must enable the two entitlements in
`Platforms/iOS/Entitlements.plist` (`com.apple.developer.kernel.increased-memory-limit`,
`com.apple.developer.kernel.extended-virtual-addressing`), which the csproj wires
up for the `ios-arm64` RID only - the simulator has no jetsam limit and
`CompileEntitlements` must not emit codesign entitlements for simulator builds.

## Build notes

- `TensorSharpSkipGgmlNative` / `TensorSharpSkipMlxNative` are set in the csproj
  and forwarded to the ProjectReferences as global properties
  (`AdditionalProperties`), since a plain property never crosses a
  ProjectReference. On the very first build of a fresh worktree the MLX backend's
  desktop CMake build (`build-native-macos.sh`, several minutes) was still
  observed to run once through the transitive TensorSharp.Models -> MLX
  reference; `scripts/build-sim.sh` therefore also exports
  `TENSORSHARP_GGML_NATIVE_SKIP=true` and `TENSORSHARP_MLX_NATIVE_SKIP=true`,
  which the backend csprojs honour however they are reached.
- TensorSharp.Models still references OpenCvSharp4 / Magick.NET (no ios-arm64
  natives). The managed assemblies restore and bundle fine; nothing in this
  spike calls them. Package C conditions them out.
- The Web UI's `/api/chat` abort is "close the HTTP stream"; `LoopbackWebHost`
  treats the resulting write failure as normal completion, so cancelling a
  demo stream mid-way is silent, as on the desktop server.
- Guarded by `InferenceWeb.Tests/TensorAgentMauiProjectTests.cs` (net10.0, no
  workload needed): the NativeReference and its linker flags, the engine
  references + trimmer roots, the wwwroot link (no forked `index.html`), the
  ATS loopback exception and usage strings, and the device-only entitlements.
