// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Python;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;
using TensorAgent.Core.Shell;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Chat;
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
    /// <param name="python">The embedded Python, when this build has one.</param>
    /// <param name="javaScript">The embedded JavaScript engine, when this build has one.</param>
    /// <param name="port">A fixed loopback port, or 0 to take a free one.</param>
    public AgentAppHost(
        AgentPaths paths,
        string? webRoot = null,
        ILoggerFactory? loggerFactory = null,
        IPythonRuntime? python = null,
        IJavaScriptRuntime? javaScript = null,
        int port = 0)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        paths.EnsureCreated();

        Settings = new SettingsStore(paths.SettingsFile);
        AppSettings settings = Settings.Load();

        Models = new ModelStore(paths.ModelsDirectory);
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

        Backend = new InProcessShellBackend(python, javaScript);
        Artifacts = new CodeArtifactStore(paths.ArtifactsDirectory);
        ShellRunner runner = new(
            CodeExec,
            _loggerFactory.CreateLogger("TensorAgent.CodeExec"),
            Artifacts,
            backend: Backend);
        CodeRunner = settings.AllowCodeExecution ? new CodeRunnerAdapter(runner, CodeExec) : null;

        Skills = new SkillRegistry(new SkillRegistryOptions
        {
            Roots = new[] { paths.BundledSkillsDirectory, paths.InstalledSkillsDirectory },
            InstallDirectory = paths.InstalledSkillsDirectory,
        });

        ModelService = new ModelService(_loggerFactory.CreateLogger<ModelService>());
        Sessions = new SessionManager();
        Uploads = new UploadStoragePolicy(paths.UploadsDirectory);

        Options = BuildOptions(paths, settings);
        Chat = new WebUiChatService(
            ModelService, Sessions, Options, Uploads, Skills,
            CodeRunner!, Workspaces, Artifacts, _loggerFactory);

        // A turn the user just took has to survive the app being closed, and the Web
        // UI page keeps its history only in memory. This is the hook the chat service
        // exposes for exactly that: the transcript is written on the host side, keyed
        // by the conversation the request named.
        Recorder = new ConversationRecorder(Conversations);
        Chat.OnChatRequest = Recorder.Record;

        SkillsService = new SkillsService(Skills, Options, Uploads, _loggerFactory);

        Server = new LoopbackServer(_loggerFactory.CreateLogger("TensorAgent.Loopback"), port)
        {
            StaticRoot = webRoot,
        };
        Server.MapWebUi(Chat, SkillsService, Recorder);
        Server.MapAgent(Catalog, Models, Conversations, Settings, DescribeEngine);

        _owned.Add(Server);
        _owned.Add(ModelService);
    }

    public AgentPaths Paths { get; }
    public SettingsStore Settings { get; }
    public ModelStore Models { get; }
    public ConversationStore Conversations { get; }
    public IReadOnlyList<CatalogModel> Catalog { get; }
    public CodeExecOptions CodeExec { get; }
    public SessionWorkspaceManager Workspaces { get; }
    public InProcessShellBackend Backend { get; }
    public CodeArtifactStore Artifacts { get; }
    public ICodeRunner? CodeRunner { get; }
    public SkillRegistry Skills { get; }
    public ModelService ModelService { get; }
    public SessionManager Sessions { get; }
    public UploadStoragePolicy Uploads { get; }
    public ServerHostingOptions Options { get; }
    public WebUiChatService Chat { get; }
    public ConversationRecorder Recorder { get; }
    public SkillsService SkillsService { get; }
    public LoopbackServer Server { get; }

    /// <summary>The URL the WebView opens: the page plus the launch token that sets its cookie.</summary>
    public string EntryUrl => Server.EntryUrl;

    public void Start() => Server.Start();

    /// <summary>
    /// One line naming what will actually run a command and what will run a script,
    /// for the status bar and for <c>/api/agent/engine</c>. A user who is told
    /// "python is not available" before writing a script is better served than one
    /// who finds out from a failed run.
    /// </summary>
    public string DescribeEngine()
    {
        var parts = new List<string> { Backend.Describe() };
        parts.Add(CodeRunner is null ? "code execution off" : "code execution on");
        parts.Add(CodeExec.AllowNetwork ? "network on" : "network off");
        parts.Add($"{Skills.Skills.Count} skills");
        return string.Join(" · ", parts);
    }

    private static ServerHostingOptions BuildOptions(AgentPaths paths, AppSettings settings) => new(
        startupModelPath: paths.SelectedModelPath(settings),
        startupMmProjPath: paths.SelectedProjectorPath(settings),
        defaultBackend: "ggml_metal",
        supportedBackends: new[]
        {
            // Metal is the point of running on the phone at all; the CPU entry stays
            // so the simulator, where there is no usable GPU, has something to pick.
            new BackendOption("ggml_metal", "GPU (Metal)"),
            new BackendOption("ggml_cpu", "CPU"),
        },
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
        skillsEnabled: true,
        skillsDiscovery: true,
        skillsAllowScripts: settings.AllowCodeExecution,
        skillsAllowNetwork: settings.AllowNetwork);

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try { _owned[i].Dispose(); }
            catch (Exception) { /* teardown is best effort; the process is going away */ }
        }
        _owned.Clear();
    }
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
