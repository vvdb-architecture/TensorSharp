# TensorAgent

An iPhone and iPad app with the full functionality of TensorSharp.Server's Web UI
chat, running entirely on the device: a .NET MAUI (`net10.0-ios`) head that links
the TensorSharp engine statically, serves the Server's own `wwwroot/index.html` to
a WKWebView from an in-process loopback HTTP server, and answers that page's API
with the same chat pipeline the desktop uses.

Nothing leaves the phone. The model runs locally, the sandbox has no network unless
the user grants it, and dictation asks for on-device speech recognition.

## What it does

**Chat, exactly as the desktop does it.** The page is
`TensorSharp.Server/wwwroot/index.html`, byte for byte, not a port of it. The
routes under it — `/api/chat`, `/api/models`, `/api/sessions`, `/api/upload`,
`/api/skills`, `/api/image-edit`, `/api/video-generate` — are bound to the same
`WebUiChatService` and `SkillsService`, so streaming, tool progress, reasoning
blocks, skill steps and artifact links all behave identically. The app's own
additions are appended to the page as one script tag at request time; the file
itself is never forked.

**A built-in model catalog.** Eight entries chosen to fit a phone, with the exact
byte size and SHA-256 of each file. Downloads resume from a kept `.part` after an
interruption and are verified before use. Both families and both architectures are
covered:

| Model | Architecture | Size | Needs |
| --- | --- | --- | --- |
| Gemma 4 E2B (Q8_0) | dense | 5.52 GB | 12 GB |
| Gemma 4 E4B (UD-Q4_K_XL) | dense | 5.69 GB | 12 GB |
| Gemma 4 E4B (Q8_0) | dense | 8.59 GB | 16 GB |
| Qwen3.5 9B (UD-Q4_K_XL) | dense | 5.97 GB | 12 GB |
| Qwen3.8 27B (UD-IQ2_XXS) | dense | 7.27 GB | 12 GB |
| Gemma 4 26B-A4B (UD-IQ2_XXS) | mixture of experts | 9.92 GB | 16 GB |
| Qwen3.6 35B-A3B (UD-IQ1_M) | mixture of experts | 10.05 GB | 16 GB |
| Qwen-Image-Edit 2511 (Q2_K) | diffusion | 10.97 GB | 16 GB |

The catalog is filtered by the device's own memory, so a phone is never offered a
model it cannot load.

**Many chats, kept.** The Web UI holds its history in the page and nowhere else,
which is fine for a desktop tab and useless on a phone that is suspended and
killed constantly. Transcripts are written on the host side instead, indexed, and
resumable: opening a saved chat re-renders it through the page's own bubble
builders and continues it.

**Multi-modal input.** Photos, camera capture, video and files go through the same
`/api/upload` the paperclip uses, so a native attachment and an in-page one are the
same thing by the time a message is sent. HEIC works, which matters because it is
the iPhone camera's default format. Voice input goes to Apple's recogniser, asking
for on-device recognition.

**Skills.** Twelve are bundled, chosen by inspection rather than by hope — see
"Skills" below. Users can install more from a zip.

**Code, generated and run.** The agent host's shell tool works here, backed by an
in-process POSIX shell, an embedded CPython 3.13 and JavaScriptCore, because iOS
allows no child processes at all.

**A sandbox the user controls.** Two switches, both in Settings, both defaulting to
the safe answer: code execution on, network off.

## Build and run

The user-local SDK is the one with the MAUI workloads:

```
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

One-time preparation, both of which produce files that are not in git:

```
TensorSharp.GGML.Native/build-ios.sh        # GgmlOps.xcframework (device + simulator)
eng/fetch-python-ios.sh                     # CPython 3.13 for iOS
TensorAgent/scripts/prepare-python.sh       # stage the interpreter and its packages
```

Then:

```
TensorAgent/scripts/build-sim.sh            # simulator build
TensorAgent/scripts/run-sim.sh              # install, launch, stream stdout
TensorAgent/scripts/verify-sim.sh           # drive the running app's API from the Mac
```

`TENSORAGENT_START_PAGE=models` opens a page other than the chat, because `simctl`
cannot tap. `TENSORAGENT_DEMO_PROMPT` types a prompt into the composer and sends
it.

A device build additionally needs a signing identity and provisioning profile:

```
dotnet build TensorAgent/src/TensorAgent.Maui/TensorAgent.Maui.csproj \
    -f net10.0-ios -r ios-arm64 -c Release \
    -p:TensorSharpIosTargets=true -p:CodesignKey="Apple Development: ..."
```

`TensorSharpIosTargets=true` must be on the command line rather than only in the
csproj: it decides whether `TensorSharp.Models` builds a `net10.0-ios` slice at
all, and restore resolves a referenced project's target frameworks before a
`ProjectReference`'s `AdditionalProperties` are applied.

## Layout

```
TensorAgent/
  scripts/          build, run and verify; prepare-python.sh; verify-skills.py
  skills/           the bundled skills, plus verdicts.json saying why each one is here
  python-runtime/   staged CPython (not in git; produced by prepare-python.sh)
  src/TensorAgent.Core/
    Catalog/        the model list, the store, install state
    Downloads/      resumable, verified downloads
    Sessions/       conversations, and the recorder that keeps them in step with the page
    Settings/       the two sandbox switches and the rest
    Hosting/        the loopback server, the route table, and AgentAppHost
    Shell/          the in-process POSIX shell and the agent host's backend over it
    Sandbox/        ExecutionPolicy and ConfinedPaths, shared by all three runtimes
    Python/         embedded CPython and the wheel installer
    JavaScript/     JavaScriptCore over its C API, with Node-shaped globals
    WebUi/          the script appended to the Server's page
  src/TensorAgent.Maui/
    MainPage        the WebView, the attachment row, dictation
    Pages/          models, chats, settings
    Hosting/        where the files live on this device; the engine and media probes
  tests/TensorAgent.Tests/
```

`AgentAppHost` is deliberately in the platform-neutral project. An iOS app cannot
be unit-tested from a terminal, so the wiring is only ever checked if it can be
started, driven over real HTTP and torn down on a development machine.

## How it differs from the desktop, and why

**No ASP.NET Core.** There is no iOS runtime pack for it, so the transport is
`System.Net.HttpListener` and the routes are bound by hand. The payloads and the
event-stream framing are byte-compatible with the Server's.

**No child processes.** `Process.Start` is unsupported on iOS, so the agent host's
`IShellBackend` seam is filled by an in-process interpreter. Confinement moves from
the kernel to the runtimes: every path goes through `ConfinedPaths`, every network
call consults the policy first. The backend reports that honestly, including the
one thing it genuinely cannot do — preempt a builtin already inside a long call.

**Weights are not backed up.** Models go under `Library/Caches`, excluded from
iCloud; conversations, settings and installed skills go under
`Library/Application Support`, which is backed up. A five-gigabyte byte-identical
copy of a public file has no business in a user's iCloud quota.

**Metal.** `ggml_metal` is the default and first-offered backend. The simulator
slice has no Metal — the simulator GPU is Apple1/Apple2 and has no
`simdgroup_matrix` — so it runs on `ggml_cpu` and the engine probe says so rather
than pretending.

## Skills

`scripts/verify-skills.py` decides what is bundled, by parsing every script and
resolving each import against the staged interpreter. It refuses anything reaching
for a capability iOS does not have. Twelve of nineteen pass; `skills/verdicts.json`
records every verdict.

The seven that do not, and what blocks each:

| Skill | Blocked by |
| --- | --- |
| docx, pptx, xlsx | `lxml` and `defusedxml` are not in the bundled runtime, and the validators shell out to LibreOffice |
| pdf | `pdfplumber` is missing, and `pdf2image` shells out to poppler |
| skill-creator | `subprocess`, `webbrowser` |
| webapp-testing | `playwright` needs a browser engine |
| mcp-builder | an MCP server needs a process and a socket |

Importable is not the same as usable: `subprocess` is in the standard library and
still cannot work here, so the checker tests unavailability before availability.

## Tests

```
dotnet test TensorAgent/tests/TensorAgent.Tests/TensorAgent.Tests.csproj
```

Hermetic by default. Three groups need something the machine may not have and say
so rather than passing silently:

| Set | Enable with |
| --- | --- |
| Live CPython | `TENSORAGENT_PYTHON_ROOT=<a staged slice or a CPython 3.13 prefix>` |
| End-to-end chat | `TENSORAGENT_TEST_MODEL_DIR=<a directory of catalog GGUFs>` (and `TENSORAGENT_TEST_MODEL_FILE` for a differently named copy) |
| Media parity | the desktop provider's packages |

The end-to-end set loads a real model and drives the real API: a question answered,
a four-turn conversation, cache reuse and its invalidation, a conversation that
survives a restart, an aborted generation, and a throughput floor. It runs on the
CPU, so budget half an hour for it and do not rebuild the test project while it is
running — that overwrites the assembly under the running host and the failure looks
exactly like a native crash.

### Measured

Gemma 4 E4B Q8_0, CPU backend, on a development Mac. The point of these numbers is
the shape, not the absolute value — a phone with Metal is a different machine.

| Turn | Prompt tokens | Reused | Reuse |
| --- | --- | --- | --- |
| 1 | 2812 | 0 | 0% |
| 2 | 2932 | 2908 | 99.2% |
| 3 | 3019 | 2996 | 99.2% |
| 4 | 3105 | 3082 | 99.3% |

Only the new message and the previous answer are processed on each turn. Starting
a new chat drops reuse to zero; rewriting an earlier turn invalidates from the
point the histories diverge, and the model then answers from the rewritten history.

### In the simulator, with a real model

The same Gemma 4 E4B Q8_0, linked into the simulator's model directory and loaded
through the app's own routes, answering through the app's own chat stream:

| | Prompt tokens | Reused |
| --- | --- | --- |
| Turn 1 | 5189 | 0 |
| Turn 2 | 5238 | 5221 (99.7%) |

The transcript was written to the app's container and listed by
`/api/agent/conversations`. Throughput there is not worth quoting: the simulator
has no Metal, and the prompt is large because all twelve skills declare themselves.

## What has not been verified

Stated plainly, because the rest of this file is written as though everything was
checked and these were not:

- **No physical device.** Everything here ran in the simulator, whose slice has no
  Metal at all. The device slice carries ggml-metal with embedded shader source and
  the engine selects it, but no generation has been timed on real hardware.
- **Image editing.** Qwen-Image-Edit is in the catalog and `/api/image-edit` is
  bound to the same service the desktop uses, but no image has been generated on
  iOS. At 10.97 GB across four files it needs a 16 GB device and sequential
  load/unload that has not been exercised.
- **Video generation.** The routes exist because they are part of the shared
  surface. No video model is small enough for the catalog, so nothing offers one.
- **Package installation.** `WheelInstaller` refuses without the network switch and
  accepts only pure-Python wheels; the accepting path has not run on iOS.

### On-device self-test

Debug builds run a self-test at launch and log one line per check, because the
failures that matter here are not compile errors — an interpreter that links but
cannot find its standard library produces an app that starts perfectly and fails on
first use. On the simulator all eleven pass:

```
ok shell: HELLO                 ok python:numpy: 3
ok shell:files: ab              ok python:pillow: (2, 2)
ok shell:awk: 6                 ok node: 2,4,6
ok python: {"v": [3, 13]}       ok node:print: 2
ok python:stdlib: stdlib ok     ok sandbox:write: Permission denied
                                ok sandbox:network: network access is disabled by the user
```
