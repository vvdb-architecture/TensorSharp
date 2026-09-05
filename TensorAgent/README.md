# TensorAgent

An iPhone and iPad app with the full functionality of TensorSharp.Server's Web UI
chat, running entirely on the device: a .NET MAUI (`net10.0-ios`) head that links
the TensorSharp engine statically, serves the Server's own `wwwroot/index.html` to
a WKWebView from an in-process loopback HTTP server, and answers that page's API
with the same chat pipeline the desktop uses.

This is the current source implementation of TensorSharp's iOS/iPadOS target.
Physical devices use the GGML Metal (`ggml_metal`) backend; build it with
`TensorSharpIosTargets=true`. It is not a remote client or a separate inference
engine. The latest tagged desktop release may not include TensorAgent yet, so
follow the source-build instructions below.

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
interruption and are verified before use, and they belong to the APP rather than to
the screen that started one — see "Downloads" below. Both families and both
architectures are covered:

| Model | Architecture | Download | Needs | Hugging Face repo |
| --- | --- | --- | --- | --- |
| Gemma 4 E2B (Q8_0) | dense | 5.52 GB | 12 GB | `ggml-org/gemma-4-E2B-it-GGUF` |
| Gemma 4 E4B (UD-Q4_K_XL) | dense | 5.79 GB | 12 GB | `unsloth/gemma-4-E4B-it-GGUF` |
| Gemma 4 E4B (Q8_0) | dense | 8.59 GB | 16 GB | `ggml-org/gemma-4-E4B-it-GGUF` |
| Qwen3.5 9B (UD-Q4_K_XL) | dense | 6.89 GB | 12 GB | `unsloth/Qwen3.5-9B-GGUF` |
| Qwen3.8 27B (UD-IQ2_XXS) | dense | 8.20 GB | 12 GB | `unsloth/Qwen3.8-27B-GGUF` |
| Gemma 4 26B-A4B (UD-IQ2_XXS) | mixture of experts | 11.11 GB | 16 GB | `unsloth/gemma-4-26B-A4B-it-GGUF` |
| Qwen3.6 35B-A3B (UD-IQ1_M) | mixture of experts | 10.95 GB | 16 GB | `unsloth/Qwen3.6-35B-A3B-GGUF` |
| Qwen-Image-Edit 2511 (Q2_K) | diffusion | 12.32 GB | 12 GB | `unsloth/Qwen-Image-Edit-2511-GGUF` + 3 companions |

The catalog is filtered by the device's own memory, so a phone is never offered a
model it cannot load.

**Many chats, kept, and one tap away.** The Web UI holds its history in the page and
nowhere else, which is fine for a desktop tab and useless on a phone that is suspended
and killed constantly. Transcripts are written on the host side instead, indexed, and
resumable: opening a saved chat re-renders it through the page's own bubble builders and
continues it, in place, without reloading the page.

The menu is a drawer from the LEFT edge and the saved chats are IN it, newest first —
not a row called "Chats" leading to a second screen. A chat someone has already had is
the thing the menu is opened for; a bottom sheet fits five rows and could only ever
offer the word. "All chats" is still there for renaming and deleting.

**Multi-modal input.** Photos, camera capture, video and files reach the same chat
service the paperclip's `/api/upload` reaches, so a native attachment and an in-page one
are the same thing by the time a message is sent — but the native ones are handed to it
DIRECTLY rather than posted over the loopback socket. The round trip was moving a file
this process already had, to a server inside this same process, through a hand-written
multipart parser; when the parser dropped a body the answer was "no file was uploaded"
and it named none of the five places that could have happened. HEIC works, which matters
because it is the iPhone camera's default format — and so does a photo that arrives with
no file extension at all, which is what iOS's picker actually hands over
(`UploadNaming`).

**Voice, by gesture.** Hold the message box for half a second and the composer
becomes one large hold-to-talk button; hold it, speak, release, and the transcription
lands in the message box for you to read before sending. A keyboard button beside it
goes back to typing. Recognition is Apple's, asked for on-device.

iOS recognises ONE language per session and cannot detect which is being spoken, so
the chips beside the button choose it — and "Auto" means the first of *your* preferred
languages this device can recognise, not the region your phone formats dates in.
Those are different things, and the difference is not subtle: a phone set to the United
States reports `en_US` however many languages its owner has added, and an English
recogniser does not fail on Mandarin — it succeeds, and hands back the sounds
romanised.

**A live sign that it is working, and a trace of what it did.** A turn can spend a
minute between the question and the first word of the answer — reading a skill, writing
a program, running it — and for all of that the reply bubble is empty. Two things fill
it, both on by default:

- *What it is doing now.* A strip pinned directly ABOVE the message box names the step
  ("Running code… 12s", the seconds ticking) and shows the last three lines of whatever
  the model is producing: its reasoning, the command it is typing, or that command's
  output as it prints. Pinned, because a turn's activity starts at the top of the turn
  and by the time a program has been run the answer is streaming several screens below
  it — so the one question the user has ("is it stuck?") was the one thing they had to
  scroll away from the answer to find out. Above rather than below, because the
  composer is anchored to the bottom of the screen: growing it upwards leaves the box
  the thumb aims at exactly where it was.
- *What it has done.* One line per finished step, kept — `Reading skill ·
  brand-guidelines · SKILL.md`, `Running code · python3 -c "from datetime…" · 3s`,
  with a red dot when a step failed — and a link for every file a script produced,
  rendered from the frame that reports it rather than from the model remembering to
  mention it. The desktop page deletes its activity block and keeps no history; on a
  phone that trace IS the answer to "what did it just spend a minute on".

The whole of the reasoning stays one tap away in the collapsed box above.

**One row of chrome, and everything else in the menu.** The composer is a "+", the
message box and Send — nothing else. Reasoning is a Settings choice ("Show reasoning by
default"), Skills is a ☰ menu item beside Chats and Models, and the dictation language
appears only in voice mode. Each was a permanent control for something decided rarely,
on the one row a phone composer has.

**The answer keeps being written while you are somewhere else.** A generation belongs to
the app, not to the HTTP request that asked for it (`ChatTurnManager`). This is not a
refinement: iOS suspends a WKWebView's content process the moment its view leaves the
window, which is what opening the model list does, so the page stops reading — and a
server that took that as "nobody wants this any more" threw away the minute the user had
just waited. Now the turn runs on, buffers what it produces, and the page ATTACHES to it
again when it comes back, replaying from the first frame; the display is held awake and
a background-task assertion is taken for as long as the model is working, both driven by
the host's own answer rather than by whichever page happens to be watching. Stopping is
something the Stop button asks for explicitly. The transcript is written by the turn, so
an answer that finishes with nobody reading is still there.

**The model you last used is loaded at launch.** The app has always remembered the
choice and then done nothing with it until you went back to the Models list and tapped
"Use" again, so every launch began at "No model yet" with a send button that refuses.
The weights are read on a background thread while the page paints, and the header says
"Loading Gemma 4 E2B…" until they are in — which is a different sentence from "no model
has ever been chosen", and asks the user for something different.

**The sandbox switches take effect now.** "Run code" and "Allow network access" used to
apply "the next time TensorAgent starts", which is honest and useless: leaving an iPhone
app does not restart it, so the real instruction was "force-quit from the app switcher"
and the switch read as one that did nothing — network turned on, `curl` still answering
"network access is disabled by the user". `AgentAppHost.ApplySettings` moves all four
holders together (the runner's options, the installer's standing policy, the shell's
host list, and the terms a skill's scripts are planned against). A command already
running keeps the terms it started with.

**Skills.** Twelve are bundled, chosen by inspection rather than by hope — see
"Skills" below. Users can install more from a zip.

**Code, generated and run.** The agent host's shell tool works here, backed by an
in-process POSIX shell, an embedded CPython 3.13 and JavaScriptCore, because iOS
allows no child processes at all.

A missing command never ends in "command not found" and nothing else. Installing a
native program is available to nobody here — iOS runs no child processes and will not
execute a binary that was not signed into the bundle — so the shell names what does
work instead: `$(( ))` and `python3` for `bc`, the interpreters for another language,
the fact that `apt`/`brew`/`sudo` have no meaning on this device *and* that Python and
JavaScript packages do install with `pip`/`npm` when network access is on. It also
catches a transposed name. This is not politeness: a dead end is where a model stops
using the shell and starts inventing the answer, which is exactly what one did on a
phone — reaching for `bc` to subtract two dates, being told 127, and finishing the
arithmetic in its head with the wrong number and a formula underneath.

**A sandbox the user controls.** Two switches, both in Settings, both defaulting to
the safe answer: code execution on, because an agent that cannot act is not an
agent, and network off, because a model that can reach the internet from inside a
sandbox is a different risk entirely. Every setting on that page does something;
one that could not be enforced was removed rather than left there implying it was.

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
                                            # (also takes a DEVICE log: a phone's 127.0.0.1
                                            #  is the phone's, so it skips the API half and
                                            #  checks everything the app logged about itself)
```

Five environment variables drive a Debug build from a script, because neither
`simctl` nor `devicectl` can tap or type:

| | |
| --- | --- |
| `TENSORAGENT_START_PAGE=models` | open a page other than the chat |
| `TENSORAGENT_USE_MODEL=<catalog id>` | load a model, as tapping "Use" would |
| `TENSORAGENT_DEMO_PROMPT=<text>` | type a prompt into the composer and send it |
| `TENSORAGENT_UI_CHECK=1` | drive the composer's gestures and the menu, one line per check |
| `TENSORAGENT_OPEN_MENU=1` | leave the menu open, so it can appear in a screenshot |
| `TENSORAGENT_NAV_CHECK=1` | leave the chat mid-answer for `TENSORAGENT_NAV_SECONDS` (15) and report whether the answer carried on |
| `TENSORAGENT_NETWORK_CHECK=1` | flip the network switch both ways and run `curl` after each, then put it back |
| `TENSORAGENT_DOWNLOAD=<catalog id>` | start a download and log it, stopping after `TENSORAGENT_DOWNLOAD_SECONDS` (60) |

Two of those exist because the claim they check has no other witness. `TENSORAGENT_NAV_CHECK`
is the only way to see that a generation survives the chat leaving the screen: iOS suspends
a WKWebView's content process the moment its view leaves the window, and nothing off-device
reproduces that. `TENSORAGENT_NETWORK_CHECK` is the only way to see that flipping the
network switch changes what the very next command can do, in one running process — which is
the whole of the bug it guards. A large upload is posted to the app's own `/api/upload` on
every Debug launch for the same reason: the multipart parser's fault only appeared when a
single read filled its buffer, which is what iOS's HTTP client does and no test host did.

A whole round trip on a real iOS runtime is therefore scriptable: link a GGUF into
the app's model directory, launch with `TENSORAGENT_USE_MODEL` and
`TENSORAGENT_DEMO_PROMPT`, and `verify-sim.sh` reads the result out of the log.

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
    Downloads/      resumable, verified downloads, and the manager that owns them
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

**One interpreter, started once, and everyone waits for it.** CPython is a
process-wide singleton here, and `EmbeddedPython` starts it on first use. Publishing
"already tried" before the work rather than after it made the fast path a window into a
half-started interpreter — and the app opens that window on every launch, because the
page fetches `/api/agent/engine` as it loads (which asks for the version, which starts
CPython) while the self-test runs `python3` on another thread. The visible cost was a
model being told "no Python interpreter is embedded in this build" by a build that has
one, on the first command of a session, after which it stops reaching for the shell.

**No child processes.** `Process.Start` is unsupported on iOS, so the agent host's
`IShellBackend` seam is filled by an in-process interpreter. Confinement moves from
the kernel to the runtimes: every path goes through `ConfinedPaths`, every network
call consults the policy first. The backend reports that honestly, including the
one thing it genuinely cannot do — preempt a builtin already inside a long call.

**The transcript is the host's, and it carries the attachments.** The Web UI keeps
its history in the page and nowhere else; on a phone the app is suspended and killed
constantly, so it is written on this side instead. What that costs is that the page
has to send everything worth keeping, and for a while it did not: a message's file
paths went to the model and nothing about them went to the transcript, so a chat with
a photo in it reopened as the words with a blank where the picture had been. The page
now sends an `attachments` array — the stored name, the name the user knows it by,
what kind of thing it is — and every URL in a saved chat is derived from that stored
name rather than remembered, so a transcript cannot point at an address that has
moved. The same array is what the host stages into the working directory of anything
the model runs, which is how "make a PDF of this photo" became a thing that works:
before it, only TEXT uploads were staged, so the model could see the picture and had
no file to open.

**Weights are not backed up.** Models go under `Library/Caches`, excluded from
iCloud; conversations, settings and installed skills go under
`Library/Application Support`, which is backed up. A five-gigabyte byte-identical
copy of a public file has no business in a user's iCloud quota.

**Downloads outlive the screen that started them.** `ModelDownloadManager` owns every
transfer for the life of the app: the model list attaches to a running job when it
opens and detaches when it closes, `POST /api/agent/catalog/{id}/download` is a window
on the job rather than its owner, and only `…/download/cancel` stops one. Leaving for
the chat, opening any other page, or dropping the progress stream now costs nothing.

Leaving the APP is the part iOS decides. A background-task assertion is held while
bytes are moving, which buys a while rather than an exemption — long enough to glance
at a message, not long enough for five gigabytes. What makes that survivable is that
nothing is ever lost: every file is written through its `.part`, so a transfer the
system does eventually stop resumes from the byte it reached, and the app restarts it
by itself when it comes back to the foreground. The user never taps twice.

**Metal.** On a device `ggml_metal` is the default and the first backend offered.
The simulator slice has no Metal at all — the simulator GPU is Apple1/Apple2 and
has no `simdgroup_matrix` — so there it is not offered, and CPU is the default.
The page is never shown a backend the build cannot initialise: a default that does
not exist puts the user one tap from a load that fails.

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

**The switch.** The skills sheet carries a master toggle above the list. Off is not
"nothing is ticked": `ServerHostingOptions.SkillsEnabled` makes the request planner
build no plan at all, so no skill is declared to the model and none is reachable.
That is what the switch is for — twelve skills announce themselves in every prompt,
which on a phone is thousands of tokens on every turn of every chat, and a user who
wants a plain assistant should be able to have one. It applies to the next message,
not the next launch.

**`research` was rewritten.** The old one had a search script that was a client for
an endpoint the user was expected to configure, plus one unauthenticated fallback
that answers a phone with a challenge page more often than with results — so every
research request began by asking the person who wanted research to supply the URLs.
The new one asks nine keyless, machine-readable services at once (Wikipedia,
DuckDuckGo, Marginalia, Hacker News, arXiv, Crossref, GitHub, Stack Overflow, Google
News' RSS), merges what they say, reads the best pages, and writes a dossier with
every source cited. Results are ranked by how much of the question the title and
snippet cover, then by how many independent indexes named the same page: agreement
between indexes that share no crawler is the only quality signal available without a
ranker, and relevance is what stops a keyword index's confidence outranking it —
asked "what is the Kessler syndrome and is it happening", MediaWiki's first answer is
the article on mental disorders. `analyze.py` then sorts the collected sources into
those that state a claim, those that state it with a denial or a hedge nearby, and
those that never mention it, quoting the sentence and the source for each.

Providers fail individually and often; that is designed for rather than hidden. A
challenge page is detected and refused by name rather than parsed, because the
alternative is reporting an engine's own navigation as the user's sources.

## Tests

```
dotnet test TensorAgent/tests/TensorAgent.Tests/TensorAgent.Tests.csproj
```

Hermetic by default. Four groups need something the machine may not have and say
so rather than passing silently:

| Set | Enable with |
| --- | --- |
| Live CPython | `TENSORAGENT_PYTHON_ROOT=<a staged slice or a CPython 3.13 prefix>` |
| End-to-end chat | `TENSORAGENT_TEST_MODEL_DIR=<a directory of catalog GGUFs>` (and `TENSORAGENT_TEST_MODEL_FILE` for a differently named copy) |
| Metal lifetime | the same weights, plus a Mac whose GgmlOps was built with ggml_metal |
| Media parity | the desktop provider's packages |
| The open web | `TENSORAGENT_ALLOW_NETWORK_TESTS=1` — these ask the real internet a real question |

The live-CPython classes share one queue (`LivePythonCollection`). There is exactly
one interpreter per process and one sandbox policy inside it, so running those
classes in parallel had each overwriting the others' permissions: twenty-one tests
failed with messages naming a different test's temporary directory, and a machine
that had staged an interpreter looked broken while one that had not passed the whole
suite.

`WebUiPageTests` RUNS the page. `tensoragent.js` is loaded into JavaScriptCore on
top of `PageDom.js` — a DOM the size of what the script touches, plus a fetch that
answers from a table and records every request — and then driven the way a person
drives it: attach something, send it, reopen the chat. Everything else that guards
that file reads it as text, which cannot answer whether a photo comes back when a
saved chat is opened again. It did not, and six of those seven tests fail against
the version before the fix.

The Metal set is about teardown rather than answers. ggml-metal's device is a C++
static whose destructor asserts that every residency set has been handed back, so a
buffer our side forgets to release does not fail a test — it aborts the process at
exit, after the run reported success. These load, generate, unload and switch on
Metal and measure the device allocation directly, which is both the mechanism behind
that assert and what a phone runs out of. They will not fall back to the CPU, where
none of it exists; without Metal they skip and say so.

The end-to-end set loads a real model and drives the real API: a question answered,
a four-turn conversation, cache reuse and its invalidation, a conversation that
survives a restart, an aborted generation, and a throughput floor. It runs on the
CPU, so budget half an hour for it and do not rebuild the test project while it is
running — that overwrites the assembly under the running host and the failure looks
exactly like a native crash.

Two sets are worth naming because of what they are written against rather than what
they need. `ModelDownloadManagerTests` drives real HTTP transfers through a loopback
range server and asserts the property the whole download rework exists for: a watcher
that walks away does not take the transfer with it. `UploadNamingTests` and the two
upload tests in `MediaRoutesTests` pin the other end of "Upload failed (400)" — a
photo whose name has no extension is placed from its own bytes, and something nobody
can identify is still refused with a sentence a person can read.

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

- **Downloading in the background.** The manager, the routes and the resume are
  covered by tests that move real bytes. The iOS half — the background-task assertion
  and the resume on `willEnterForeground` — is compile-verified only: neither can be
  exercised in a simulator that is never suspended, and how long iOS actually grants
  is a property of a real device under real memory pressure.
- **The native picker's own file names.** `UploadNaming` is tested against the shapes
  iOS produces (a stem with no extension, no content type, HEIC and MP4 bytes behind
  the same absent name), but the picker itself has only been run by hand.
- **Image editing.** Qwen-Image-Edit is in the catalog and `/api/image-edit` is
  bound to the same service the desktop uses, but no image has been generated on
  iOS. At 10.97 GB across four files it needs a 16 GB device and sequential
  load/unload that has not been exercised.
- **Video generation.** The routes exist because they are part of the shared
  surface. No video model is small enough for the catalog, so nothing offers one.
- **Package installation.** `WheelInstaller` refuses without the network switch and
  accepts only pure-Python wheels; the accepting path has not run on iOS.

### On a physical iPhone

An iPhone 17 Pro Max (A19 Pro, 12.26 GB, iOS 26.6.1), Debug build, installed with
`devicectl`:

```
engine probe   backend=GgmlMetal  ggmlMetalAvailable=true  gpu="Apple A19 Pro GPU"
               reason: ggml-metal on Apple A19 Pro GPU (MTLGPUFamilyApple7 present)
memory tier    physical memory 12.26 GB -> catalog tier 12 GB
self-test      all eleven pass, including all four CPython checks
gestures       all seven uicheck lines pass in the phone's own WKWebView
model load     gemma-4-E2B-it-Q8_0 (4.63 GB) + projector on ggml_metal in 22 s
a whole turn   prompt -> shell tool -> in-process CPython -> answer, recorded to the
               conversation store on the device
downloads      157 MB fetched in 40 s, still arriving after the app was sent to the
               background, cancelled cleanly, and the app was not terminated by iOS
```

Two things that run only here and nowhere else: the device slice of the engine (the
simulator's has no Metal at all) and the device staging of CPython, where every
compiled extension module is a signed framework rather than a `.so`.

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

### The composer, in real WebKit

The page's behaviour is the half of this app no unit test can reach: it is JavaScript
in WKWebView, and "is the hold-to-talk button visible" is a layout question with no
answer anywhere else. `TENSORAGENT_UI_CHECK=1` synthesises the gestures in the running
app and logs one line per assertion, which `verify-sim.sh` asserts on:

```
ok voice-switch-gone                     ok holding-the-box-gives-hold-to-talk
ok reasoning-is-a-setting                ok the-keyboard-button-returns
ok skills-moved-to-the-menu              ok the-menu-comes-from-the-left
ok activity-above-the-box                ok the-menu-lists-the-saved-chats
ok a-tap-still-types                     ok a-turn-can-be-taken-back-up
```

The menu is measured rather than asserted about — flush with the left edge, narrower
than the window, as tall as it — because a bottom sheet that had merely been renamed
would pass every check that only looked at class names.

### An answer that outlives the screen it was on

```
navcheck leaving the chat with a turn running · <id>=Running 1 frames
navcheck ok away for 20s: frames 1 -> 43, still generating=True
navcheck ok back in the chat, the answer on screen is 190 characters
```

The frame count rising while the chat is not on screen is the whole claim, and it is
the one thing a unit test cannot make: dropping a reader in a test proves the server
keeps going, and says nothing about what iOS does to the WebView. The last line is the
other half — the page found its way back to the turn rather than showing the half
sentence it walked away from.
