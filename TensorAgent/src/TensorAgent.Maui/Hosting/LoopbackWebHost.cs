// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Foundation;
using Microsoft.Extensions.Logging;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Python;
using TensorSharp.Runtime;
using TensorSharp.Server;

namespace TensorAgent.Maui.Hosting;

/// <summary>
/// The iOS half of the app's host: where the files live on this device, and
/// nothing else.
///
/// <para>
/// Everything the server actually does — the routes, the chat pipeline, the code
/// runner, the model catalog — is <see cref="AgentAppHost"/> in the platform-neutral
/// project, so it can be tested on a development machine. What is left here is the
/// part that genuinely cannot be: which directory iOS gives an app for data it must
/// back up, which for files it can re-fetch, how much memory the device has, and
/// where the bundle put the Web UI.
/// </para>
/// </summary>
public sealed class LoopbackWebHost : IDisposable
{
    private readonly AgentAppHost _host;

    /// <param name="webRoot">The bundled copy of TensorSharp.Server/wwwroot.</param>
    /// <param name="loggerFactory">Where the engine logs; console output is what <c>simctl launch --console</c> shows.</param>
    /// <param name="python">The embedded interpreter, when this build has one.</param>
    /// <param name="javaScript">The embedded JavaScript engine, when this build has one.</param>
    public LoopbackWebHost(
        string webRoot,
        ILoggerFactory? loggerFactory = null,
        IPythonRuntime? python = null,
        IJavaScriptRuntime? javaScript = null)
    {
        WebRoot = Path.GetFullPath(webRoot);
        string index = Path.Combine(WebRoot, "index.html");
        if (!File.Exists(index))
        {
            // A bundle with no page is a build problem, and a blank WebView is the
            // worst possible way to learn about it.
            throw new FileNotFoundException(
                $"The bundled Web UI is missing: expected {index}. The csproj links "
                + "TensorSharp.Server/wwwroot/** into the bundle as webui/.", index);
        }

        // The app, and only the app, installs the process-exit net: ggml-metal's
        // device is a C++ static whose destructor asserts every residency set was
        // handed back, and a user who closes the app without unloading first — which
        // is every user — would otherwise abort instead of exiting.
        AgentAppHost.ReleaseTheEngineWhenTheProcessExits();

        // What the page offers is what this build can actually run. The simulator's
        // slice of the engine has no Metal, and a page whose default backend does not
        // exist puts the user one tap from a load that fails.
        _host = new AgentAppHost(
            DevicePaths(), WebRoot, loggerFactory, python, javaScript,
            backends: BackendsFor(Compute.Selection));
    }

    /// <summary>
    /// The backend list the page shows, best first. Metal leads when the linked
    /// engine has it and the GPU can run its kernels; otherwise CPU leads and Metal
    /// is not offered at all, because offering a backend that cannot initialise is
    /// worse than offering one fewer.
    /// </summary>
    private static IReadOnlyList<BackendOption> BackendsFor(ComputeSelection selection)
        => selection.Backend == BackendType.GgmlMetal
            ? new[] { new BackendOption("ggml_metal", "GPU (Metal)"), new BackendOption("ggml_cpu", "CPU") }
            : new[] { new BackendOption("ggml_cpu", "CPU") };

    /// <summary>Where the Web UI is served from, for the startup log.</summary>
    public string WebRoot { get; }

    public int Port => _host.Server.Port;
    public string BaseUrl => _host.Server.BaseUrl;
    public string EntryUrl => _host.EntryUrl;
    public string Token => _host.Server.Token;

    /// <summary>The assembled application, for the native pages that drive it directly.</summary>
    public AgentAppHost App => _host;

    public void Start() => _host.Start();

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// The two directories iOS gives an app, used for what each is actually for.
    ///
    /// <para>
    /// Library/Application Support is backed up to iCloud and restored onto a new
    /// device, which is right for conversations, settings and installed skills and
    /// badly wrong for model weights: a single entry in this catalog is five to ten
    /// gigabytes, it is a byte-identical copy of a public file, and pushing it into
    /// the user's iCloud quota would be indefensible. Weights therefore go to Caches,
    /// which is excluded from backup — and which the system may purge under storage
    /// pressure, so the app has to treat a missing model as "download it again"
    /// rather than as an error. <see cref="Core.Catalog.ModelStore"/> already does.
    /// </para>
    /// </summary>
    private static AgentPaths DevicePaths()
    {
        string data = NSSearchPath.GetDirectories(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User, true)[0];
        string cache = NSSearchPath.GetDirectories(NSSearchPathDirectory.CachesDirectory, NSSearchPathDomain.User, true)[0];

        return new AgentPaths(
            Path.Combine(data, "TensorAgent"),
            Path.Combine(cache, "TensorAgent"))
        {
            DeviceMemoryGB = DeviceMemoryGigabytes(),
            BundledSkillsDirectory = Path.Combine(NSBundle.MainBundle.BundlePath, "skills"),
            // The interpreter's standard library is staged into the bundle beside the
            // Python framework, which is where PyConfig's module search paths point.
            PythonRuntimeDirectory = NSBundle.MainBundle.BundlePath,
        };
    }

    /// <summary>
    /// Physical memory in whole gigabytes, which decides which catalog entries the
    /// app will even offer. It is deliberately the DEVICE's memory and not the app's
    /// jetsam budget: the budget is roughly two thirds of it, and the catalog's
    /// per-entry minimum is already written against the device figure.
    /// </summary>
    private static int DeviceMemoryGigabytes()
    {
        ulong bytes = NSProcessInfo.ProcessInfo.PhysicalMemory;
        return (int)Math.Round(bytes / (1024.0 * 1024.0 * 1024.0));
    }
}
