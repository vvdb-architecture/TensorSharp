// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Python;

/// <summary>
/// Where the interpreter's files are. Read from the manifest
/// <c>prepare-python.sh</c> writes, so the app and the staging script cannot
/// drift apart about a directory name.
/// </summary>
/// <param name="Root">The staged slice — the directory that contains <c>python/</c>.</param>
/// <param name="Home">What <c>PyConfig.home</c> is set to: the prefix whose <c>lib/pythonX.Y</c> is the standard library.</param>
/// <param name="StdLib">The standard library directory.</param>
/// <param name="DynLoad">Where the extension modules (as <c>.fwork</c> placeholders on iOS) live.</param>
/// <param name="Packages">The pre-staged packages the app ships.</param>
/// <param name="Version">The <c>X.Y</c> the layout was built for.</param>
internal sealed record PythonRuntimeLayout(
    string Root,
    string Home,
    string StdLib,
    string DynLoad,
    string Packages,
    string Version)
{
    private const string ManifestName = "tensoragent-python.json";

    /// <summary>
    /// Finds the layout under <paramref name="root"/>, or explains exactly which
    /// file or directory is missing. Nothing here guesses: a reason that says
    /// "not found" without saying what is the reason this class refuses to give.
    /// </summary>
    internal static bool TryDiscover(string root, out PythonRuntimeLayout? layout, out string? error)
    {
        layout = null;
        error = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "no Python runtime root was configured; call EmbeddedPython.Configure(<staged runtime>) at startup";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"the Python runtime root '{root}' is not a usable path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(full))
        {
            error = $"the Python runtime root {full} does not exist";
            return false;
        }

        // The staging script writes the manifest into python/; a caller that
        // pointed straight at that directory is accepted too.
        string bundleRoot = full;
        string manifest = Path.Combine(full, "python", ManifestName);
        if (!File.Exists(manifest) && File.Exists(Path.Combine(full, ManifestName)))
        {
            manifest = Path.Combine(full, ManifestName);
            bundleRoot = Path.GetDirectoryName(full) ?? full;
        }

        string? stdlib = null;
        string? packages = null;
        string version = "3.13";
        if (File.Exists(manifest))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
                JsonElement element = document.RootElement;
                if (element.TryGetProperty("stdlib", out JsonElement value) && value.GetString() is { Length: > 0 } relative)
                    stdlib = Path.GetFullPath(Path.Combine(bundleRoot, relative));
                if (element.TryGetProperty("packages", out value) && value.GetString() is { Length: > 0 } packageRelative)
                    packages = Path.GetFullPath(Path.Combine(bundleRoot, packageRelative));
                if (element.TryGetProperty("version", out value) && value.GetString() is { Length: > 0 } manifestVersion)
                    version = manifestVersion;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                error = $"the Python manifest {manifest} could not be read: {ex.Message}";
                return false;
            }
        }
        else
        {
            // No manifest: a developer machine pointing at a plain CPython
            // prefix. Any 3.x found is accepted here and reported by version --
            // whether its PyConfig is the one this build knows is a question
            // only PythonConfigLayout can answer, and it answers it precisely.
            foreach (string parent in new[] { Path.Combine(full, "python", "lib"), Path.Combine(full, "lib") })
            {
                if (!Directory.Exists(parent))
                    continue;
                string? found = Directory.GetDirectories(parent, "python3.*")
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (found is not null)
                {
                    stdlib = found;
                    version = Path.GetFileName(found)["python".Length..];
                    break;
                }
            }
            if (stdlib is null)
            {
                error = $"{full} holds no embedded Python: neither python/{ManifestName}, "
                    + $"python/lib/python{version} nor lib/python{version} is there";
                return false;
            }
        }

        if (stdlib is null || !Directory.Exists(stdlib))
        {
            error = $"the standard library {stdlib ?? "(unnamed)"} named by {manifest} is not there";
            return false;
        }

        // home is a prefix: CPython expects home/lib/pythonX.Y to be the
        // standard library, so it is the grandparent of the stdlib directory.
        string home = Path.GetDirectoryName(Path.GetDirectoryName(stdlib)!) ?? stdlib;
        string dynload = Path.Combine(stdlib, "lib-dynload");
        packages ??= Path.Combine(home, "app_packages");

        layout = new PythonRuntimeLayout(bundleRoot, home, stdlib, dynload, packages, version);
        return true;
    }

    /// <summary>Every tree the interpreter must be able to read to import anything at all.</summary>
    internal IEnumerable<string> ImportRoots()
    {
        yield return Home;
        yield return StdLib;
        if (Directory.Exists(DynLoad))
            yield return DynLoad;
        if (Directory.Exists(Packages))
            yield return Packages;
    }

    /// <summary>
    /// A real <c>libpython</c> to open, or null when the symbols are expected to
    /// be in the app image (iOS) rather than in a file.
    /// </summary>
    internal string? FindLibrary()
    {
        foreach (string candidate in new[]
                 {
                     Path.Combine(Home, "lib", $"libpython{Version}.dylib"),
                     Path.Combine(Root, "lib", $"libpython{Version}.dylib"),
                     Path.Combine(Root, "Frameworks", "Python.framework", "Python"),
                     Path.Combine(Root, "Python.framework", "Python"),
                     Path.Combine(Root, "Python"),
                     Path.Combine(Home, "lib", $"libpython{Version}.so"),
                     Path.Combine(Root, "lib", $"libpython{Version}.so"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>
/// The one CPython in this process: one thread, one GIL, one initialization.
///
/// <para>
/// Everything that touches the interpreter runs on a single long-lived thread.
/// That is not tidiness — <c>PyGILState_Ensure</c> and its matching
/// <c>Release</c> must happen on the same thread, and the thread that
/// initializes CPython is the one Python calls "the main thread", which is the
/// only thread pending calls are delivered on. A worker queue keeps all three
/// facts true without the caller having to know any of them.
/// </para>
/// <para>
/// CPython cannot be re-initialized after finalization in the way an app would
/// need, so this type never finalizes: the interpreter lives as long as the
/// process, and a second runtime root is refused rather than quietly ignored.
/// </para>
/// </summary>
internal sealed unsafe class PythonInterpreter
{
    private static readonly object s_gate = new();
    private static PythonInterpreter? s_instance;
    private static string? s_instanceRoot;

    // The run whose output the native write callback belongs to. Runs are
    // serialized by EmbeddedPython's semaphore, and both fields are only ever
    // touched from the worker thread, so plain fields are enough.
    private static OutputCapture? s_stdout;
    private static OutputCapture? s_stderr;

    // Read by the pending call, written by the worker; the interrupt only fires
    // if the run it was aimed at is still the one running.
    private static long s_currentRun;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private IntPtr _module;
    private IntPtr _runFunction;

    private PythonInterpreter()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "tensoragent-python",
        };
    }

    /// <summary><c>X.Y.Z</c> as the interpreter itself reports it.</summary>
    internal string Version { get; private set; } = string.Empty;

    /// <summary>
    /// The process-wide interpreter for <paramref name="layout"/>, initializing
    /// it on first use. A second call naming a different root fails rather than
    /// silently serving the first one, because the two would disagree about
    /// where the standard library is.
    /// </summary>
    internal static PythonInterpreter? Acquire(PythonRuntimeLayout layout, IReadOnlyList<string> extraPaths, out string? error)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (s_gate)
        {
            if (s_instance is not null)
            {
                if (!string.Equals(s_instanceRoot, layout.Root, StringComparison.Ordinal))
                {
                    error = $"CPython is already initialized from {s_instanceRoot}; "
                        + $"{layout.Root} cannot also be loaded into this process";
                    return null;
                }
                error = null;
                return s_instance;
            }

            var interpreter = new PythonInterpreter();
            interpreter._thread.Start();
            error = interpreter.Invoke(() => interpreter.Initialize(layout, extraPaths));
            if (error is not null)
            {
                interpreter._work.CompleteAdding();
                return null;
            }

            s_instance = interpreter;
            s_instanceRoot = layout.Root;
            return interpreter;
        }
    }

    /// <summary>
    /// Applies <paramref name="policySource"/> and runs <paramref name="payload"/>,
    /// with the GIL held for both and the two captures wired to
    /// <c>sys.stdout</c>/<c>sys.stderr</c> for the duration.
    /// </summary>
    internal Task<int> RunAsync(long runId, string policySource, string payload, OutputCapture stdout, OutputCapture stderr)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
        {
            int state = PythonNative.PyGILState_Ensure();
            s_stdout = stdout;
            s_stderr = stderr;
            Volatile.Write(ref s_currentRun, runId);
            try
            {
                if (PythonNative.PyRun_SimpleString(PythonNative.Utf8(policySource)) != 0)
                {
                    PythonNative.PyErr_Clear();
                    stderr.Write("python: the sandbox policy for this run could not be applied\n");
                    completion.TrySetResult(1);
                    return;
                }
                completion.TrySetResult(Call(payload, stderr));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref s_currentRun, 0);
                s_stdout = null;
                s_stderr = null;
                PythonNative.PyGILState_Release(state);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("the embedded Python interpreter is shut down"));
        }
        return completion.Task;
    }

    /// <summary>
    /// Compiles without running, and renders a failure as <c>path:line: message</c>.
    /// Null means it compiled.
    /// </summary>
    internal Task<string?> CheckSyntaxAsync(string source, string path)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
        {
            int state = PythonNative.PyGILState_Ensure();
            try
            {
                completion.TrySetResult(Compile(source, path));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                PythonNative.PyGILState_Release(state);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("the embedded Python interpreter is shut down"));
        }
        return completion.Task;
    }

    /// <summary>
    /// Asks the run identified by <paramref name="runId"/> to stop.
    ///
    /// <para>
    /// Two attempts, from a thread that holds neither the GIL nor anything else.
    /// <c>PyErr_SetInterrupt</c> is the documented way and is what a real
    /// <c>SIGINT</c> would do, but it only fires if a Python-level SIGINT
    /// handler is installed and this interpreter starts with
    /// <c>install_signal_handlers = 0</c> — so a pending call that raises
    /// <c>KeyboardInterrupt</c> directly is queued as well, and that is the one
    /// that actually lands.
    /// </para>
    /// <para>
    /// Honest limit: both are delivered by the evaluation loop between bytecodes.
    /// A run that is inside a C extension — <c>numpy</c> multiplying large
    /// arrays, <c>re</c> backtracking, <c>time.sleep</c> — will not see either
    /// until it comes back, and there is no way to kill it: there is no process
    /// to signal and killing a thread would leave the interpreter's locks held.
    /// The caller reports a timeout and the run keeps the interpreter until it
    /// returns on its own.
    /// </para>
    /// </summary>
    internal void Interrupt(long runId)
    {
        if (Volatile.Read(ref s_currentRun) != runId)
            return;
        PythonNative.PyErr_SetInterrupt();
        PythonNative.Py_AddPendingCall(&PendingInterrupt, (IntPtr)runId);
    }

    private bool TryPost(Action work)
    {
        try
        {
            _work.Add(work);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private T Invoke<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            throw new InvalidOperationException("the embedded Python interpreter is shut down");
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Pump()
    {
        foreach (Action work in _work.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch
            {
                // Each work item reports its own failure through its completion
                // source; nothing here may take the thread down, because the
                // interpreter cannot be started again.
            }
        }
    }

    // =====================================================================================
    // initialization (worker thread)
    // =====================================================================================

    private string? Initialize(PythonRuntimeLayout layout, IReadOnlyList<string> extraPaths)
    {
        PythonNative.UseLibrary(layout.FindLibrary());
        IntPtr image = PythonNative.TryLoad(out string? loadError);
        if (image == IntPtr.Zero)
            return loadError ?? "the CPython library could not be loaded";

        try
        {
            if (PythonNative.Py_IsInitialized() != 0)
                return "CPython was already initialized by something else in this process";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "the CPython entry points could not be bound"
                + (PythonNative.ResolverConflict is { } conflict ? $" ({conflict})" : string.Empty)
                + $": {ex.Message}";
        }

        IntPtr config = Marshal.AllocHGlobal(PythonConfigLayout.AllocationSize);
        try
        {
            new Span<byte>((void*)config, PythonConfigLayout.AllocationSize).Clear();
            PythonNative.PyConfig_InitIsolatedConfig(config);

            string? mismatch = PythonConfigLayout.Verify(config);
            if (mismatch is not null)
                return mismatch;

            // Isolated already means use_environment = 0, user_site_directory = 0
            // and install_signal_handlers = 0. These three are ours:
            //  - the bundle is read-only, so a .pyc write would fail on every import;
            //  - site.py has nothing to find and only costs startup time;
            //  - the app owns the process's stdio, so CPython must not configure
            //    it (already the isolated default, written anyway so a change in
            //    that default cannot quietly hand our file descriptors away).
            Marshal.WriteInt32(config, PythonConfigLayout.OffsetOf(nameof(PythonNative.PyConfig.WriteBytecode)), 0);
            Marshal.WriteInt32(config, PythonConfigLayout.OffsetOf(nameof(PythonNative.PyConfig.SiteImport)), 0);
            Marshal.WriteInt32(config, PythonConfigLayout.OffsetOf(nameof(PythonNative.PyConfig.ConfigureCStdio)), 0);

            string? failure = SetString(config, nameof(PythonNative.PyConfig.ProgramName), "python3")
                ?? SetString(config, nameof(PythonNative.PyConfig.Home), layout.Home)
                ?? SetString(config, nameof(PythonNative.PyConfig.PlatLibDir), "lib");
            if (failure is not null)
                return failure;

            var searchPaths = new List<string> { layout.StdLib };
            if (Directory.Exists(layout.DynLoad))
                searchPaths.Add(layout.DynLoad);
            if (Directory.Exists(layout.Packages))
                searchPaths.Add(layout.Packages);
            foreach (string extra in extraPaths)
            {
                if (!string.IsNullOrEmpty(extra) && !searchPaths.Contains(extra, StringComparer.Ordinal))
                    searchPaths.Add(extra);
            }

            Marshal.WriteInt32(config, PythonConfigLayout.OffsetOf(nameof(PythonNative.PyConfig.ModuleSearchPathsSet)), 1);
            IntPtr list = config + PythonConfigLayout.OffsetOf(nameof(PythonNative.PyConfig.ModuleSearchPaths));
            foreach (string path in searchPaths)
            {
                failure = AppendPath(list, path);
                if (failure is not null)
                    return failure;
            }

            PythonNative.PyStatus status = PythonNative.Py_InitializeFromConfig(config);
            if (status.IsError)
                return "CPython refused to start: " + status.Describe();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "the CPython entry points could not be bound"
                + (PythonNative.ResolverConflict is { } conflict ? $" ({conflict})" : string.Empty)
                + $": {ex.Message}";
        }
        finally
        {
            PythonNative.PyConfig_Clear(config);
            Marshal.FreeHGlobal(config);
        }

        if (PythonNative.Py_IsInitialized() == 0)
            return "Py_InitializeFromConfig reported success but Py_IsInitialized is still false";

        string? bootstrapError = InstallBootstrap();
        if (bootstrapError is not null)
            return bootstrapError;

        // Hand the GIL back so this thread can take it again per run through
        // PyGILState_Ensure, which is the only pairing that stays correct when
        // Python's own threads are in play.
        PythonNative.PyEval_SaveThread();
        return null;
    }

    private string? SetString(IntPtr config, string field, string value)
    {
        PythonNative.PyStatus status = PythonNative.PyConfig_SetBytesString(
            config, config + PythonConfigLayout.OffsetOf(field), PythonNative.Utf8(value));
        return status.IsError ? $"PyConfig.{field} = '{value}' was refused: {status.Describe()}" : null;
    }

    private static string? AppendPath(IntPtr list, string path)
    {
        IntPtr wide = PythonNative.Py_DecodeLocale(PythonNative.Utf8(path), IntPtr.Zero);
        if (wide == IntPtr.Zero)
            return $"the import path '{path}' could not be decoded for CPython";
        try
        {
            PythonNative.PyStatus status = PythonNative.PyWideStringList_Append(list, wide);
            return status.IsError ? $"the import path '{path}' was refused: {status.Describe()}" : null;
        }
        finally
        {
            PythonNative.PyMem_RawFree(wide);
        }
    }

    private string? InstallBootstrap()
    {
        if (PythonNative.PyRun_SimpleString(PythonNative.Utf8(PythonBootstrap.InstallSource)) != 0)
        {
            PythonNative.PyErr_Clear();
            return "the Python bootstrap did not compile; the embedded interpreter is unusable";
        }

        IntPtr main = PythonNative.PyImport_AddModule(PythonNative.Utf8("__main__"));
        if (main == IntPtr.Zero)
            return "CPython has no __main__ module after startup: " + (PythonNative.TakeError() ?? "no reason given");

        IntPtr installer = PythonNative.PyObject_GetAttrString(main, PythonNative.Utf8(PythonBootstrap.InstallFunction));
        if (installer == IntPtr.Zero)
            return "the Python bootstrap defined no installer: " + (PythonNative.TakeError() ?? "no reason given");

        try
        {
            IntPtr writer = CreateWriteCallable();
            if (writer == IntPtr.Zero)
                return "the native stdout callback could not be built: " + (PythonNative.TakeError() ?? "no reason given");

            IntPtr module;
            try
            {
                module = PythonNative.PyObject_CallOneArg(installer, writer);
            }
            finally
            {
                PythonNative.Py_DecRef(writer);
            }

            if (module == IntPtr.Zero)
                return "the Python bootstrap failed: " + (PythonNative.TakeError() ?? "no reason given");

            _module = module;
            _runFunction = PythonNative.PyObject_GetAttrString(module, PythonNative.Utf8("run"));
            if (_runFunction == IntPtr.Zero)
                return "the Python bootstrap published no run(): " + (PythonNative.TakeError() ?? "no reason given");

            Version = PythonNative.AttributeAsString(module, "version") ?? string.Empty;

            // The installer has served its purpose and __main__ is replaced for
            // every run anyway; removing it keeps the namespace a script starts
            // in identical to a real interpreter's.
            PythonNative.PyObject_SetAttrString(main, PythonNative.Utf8(PythonBootstrap.InstallFunction), IntPtr.Zero);
            PythonNative.PyErr_Clear();
            return null;
        }
        finally
        {
            PythonNative.Py_DecRef(installer);
        }
    }

    /// <summary>
    /// Builds the callable <c>sys.stdout.write</c> ends up calling: a
    /// <c>PyCFunction</c> over <see cref="NativeWrite"/>. The method definition
    /// is allocated once and never freed, because CPython keeps a bare pointer
    /// to it for as long as the function object lives — which is for the life of
    /// the process.
    /// </summary>
    private static IntPtr CreateWriteCallable()
    {
        IntPtr name = Marshal.StringToHGlobalAnsi("_tensoragent_write");
        IntPtr definition = Marshal.AllocHGlobal(IntPtr.Size * 4);
        new Span<byte>((void*)definition, IntPtr.Size * 4).Clear();
        Marshal.WriteIntPtr(definition, 0, name);
        Marshal.WriteIntPtr(definition, IntPtr.Size, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&NativeWrite);
        Marshal.WriteInt32(definition, IntPtr.Size * 2, PythonNative.MethVarargs);
        Marshal.WriteIntPtr(definition, IntPtr.Size * 3, IntPtr.Zero);
        return PythonNative.PyCFunction_NewEx(definition, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// <c>_tensoragent_write(stream, text)</c>, called by the Python stream
    /// objects with the GIL held on the interpreter thread. It appends to the
    /// run's capture, which fires the caller's per-line tap synchronously — the
    /// reason a tap must never call back into Python.
    /// </summary>
    [UnmanagedCallersOnly]
    private static IntPtr NativeWrite(IntPtr self, IntPtr args)
    {
        nint written = 0;
        try
        {
            if (PythonNative.PyTuple_Size(args) >= 2)
            {
                nint stream = PythonNative.PyLong_AsLong(PythonNative.PyTuple_GetItem(args, 0));
                string? text = PythonNative.ReadString(PythonNative.PyTuple_GetItem(args, 1));
                if (text is { Length: > 0 })
                {
                    written = text.Length;
                    (stream == 2 ? s_stderr : s_stdout)?.Write(text);
                }
            }
        }
        catch
        {
            // A managed exception must never cross back into the interpreter:
            // it would unwind through CPython frames that know nothing about it.
            // Losing a line of output is the lesser failure.
        }
        PythonNative.PyErr_Clear();
        return PythonNative.PyLong_FromSsize_t(written);
    }

    /// <summary>
    /// Raises <c>KeyboardInterrupt</c> in the run it was queued for. Returning -1
    /// tells the evaluation loop an exception is set.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int PendingInterrupt(IntPtr argument)
    {
        if (Volatile.Read(ref s_currentRun) != (long)argument)
            return 0;
        PythonNative.PyErr_SetString(
            PythonNative.ExceptionType("PyExc_KeyboardInterrupt"),
            PythonNative.Utf8("the run exceeded its time limit"));
        return -1;
    }

    // =====================================================================================
    // calls (worker thread, GIL held)
    // =====================================================================================

    private int Call(string payload, OutputCapture stderr)
    {
        IntPtr argument = PythonNative.PyUnicode_FromString(PythonNative.Utf8(payload));
        if (argument == IntPtr.Zero)
        {
            PythonNative.PyErr_Clear();
            stderr.Write("python: the run request could not be handed to the interpreter\n");
            return 1;
        }

        IntPtr result;
        try
        {
            result = PythonNative.PyObject_CallOneArg(_runFunction, argument);
        }
        finally
        {
            PythonNative.Py_DecRef(argument);
        }

        if (result == IntPtr.Zero)
        {
            // The bootstrap turns a script's exception into a traceback itself,
            // so anything arriving here is a fault in the bootstrap.
            stderr.Write("python: " + (PythonNative.TakeError() ?? "the interpreter failed without a reason") + "\n");
            return 1;
        }

        try
        {
            nint code = PythonNative.PyLong_AsLong(result);
            if (code == -1 && PythonNative.PyErr_Occurred() != IntPtr.Zero)
            {
                PythonNative.PyErr_Clear();
                return 1;
            }
            return (int)code;
        }
        finally
        {
            PythonNative.Py_DecRef(result);
        }
    }

    private static string? Compile(string source, string path)
    {
        IntPtr code = PythonNative.Py_CompileString(
            PythonNative.Utf8(source), PythonNative.Utf8(path), PythonNative.PyFileInput);
        if (code != IntPtr.Zero)
        {
            PythonNative.Py_DecRef(code);
            return null;
        }

        IntPtr exception = PythonNative.PyErr_GetRaisedException();
        if (exception == IntPtr.Zero)
        {
            PythonNative.PyErr_Clear();
            return $"{path}: the source could not be compiled";
        }

        try
        {
            string? message = PythonNative.AttributeAsString(exception, "msg");
            long line = PythonNative.AttributeAsLong(exception, "lineno", 0);
            if (message is null)
            {
                IntPtr text = PythonNative.PyObject_Str(exception);
                message = PythonNative.ReadString(text) ?? "invalid syntax";
                if (text != IntPtr.Zero)
                    PythonNative.Py_DecRef(text);
            }
            return line > 0 ? $"{path}:{line}: {message}" : $"{path}: {message}";
        }
        finally
        {
            PythonNative.Py_DecRef(exception);
            PythonNative.PyErr_Clear();
        }
    }

    /// <summary>
    /// The JSON one run is described by. Built with a writer rather than a
    /// serializer so nothing here needs reflection, which is what keeps it
    /// working under full ahead-of-time compilation on the device.
    /// </summary>
    internal static string CreatePayload(
        string mode,
        string target,
        IReadOnlyList<string> argv,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> pathFront,
        IReadOnlyList<string> pathBack,
        string? standardInput)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", mode);
            writer.WriteString("target", target);
            writer.WriteString("cwd", workingDirectory);
            writer.WriteString("stdin", standardInput ?? string.Empty);
            writer.WriteStartArray("argv");
            foreach (string value in argv)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
            writer.WriteStartArray("path_front");
            foreach (string value in pathFront)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
            writer.WriteStartArray("path_back");
            foreach (string value in pathBack)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
            writer.WriteStartObject("env");
            foreach ((string key, string value) in environment)
                writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
