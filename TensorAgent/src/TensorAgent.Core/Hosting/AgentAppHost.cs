// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Downloads;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Python;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;
using TensorAgent.Core.Sandbox;
using TensorAgent.Core.Shell;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Chat;
using TensorSharp.GGML;
using TensorSharp.Server;
using TensorSharp.Server.Hosting;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// Where everything the app runs is assembled and wired together.
///
/// <para>
/// The desktop server does this in <c>Program.cs</c> with a dependency-injection
/// container and a hundred command-line flags. A phone has neither, so the same
/// object graph is built here from a directory and a settings file: the model
/// service and its session manager, the skills registry, the code runner over the
/// in-process shell, and the loopback server that puts the Web UI in front of them.
/// </para>
/// <para>
/// It deliberately lives in the platform-neutral project rather than in the iOS
/// head. Everything here is ordinary .NET, so it can be started, driven over real
/// HTTP and torn down by a test on a development machine — which is the only way the
/// wiring gets checked at all, since an iOS app cannot be unit-tested from a
/// terminal.
/// </para>
/// </summary>
public sealed class AgentAppHost : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<IDisposable> _owned = new();

    /// <param name="paths">Where this installation keeps its files.</param>
    /// <param name="webRoot">The bundled copy of the Web UI, or null to serve no static files.</param>
    /// <param name="loggerFactory">Where the engine and the code runner log.</param>
    /// <param name="python">An interpreter to use instead of the default; null discovers one.</param>
    /// <param name="javaScript">An engine to use instead of the default; null discovers one.</param>
    /// <param name="port">A fixed loopback port, or 0 to take a free one.</param>
    /// <param name="backends">What this build can run, best first; null offers Metal then CPU.</param>
    /// <param name="modelService">
    /// The engine service to own, or null to build one. The app never passes one; a
    /// test does, because the shutdown order this class exists to enforce — stop
    /// serving, wait for the engine, then free the weights — is otherwise invisible
    /// from outside, and getting it wrong costs a segmentation fault rather than a
    /// failed assertion.
    /// </param>
    public AgentAppHost(
        AgentPaths paths,
        string? webRoot = null,
        ILoggerFactory? loggerFactory = null,
        IPythonRuntime? python = null,
        IJavaScriptRuntime? javaScript = null,
        int port = 0,
        IReadOnlyList<BackendOption>? backends = null,
        ModelService? modelService = null)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        paths.EnsureCreated();

        Settings = new SettingsStore(paths.SettingsFile);
        AppSettings settings = Settings.Load();

        Models = new ModelStore(paths.ModelsDirectory);
        // The downloads belong to the APP, not to the model list: a five-gigabyte
        // transfer must not end because the user went back to the chat. See
        // ModelDownloadManager.
        Downloads = new ModelDownloadManager(Models, _loggerFactory.CreateLogger("TensorAgent.Downloads"));
        Conversations = new ConversationStore(paths.ConversationsDirectory);
        Catalog = ModelCatalog.ForDevice(paths.DeviceMemoryGB);

        // The two switches the user actually sees. They are read here, at startup,
        // and again on every launch the runner builds, so flipping one in the app
        // takes effect on the next command rather than on the next restart.
        CodeExec = new CodeExecOptions
        {
            Enabled = settings.AllowCodeExecution,
            AllowNetwork = settings.AllowNetwork,
            AllowInstall = settings.AllowNetwork,
            ScratchDirectory = paths.ScratchDirectory,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.ToolTimeoutSeconds, 5, 600)),
        };

        Workspaces = new SessionWorkspaceManager(paths.ScratchDirectory, _loggerFactory.CreateLogger("TensorAgent.Workspaces"));
        Workspaces.SweepOrphans();

        // Both runtimes are discovered rather than required. Each reports its own
        // availability with a reason, the shell repeats that reason when a model tries
        // to use one, and the engine line says so before anything is attempted — so a
        // build without an interpreter is a build that says it has no interpreter,
        // never one that fails halfway through a script.
        Python = python ?? Discover(() => new EmbeddedPython(paths.PythonRuntimeDirectory));
        JavaScript = javaScript ?? Discover(() => new JavaScriptCoreEngine());
        // The installer's own policy answers "can this app install anything at all",
        // which is the user's network switch; each individual install is re-checked
        // against the policy of the launch that asked for it.
        Installer = new WheelInstaller(new ExecutionPolicy(
            AllowScripts: settings.AllowCodeExecution,
            AllowNetwork: settings.AllowNetwork,
            WorkRoot: paths.ScratchDirectory,
            ReadableRoots: Array.Empty<string>(),
            TempRoot: paths.ScratchDirectory));
        Backend = new InProcessShellBackend(Python, JavaScript, Installer, settings.NetworkHosts);
        Artifacts = new CodeArtifactStore(paths.ArtifactsDirectory);
        ShellRunner runner = new(
            CodeExec,
            _loggerFactory.CreateLogger("TensorAgent.CodeExec"),
            Artifacts,
            backend: Backend);
        // Always built, never conditional on the switch. ShellRunner.CanRun reads
        // CodeExec.Enabled every time it is asked, and the request planner offers the
        // code tools only for a runner that says it can run -- so a runner that exists
        // and answers "no" is the same refusal as no runner at all, and it is one the
        // user can lift from the Settings screen without relaunching the app. Building
        // it conditionally is what made both sandbox switches take effect "next time
        // TensorAgent starts", which on a phone reads as a switch that does nothing.
        CodeRunner = new CodeRunnerAdapter(runner, CodeExec);

        Skills = new SkillRegistry(new SkillRegistryOptions
        {
            Roots = new[] { paths.BundledSkillsDirectory, paths.InstalledSkillsDirectory },
            InstallDirectory = paths.InstalledSkillsDirectory,
            // Most of this app's skills ship INSIDE the bundle, which is read-only and
            // is rewritten by every install of the app. Deleting one therefore cannot
            // be done by deleting files: without this record the delete either failed
            // outright or lasted until the next launch. It lives in the data root, so
            // it survives app updates the way the user's conversations do.
            RemovedRecordFile = Path.Combine(paths.DataRoot, "removed-skills.txt"),
        });

        ModelService = modelService ?? new ModelService(_loggerFactory.CreateLogger<ModelService>());
        EngineHasWorkInFlight = EngineIsProcessing;
        Sessions = new SessionManager();
        Uploads = new UploadStoragePolicy(paths.UploadsDirectory);

        Options = BuildOptions(paths, settings, backends);

        // A diffusion entry is five files, not one, and only three of them are found by
        // the scan the pipeline does next to the weights. Publishing all of them here —
        // before anything can ask for a load — is what makes the catalog's file list the
        // whole story rather than most of it. See DiffusionCompanions.
        CatalogModel? selected = settings.SelectedModelId is { Length: > 0 } selectedId
            ? ModelCatalog.Find(selectedId)
            : null;
        IReadOnlyDictionary<string, string> companions = DiffusionCompanions.Publish(
            selected?.Kind == CatalogArchitectureKind.Diffusion ? selected : null, Models, paths.DeviceMemoryGB);
        if (companions.Count > 0)
        {
            _loggerFactory.CreateLogger("TensorAgent.Host").LogInformation(
                "image-generation companions: {Companions}",
                string.Join(", ", companions.Select(c => $"{c.Key}={Path.GetFileName(c.Value)}")));
        }

        Chat = new WebUiChatService(
            ModelService, Sessions, Options, Uploads, Skills,
            CodeRunner!, Workspaces, Artifacts, _loggerFactory);

        // A turn the user just took has to survive the app being closed, and the Web
        // UI page keeps its history only in memory. This is the hook the chat service
        // exposes for exactly that: the transcript is written on the host side, keyed
        // by the conversation the request named.
        Recorder = new ConversationRecorder(Conversations);
        Chat.OnChatRequest = Recorder.Record;

        // A generation belongs to the app, not to the HTTP request that asked for it.
        // See ChatTurnManager: on a phone the reader goes away constantly -- another
        // screen, another app, a display that dimmed -- and every one of those used to
        // be an answer thrown away halfway through.
        Turns = new ChatTurnManager(Recorder);

        SkillsService = new SkillsService(Skills, Options, Uploads, _loggerFactory);

        Server = new LoopbackServer(_loggerFactory.CreateLogger("TensorAgent.Loopback"), port)
        {
            StaticRoot = webRoot,
        };
        Server.MapWebUi(Chat, Options.UploadDirectory, SkillsService, Recorder, chatFrames: null, turns: Turns);
        // Without this the model's "here is your PDF" link 404s: the runner emits
        // /api/code/artifacts/... and nothing served it. See MapCodeArtifacts.
        Server.MapCodeArtifacts(Artifacts);
        Server.MapAgent(
            Catalog, Models, Conversations, Settings, DescribeEngine, RaisePageEvent, Downloads,
            onSettingsChanged: ApplySettings, describeModel: DescribeModelState);
    }

    public AgentPaths Paths { get; }
    public SettingsStore Settings { get; }
    public ModelStore Models { get; }
    /// <summary>Every model download this launch started, independent of any screen.</summary>
    public ModelDownloadManager Downloads { get; }
    public ConversationStore Conversations { get; }
    public IReadOnlyList<CatalogModel> Catalog { get; }
    public CodeExecOptions CodeExec { get; }
    public SessionWorkspaceManager Workspaces { get; }
    public InProcessShellBackend Backend { get; }
    public IPythonRuntime? Python { get; }
    public IJavaScriptRuntime? JavaScript { get; }
    public IInstallHook? Installer { get; }
    public CodeArtifactStore Artifacts { get; }
    public ICodeRunner? CodeRunner { get; }
    public SkillRegistry Skills { get; }
    public ModelService ModelService { get; }
    public SessionManager Sessions { get; }
    public UploadStoragePolicy Uploads { get; }
    public ServerHostingOptions Options { get; }
    public WebUiChatService Chat { get; }
    public ConversationRecorder Recorder { get; }
    /// <summary>Every generation this launch started, independent of any page or request.</summary>
    public ChatTurnManager Turns { get; }
    public SkillsService SkillsService { get; }
    public LoopbackServer Server { get; }

    /// <summary>
    /// Raised for each message the page posts about itself: <c>ready</c>,
    /// <c>conversation</c>, and whatever the injected script adds later. The native
    /// chrome subscribes so the title bar and the sessions list follow what the page
    /// is actually showing rather than what the app last asked it to show.
    /// </summary>
    public event Action<string, System.Text.Json.JsonElement>? PageEvent;

    private void RaisePageEvent(string kind, System.Text.Json.JsonElement message) => PageEvent?.Invoke(kind, message);

    /// <summary>The URL the WebView opens: the page plus the launch token that sets its cookie.</summary>
    public string EntryUrl => Server.EntryUrl;

    public void Start()
    {
        Server.Start();
        LoadSelectedModelInBackground();
    }

    // ---- the model the user last used --------------------------------------------

    /// <summary>Where the automatic startup load has got to.</summary>
    public enum ModelLoadState
    {
        /// <summary>No model has ever been chosen, or its files are not on the device.</summary>
        None,
        /// <summary>The weights are being read right now.</summary>
        Loading,
        /// <summary>A model is loaded and the chat can be used.</summary>
        Loaded,
        /// <summary>The load was tried and refused; <see cref="ModelLoadError"/> says why.</summary>
        Failed,
    }

    private int _autoLoadStarted;

    /// <summary>Where the model this app is meant to be using has got to.</summary>
    public ModelLoadState ModelLoad { get; private set; } = ModelLoadState.None;

    /// <summary>Why <see cref="ModelLoad"/> is <see cref="ModelLoadState.Failed"/>, or null.</summary>
    public string? ModelLoadError { get; private set; }

    /// <summary>Raised whenever <see cref="ModelLoad"/> moves, for native chrome that shows it.</summary>
    public event Action<ModelLoadState>? ModelLoadChanged;

    /// <summary>
    /// Load the model the user last used, without being asked.
    ///
    /// <para>
    /// The app remembers the choice — it has always written <c>selectedModelId</c> — and
    /// then did nothing with it until the user went back to the Models list and tapped
    /// "Use" again. Every launch therefore started at "No model yet" with a send button
    /// that refuses, which is the app apparently forgetting a setting it can see. There
    /// is exactly one model the app should be holding, and this is it.
    /// </para>
    /// <para>
    /// On a background thread, because reading four gigabytes of weights off flash and
    /// handing them to Metal takes seconds and the page has to be able to paint while it
    /// happens. It runs at most once per launch, and it does nothing at all when the
    /// selected model's files are not on the device — a purged cache is "download it
    /// again", never an error at startup.
    /// </para>
    /// </summary>
    public void LoadSelectedModelInBackground()
    {
        if (Interlocked.Exchange(ref _autoLoadStarted, 1) != 0)
            return;

        AppSettings settings = Settings.Load();
        if (settings.SelectedModelId is not { Length: > 0 } id || ModelCatalog.Find(id) is not { } model)
            return;
        if (!File.Exists(Paths.SelectedModelPath(settings)))
        {
            _loggerFactory.CreateLogger("TensorAgent.Host").LogInformation(
                "the last used model {Model} is not on this device; nothing to load", id);
            return;
        }

        SetModelLoad(ModelLoadState.Loading, null);
        _ = Task.Run(() =>
        {
            try
            {
                Console.WriteLine($"TensorAgent: loaded the last used model {model.Id} on {UseModel(model)}");
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger("TensorAgent.Host")
                    .LogWarning(ex, "the last used model {Model} could not be loaded", model.Id);
            }
        });
    }

    private void SetModelLoad(ModelLoadState state, string? error)
    {
        ModelLoad = state;
        ModelLoadError = error;
        try { ModelLoadChanged?.Invoke(state); }
        catch (Exception) { /* a listener must not break the load */ }
    }

    /// <summary>
    /// What the page shows while the weights are still being read: which model, and how
    /// far it has got. Without it the chat says "No model yet" for the whole of a load
    /// that is going perfectly well, and the send button refuses for reasons the user
    /// cannot see.
    /// </summary>
    private object DescribeModelState()
    {
        AppSettings settings = Settings.Load();
        CatalogModel? model = settings.SelectedModelId is { Length: > 0 } id ? ModelCatalog.Find(id) : null;
        return new
        {
            id = model?.Id,
            name = model?.DisplayName,
            state = ModelLoad.ToString(),
            loading = ModelLoad == ModelLoadState.Loading,
            error = ModelLoadError,
        };
    }

    /// <summary>
    /// Take a settings change now rather than at the next launch.
    ///
    /// <para>
    /// "Allow network access" is the one that made this necessary. It is a switch on a
    /// settings screen, and the page under it said the change would apply the next time
    /// the app started — but an iPhone app is not restarted by leaving it, so the honest
    /// instruction was "force-quit TensorAgent from the app switcher", which nobody
    /// does. The reported symptom is exactly what that produces: network turned on,
    /// <c>curl</c> still answering "network access is disabled by the user".
    /// </para>
    /// <para>
    /// Everything a run's permissions are read from is moved here, in one place, so the
    /// four holders cannot drift apart: the code runner's options, the installer's
    /// standing policy, the shell's host allow-list, and the terms a skill's scripts are
    /// planned against. A command already running keeps the terms it started with.
    /// </para>
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CodeExec.Enabled = settings.AllowCodeExecution;
        CodeExec.AllowNetwork = settings.AllowNetwork;
        CodeExec.AllowInstall = settings.AllowNetwork;
        CodeExec.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.ToolTimeoutSeconds, 5, 600));
        // The reply length limit is read by /api/models, which the page re-reads every
        // time it comes back to the front, so moving it here is all it takes for the
        // stepper to mean something before the next launch.
        Options.RepointGenerationDefaults(settings.MaxTokens);

        Backend.NetworkHosts = settings.NetworkHosts;
        if (Installer is WheelInstaller wheels)
        {
            wheels.Policy = wheels.Policy with
            {
                AllowScripts = settings.AllowCodeExecution,
                AllowNetwork = settings.AllowNetwork,
                NetworkHosts = settings.NetworkHosts,
            };
        }

        Options.RepointSandboxPermissions(settings.AllowCodeExecution, settings.AllowNetwork);
        Options.RepointSkills(settings.SkillsEnabled);
        _loggerFactory.CreateLogger("TensorAgent.Host").LogInformation("settings applied: {Engine}", DescribeEngine());
    }

    /// <summary>
    /// One line naming what will actually run a command and what will run a script,
    /// for the status bar and for <c>/api/agent/engine</c>. A user who is told
    /// "python is not available" before writing a script is better served than one
    /// who finds out from a failed run.
    /// </summary>
    public string DescribeEngine()
    {
        var parts = new List<string> { Backend.Describe() };
        // Asked of the runner rather than of the field, because the runner is always
        // there now and it is its answer -- read live from CodeExec.Enabled -- that
        // decides whether the model is offered the tools at all.
        parts.Add(CodeRunner is { CanRun: true } ? "code execution on" : "code execution off");
        parts.Add(CodeExec.AllowNetwork ? "network on" : "network off");
        parts.Add($"{Skills.Skills.Count} skills");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Run a handful of representative commands through the real backend and report
    /// what each did.
    ///
    /// <para>
    /// It exists because the interesting failures on this platform are not compile
    /// errors. An interpreter that links but cannot find its standard library, a
    /// sandbox that refuses a path it should allow, a shell builtin that behaves
    /// differently under ahead-of-time compilation — all of those produce an app that
    /// starts perfectly and then fails the first time a model tries to do anything.
    /// This turns that into a line in the launch log.
    /// </para>
    /// <para>
    /// It runs the checks under the app's real policy, in a throwaway directory, and
    /// it never runs on its own: the caller decides. Nothing here writes outside that
    /// directory, and the network check is expected to be refused.
    /// </para>
    /// </summary>
    public IReadOnlyList<SelfTestResult> SelfTest()
    {
        string root = Path.Combine(Paths.ScratchDirectory, "selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            return new[]
            {
                Check("shell", new[] { "sh", "-c", "echo hello | tr a-z A-Z" }, root, "HELLO"),
                Check("shell:files", new[] { "sh", "-c", "printf 'b\na\n' > f.txt && sort f.txt | tr -d '\n'" }, root, "ab"),
                Check("shell:awk", new[] { "sh", "-c", "echo 'x 2' | awk '{print $2*3}'" }, root, "6"),
                Check("python", new[] { "python3", "-c", "import sys, json; print(json.dumps({'v': sys.version_info[:2]}))" }, root, "[3, 13]"),
                Check("python:stdlib", new[] { "python3", "-c", "import re, zipfile, sqlite3; print('stdlib ok')" }, root, "stdlib ok"),
                // The packages the bundled skills import. A staged wheel whose compiled
                // extension did not make it into the bundle imports fine on a laptop
                // and fails here, which is exactly the failure this catches.
                Check("python:numpy", new[] { "python3", "-c", "import numpy; print(numpy.arange(3).sum())" }, root, "3"),
                Check("python:pillow", new[] { "python3", "-c", "from PIL import Image; print(Image.new('RGB', (2, 2)).size)" }, root, "(2, 2)"),
                Check("node", new[] { "node", "-e", "console.log([1,2,3].map(n => n * 2).join(','))" }, root, "2,4,6"),
                Check("node:print", new[] { "node", "-p", "1 + 1" }, root, "2"),
                Check("sandbox:write", new[] { "sh", "-c", "echo x > /tmp/tensoragent-selftest-escape" }, root, expectFailure: true),
                Check("sandbox:network", new[] { "sh", "-c", "curl https://example.com" }, root, expectFailure: true),
            };
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
        }
    }

    private SelfTestResult Check(string name, string[] argv, string root, string? expected = null, bool expectFailure = false)
    {
        try
        {
            ConfinedResult result = ((IShellBackend)Backend).Run(new ShellLaunch
            {
                Argv = argv,
                WorkingDirectory = root,
                WriteDirectory = root,
                ReadOnlyDirectory = root,
                AllowNetwork = false,
                Timeout = TimeSpan.FromSeconds(30),
            });

            string output = (result.Stdout + result.Stderr).Trim();
            bool ok = expectFailure
                ? !result.Ok
                : result.Ok && (expected is null || output.Contains(expected, StringComparison.Ordinal));

            // A Python traceback puts the useful sentence last and the useless frames
            // first, so a failure is reported from the end. A one-line log entry that
            // says "Traceback (most recent call last):" and nothing else is worthless.
            string detail = ok || !output.Contains('\n')
                ? output
                : output[(output.LastIndexOf('\n') + 1)..];
            return new SelfTestResult(name, ok, detail.Length > 240 ? detail[..240] + "…" : detail);
        }
        catch (Exception ex)
        {
            return new SelfTestResult(name, false, ex.Message);
        }
    }

    /// <summary>
    /// Build a runtime, and treat "it could not be built" as an absence rather than
    /// as a crash. A missing framework, a bundle staged without its interpreter or a
    /// platform that has neither must cost the app its scripting, not its startup.
    /// </summary>
    private T? Discover<T>(Func<T> build) where T : class
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger("TensorAgent.Runtimes")
                .LogWarning(ex, "{Runtime} is not available on this host", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// The backends this build can actually offer, best first.
    ///
    /// <para>
    /// The default names Metal first because that is the point of running on a phone.
    /// It is a parameter rather than a constant because it is not always true: the
    /// simulator's slice of the engine has no Metal at all, and a page whose default
    /// backend does not exist puts the user one tap from a load that fails. The iOS
    /// head passes what its probe found.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BackendOption> DefaultBackends { get; } = new[]
    {
        new BackendOption("ggml_metal", "GPU (Metal)"),
        new BackendOption("ggml_cpu", "CPU"),
    };

    private static ServerHostingOptions BuildOptions(
        AgentPaths paths, AppSettings settings, IReadOnlyList<BackendOption>? backends) => new(
        startupModelPath: paths.SelectedModelPath(settings),
        startupMmProjPath: paths.SelectedProjectorPath(settings),
        defaultBackend: (backends ?? DefaultBackends)[0].Value,
        supportedBackends: backends ?? DefaultBackends,
        defaultMaxTokens: settings.MaxTokens,
        maxTokensPinned: false,
        defaultVideoFrames: 0,
        defaultVideoFps: 0,
        defaultVideoWidth: 0,
        defaultVideoHeight: 0,
        defaultVideoSteps: 0,
        defaultVideoMode: null,
        uploadDirectory: paths.UploadsDirectory,
        logDirectory: paths.LogsDirectory,
        fileLoggingEnabled: true,
        samplingDefaults: null,
        // A skill that cannot run its own scripts is a document, not a skill: the
        // bundled ones are chosen precisely because they do work end to end. What
        // gates them is the user's own switch, read here, not a build-time default.
        skillsEnabled: settings.SkillsEnabled,
        skillsDiscovery: true,
        skillsAllowScripts: settings.AllowCodeExecution,
        skillsAllowNetwork: settings.AllowNetwork);

    /// <summary>
    /// Wait until the engine has nothing in flight.
    ///
    /// <para>
    /// Stopping the server ends the HTTP requests, but a generation those requests
    /// started can still be inside a graph compute on the engine's own threads —
    /// cancellation is delivered between tokens, and a token can take a while.
    /// Releasing the model at that moment unmaps weights those threads are reading,
    /// and the process dies with a segmentation fault in whichever kernel happened to
    /// be running. The engine reports what it is processing; this waits for that to
    /// reach zero, with a cap so a wedged request cannot stop the app from closing.
    /// </para>
    /// </summary>
    /// <summary>
    /// Make <paramref name="model"/> the model this app is using, now.
    ///
    /// <para>
    /// The engine enforces one hosted model per process because the desktop server is
    /// launched against one <c>--model</c>. An app is not: its user picks from a list,
    /// and until this existed the pick only took effect on the next launch -- so the
    /// Models page said "selected" while the chat said "No model is configured", which
    /// is indistinguishable from a broken button. This saves the choice, moves the
    /// guard, and loads the weights, in that order.
    /// </para>
    /// <para>
    /// Backends are tried best-first and a refusal is reported rather than swallowed:
    /// a phone that silently fell back to the CPU would look like the model loading
    /// very slowly rather than like Metal being unavailable.
    /// </para>
    /// </summary>
    /// <param name="model">The catalog entry to use. Its files must already be installed.</param>
    /// <returns>The backend that answered.</returns>
    public string UseModel(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // One load at a time, whoever asked. There are three callers now — the startup
        // load, the Models list, and the device hook — and the startup one takes twenty
        // seconds on real weights, which is exactly the window in which a user who sees
        // "No model yet" goes to the list and taps Use. Two threads inside the engine's
        // load at once free and remap the same buffers; the failure is a segmentation
        // fault in a kernel with nothing to do with either of them.
        lock (_modelGate)
        {
            SetModelLoad(ModelLoadState.Loading, null);
            AppSettings settings = Settings.Load();
            settings.SelectedModelId = model.Id;
            Settings.Save(settings);

            string weights = Paths.SelectedModelPath(settings);
            string? projector = Paths.SelectedProjectorPath(settings);
            if (!File.Exists(weights))
            {
                var missing = new FileNotFoundException($"{model.DisplayName} is not downloaded yet.", weights);
                SetModelLoad(ModelLoadState.Failed, missing.Message);
                throw missing;
            }
            if (projector is not null && !File.Exists(projector))
                projector = null;

            // Before the load, not after: the engine reads its context length and KV
            // dtype when the model is constructed. This is the only funnel for a load
            // (startup, the Models list, the device hook all arrive here), which is why
            // it is the right place for the budget. See EngineMemoryPolicy for the
            // measurements -- this is what stops a pasted document from growing the KV
            // cache until jetsam kills the app.
            EngineMemoryPolicy.Apply(model, settings);

            Options.RepointHostedModel(weights, projector);

            var refusals = new List<string>();
            foreach (BackendOption backend in Options.SupportedBackends)
            {
                try
                {
                    ModelService.LoadModel(weights, projector, backend.Value);
                    _loggerFactory.CreateLogger("TensorAgent.Host").LogInformation(
                        "using {Model} on {Backend}", model.Id, backend.Value);
                    SetModelLoad(ModelLoadState.Loaded, null);
                    return backend.Value;
                }
                catch (Exception ex)
                {
                    refusals.Add($"{backend.Value}: {ex.Message}");
                }
            }

            var refused = new InvalidOperationException(
                $"{model.DisplayName} could not be loaded on any backend this build offers:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", refusals));
            SetModelLoad(ModelLoadState.Failed, refused.Message);
            throw refused;
        }
    }

    private readonly object _modelGate = new();

    /// <summary>
    /// What <see cref="WaitForTheEngineToStop"/> polls, and the only thing that decides
    /// how long the shutdown waits. Replaceable so a test can hold the shutdown open
    /// deterministically instead of racing a real engine.
    /// </summary>
    internal Func<bool> EngineHasWorkInFlight { get; set; } = () => false;

    /// <summary>
    /// Whether the engine is still working, straight from its own counters. A component
    /// that cannot say what it is doing is not a reason to keep the app open; the wait
    /// is a precaution, not a contract, so an engine that will not answer reads as idle.
    /// </summary>
    private bool EngineIsProcessing()
    {
        try
        {
            if (!ModelService.EngineHost.TryGetLiveStats(out int processing, out int waiting, out _))
                return false;
            return processing > 0 || waiting > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WaitForTheEngineToStop()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < EngineDrainTimeout)
        {
            if (!EngineHasWorkInFlight())
                return;
            Thread.Sleep(25);
        }
        _loggerFactory.CreateLogger("TensorAgent.Host")
            .LogWarning("the engine was still working after {Seconds}s; releasing the model anyway",
                EngineDrainTimeout.TotalSeconds);
    }

    /// <summary>How long <see cref="Dispose"/> waits for the engine before releasing the model regardless.</summary>
    public static TimeSpan EngineDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private static int _exitHookInstalled;

    /// <summary>
    /// Hand the GPU back before the C runtime tears itself down, which is the one
    /// piece of shutdown <see cref="Dispose"/> cannot do.
    ///
    /// <para>
    /// ggml-metal's device is a C++ static whose destructor asserts that every
    /// residency set has been given back, and that destructor runs from
    /// <c>__cxa_finalize</c> — after the last managed code. A user who closes the app
    /// without unloading first (which is every user) therefore leaves the loaded
    /// model's buffers registered, and the process aborts on
    /// <c>GGML_ASSERT([rsets-&gt;data count] == 0)</c> instead of exiting. Disposing
    /// this host releases them, but nothing guarantees anyone disposes it.
    /// </para>
    /// <para>
    /// So: the same wiring the desktop server and the CLI already have
    /// (TensorSharp.Server/Program.cs registers both ApplicationStopped and
    /// ProcessExit), for the same reason. The call is idempotent and does nothing
    /// when no GGML backend was ever initialised.
    /// </para>
    /// <para>
    /// The APP calls this, not the constructor. A net that catches an undisposed
    /// engine also hides one, and a test process that acquired it merely by building
    /// a host would stop aborting on exactly the leak this exists to survive — which
    /// is how the leak went unnoticed in the first place. Tests build hosts and get
    /// no net; the app installs it deliberately, once, at startup.
    /// </para>
    /// </summary>
    public static void ReleaseTheEngineWhenTheProcessExits()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0)
            return;

        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            try
            {
                GgmlBasicOps.Shutdown();
            }
            catch (DllNotFoundException)
            {
                // No GgmlOps in this build, so there is no device holding anything
                // and nothing to release. Not a fallback: the engine that would need
                // shutting down was never there.
            }
            catch (EntryPointNotFoundException)
            {
                // An older GgmlOps without the entry point. Same conclusion, and the
                // process is already on its way out; there is nowhere left to report.
            }
        };
    }

    /// <summary>
    /// Shut down in the only order that is safe: the server first, then the engine.
    ///
    /// <para>
    /// The server owns the requests, and a request in flight is very often inside the
    /// model. Freeing the model first hands the native compute threads memory that has
    /// been unmapped underneath them, and the process dies with a segmentation fault
    /// in whichever kernel happened to be reading. Stopping the server waits for those
    /// requests to finish, so by the time the model is released nothing is using it.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        // First of all, and this is now load-bearing: a turn no longer stops when its
        // reader goes away, so closing the server is not enough to end one. Asking the
        // turns to stop is what lets WaitForTheEngineToStop below ever return -- and
        // that wait is the only thing between a generation on the engine's threads and
        // the weights being unmapped underneath it.
        Turns.StopAll();
        Close(Server);
        // Before the engine wait, because a download holds a socket and a file handle
        // and has nothing to do with the model: making a five-second transfer teardown
        // wait behind a generation would be for no reason.
        Close(Downloads);
        WaitForTheEngineToStop();
        Close(Turns);
        Close(ModelService);
        foreach (IDisposable owned in _owned)
            Close(owned);
        _owned.Clear();

        static void Close(IDisposable owned)
        {
            try { owned.Dispose(); }
            catch (Exception) { /* teardown is best effort; the process is going away */ }
        }
    }
}

/// <summary>One self-test check: what was tried, whether it behaved, and what it said.</summary>
public sealed record SelfTestResult(string Name, bool Ok, string Detail)
{
    public override string ToString() => $"{(Ok ? "ok  " : "FAIL")} {Name}: {Detail}";
}

/// <summary>
/// Where this installation keeps its files.
///
/// <para>
/// The split matters on iOS more than it does on a desktop. Models go somewhere
/// excluded from iCloud backup, because a 6 GB weight file that can be downloaded
/// again must not be uploaded to the user's iCloud account; conversations and
/// settings go somewhere that IS backed up, because they cannot be recovered any
/// other way; and scratch space goes somewhere the system may reclaim.
/// </para>
/// </summary>
public sealed record AgentPaths(string DataRoot, string CacheRoot)
{
    /// <summary>Physical memory in whole gigabytes, which decides what the catalog offers.</summary>
    public int DeviceMemoryGB { get; init; } = 12;

    public string ModelsDirectory => Path.Combine(CacheRoot, "models");
    public string ConversationsDirectory => Path.Combine(DataRoot, "conversations");
    public string UploadsDirectory => Path.Combine(CacheRoot, "uploads");
    public string ScratchDirectory => Path.Combine(CacheRoot, "scratch");
    public string ArtifactsDirectory => Path.Combine(CacheRoot, "artifacts");
    public string LogsDirectory => Path.Combine(CacheRoot, "logs");
    public string InstalledSkillsDirectory => Path.Combine(DataRoot, "skills");
    public string BundledSkillsDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Where the bundled CPython lives: the directory holding <c>python/</c> and the
    /// extension frameworks. Empty when this build ships no interpreter, which the
    /// runtime reports as unavailable rather than failing to construct.
    /// </summary>
    public string PythonRuntimeDirectory { get; init; } = string.Empty;
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public void EnsureCreated()
    {
        foreach (string directory in new[]
                 {
                     DataRoot, CacheRoot, ModelsDirectory, ConversationsDirectory, UploadsDirectory,
                     ScratchDirectory, ArtifactsDirectory, LogsDirectory, InstalledSkillsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// The weights file for whichever model the user picked, or a path inside the
    /// model directory that does not exist yet.
    ///
    /// <para>
    /// A non-existent path rather than null is deliberate: the chat service reads the
    /// startup path to decide what <c>/api/models</c> offers and refuses a chat
    /// request until something is loaded, and a null there would read as "this build
    /// has no model support" rather than "no model has been downloaded yet".
    /// </para>
    /// </summary>
    public string SelectedModelPath(AppSettings settings)
    {
        CatalogModel? model = settings.SelectedModelId is { Length: > 0 } id
            ? ModelCatalog.Find(id)
            : null;
        return model is null
            ? Path.Combine(ModelsDirectory, "no-model-selected.gguf")
            : Path.Combine(ModelsDirectory, model.Id, model.Weights.FileName);
    }

    /// <summary>The multimodal projector beside the selected model, or null when it has none.</summary>
    public string? SelectedProjectorPath(AppSettings settings)
    {
        CatalogModel? model = settings.SelectedModelId is { Length: > 0 } id
            ? ModelCatalog.Find(id)
            : null;
        return model?.Projector is { } projector
            ? Path.Combine(ModelsDirectory, model.Id, projector.FileName)
            : null;
    }
}
