// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TensorAgent.Core.Interop;

namespace TensorAgent.Core.Python;

/// <summary>
/// The CPython 3.13 C API, as much of it as embedding needs.
///
/// <para>
/// The library name is <c>__Internal</c> because on iOS there is no library to
/// load: <c>Python.framework</c> is linked into the app image and every
/// <c>Py_*</c> symbol lives in the main program, exactly as <c>GgmlOps</c> does
/// for <see cref="TensorSharp.GGML"/>. The import resolver below therefore
/// answers with <see cref="NativeLibrary.GetMainProgramHandle"/> — with one
/// addition: when a runtime root has been configured that carries a real
/// <c>libpython3.13</c> (a development or test machine), that file is opened
/// instead, which is what makes the live-interpreter tests runnable off-device.
/// </para>
/// <para>
/// Only one import resolver may be registered per assembly and
/// <see cref="TensorAgent.Core.JavaScript"/> wants one too, so neither registers
/// its own: both hand theirs to <see cref="NativeResolvers"/>, which registers
/// once and asks each in turn. Order no longer decides who works.
/// </para>
/// </summary>
internal static unsafe class PythonNative
{
    /// <summary>
    /// The name every <c>DllImport</c> below spells. It is not a file: the
    /// resolver maps it to the main program image (iOS) or to a real
    /// <c>libpython</c> (developer machines).
    /// </summary>
    internal const string LibraryName = "__Internal";

    /// <summary>The <c>compile()</c> start token for a whole file — CPython's <c>Py_file_input</c>.</summary>
    internal const int PyFileInput = 257;

    /// <summary><c>METH_VARARGS</c>: the callback takes <c>(self, args-tuple)</c>.</summary>
    internal const int MethVarargs = 0x0001;

    private static readonly object s_gate = new();
    private static string? s_libraryPath;
    private static IntPtr s_handle;
    private static bool s_registered;

    /// <summary>
    /// Non-null when the shared resolver could not be registered at all — which
    /// now takes something outside this assembly's own interops claiming the slot.
    /// Reported verbatim, because the symptom names nothing that would find it.
    /// </summary>
    internal static string? ResolverConflict => NativeResolvers.Conflict;

    /// <summary>
    /// Point the loader at a specific <c>libpython</c> before anything is
    /// imported. Off-device only: on iOS the interpreter is in the app image and
    /// <paramref name="path"/> is null.
    /// </summary>
    internal static void UseLibrary(string? path)
    {
        lock (s_gate)
        {
            s_libraryPath = path;
            s_handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Registers the resolver and returns the image the <c>Py_*</c> symbols
    /// should be in, or <see cref="IntPtr.Zero"/> with <paramref name="error"/>
    /// set. Never throws: a host with no CPython is an expected state, not a
    /// fault.
    /// </summary>
    internal static IntPtr TryLoad(out string? error)
    {
        lock (s_gate)
        {
            error = null;
            if (!s_registered)
            {
                s_registered = true;
                NativeResolvers.Register(Resolve);
            }

            if (s_handle != IntPtr.Zero)
                return s_handle;

            if (s_libraryPath is { Length: > 0 })
            {
                if (!NativeLibrary.TryLoad(s_libraryPath, out s_handle))
                {
                    error = $"the CPython library {s_libraryPath} could not be loaded";
                    return IntPtr.Zero;
                }
            }
            else
            {
                try
                {
                    s_handle = NativeLibrary.GetMainProgramHandle();
                }
                catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
                {
                    error = "the main program image is not available to look CPython symbols up in: " + ex.Message;
                    return IntPtr.Zero;
                }
            }

            // The honest availability check. A handle always comes back on Apple
            // platforms; whether it contains an interpreter is a different
            // question, and this is the cheapest way to ask it without a
            // P/Invoke that would throw.
            if (!NativeLibrary.TryGetExport(s_handle, "Py_IsInitialized", out _))
            {
                error = s_libraryPath is { Length: > 0 }
                    ? $"{s_libraryPath} exports no Py_IsInitialized, so it is not a CPython library"
                    : "no CPython is linked into this process (Python.framework is only linked into the iOS app)";
                s_handle = IntPtr.Zero;
                return IntPtr.Zero;
            }

            return s_handle;
        }
    }

    /// <summary>
    /// Reads a CPython data export — <c>PyExc_KeyboardInterrupt</c> is a
    /// <c>PyObject*</c> variable, not a function, so it cannot be a DllImport.
    /// </summary>
    internal static IntPtr ExceptionType(string name)
    {
        IntPtr handle = TryLoad(out _);
        if (handle == IntPtr.Zero || !NativeLibrary.TryGetExport(handle, name, out IntPtr slot))
            return IntPtr.Zero;
        return Marshal.ReadIntPtr(slot);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;
        return TryLoad(out _);
    }

    /// <summary>NUL-terminated UTF-8, which is what every <c>const char*</c> below wants.</summary>
    internal static byte[] Utf8(string text)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    // =====================================================================================
    // PyStatus / PyConfig
    // =====================================================================================

    /// <summary>CPython's <c>PyStatus</c>: an OK/ERROR/EXIT tag plus the failing function and message.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PyStatus
    {
        public int Type;
        public IntPtr Func;
        public IntPtr ErrMsg;
        public int ExitCode;

        public readonly bool IsError => Type != 0;

        public readonly string Describe()
        {
            string? message = ErrMsg == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ErrMsg);
            string? function = Func == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Func);
            return (message ?? "initialization failed") + (function is null ? string.Empty : $" (in {function})");
        }
    }

    /// <summary><c>PyWideStringList</c>: the shape of <c>argv</c> and <c>module_search_paths</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PyWideStringList
    {
        public nint Length;
        public IntPtr Items;
    }

    /// <summary>
    /// CPython 3.13's <c>PyConfig</c>, field for field.
    ///
    /// <para>
    /// This mirror exists because 3.13 has no accessor API — <c>PyConfig_Get</c>
    /// arrived in 3.14 — so the only way to set <c>home</c> or append to
    /// <c>module_search_paths</c> is to know where they are. Guessing wrong
    /// would scribble over the interpreter's own state, so nothing is written
    /// until <see cref="PythonConfigLayout.Verify"/> has read a dozen fields
    /// whose values <c>PyConfig_InitIsolatedConfig</c> is documented to set and
    /// found every one of them where this declaration says it is. The Windows
    /// (<c>legacy_windows_stdio</c>), <c>Py_STATS</c> and <c>Py_DEBUG</c>
    /// conditional fields are deliberately absent: this build only ever loads an
    /// Apple release interpreter.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PyConfig
    {
        public int ConfigInit;
        public int Isolated;
        public int UseEnvironment;
        public int DevMode;
        public int InstallSignalHandlers;
        public int UseHashSeed;
        public ulong HashSeed;
        public int FaultHandler;
        public int Tracemalloc;
        public int PerfProfiling;
        public int ImportTime;
        public int CodeDebugRanges;
        public int ShowRefCount;
        public int DumpRefs;
        public IntPtr DumpRefsFile;
        public int MallocStats;
        public IntPtr FilesystemEncoding;
        public IntPtr FilesystemErrors;
        public IntPtr PycachePrefix;
        public int ParseArgv;
        public PyWideStringList OrigArgv;
        public PyWideStringList Argv;
        public PyWideStringList XOptions;
        public PyWideStringList WarnOptions;
        public int SiteImport;
        public int BytesWarning;
        public int WarnDefaultEncoding;
        public int Inspect;
        public int Interactive;
        public int OptimizationLevel;
        public int ParserDebug;
        public int WriteBytecode;
        public int Verbose;
        public int Quiet;
        public int UserSiteDirectory;
        public int ConfigureCStdio;
        public int BufferedStdio;
        public IntPtr StdioEncoding;
        public IntPtr StdioErrors;
        public IntPtr CheckHashPycsMode;
        public int UseFrozenModules;
        public int SafePath;
        public int IntMaxStrDigits;
        public int CpuCount;
        public int PathConfigWarnings;
        public IntPtr ProgramName;
        public IntPtr PythonPathEnv;
        public IntPtr Home;
        public IntPtr PlatLibDir;
        public int ModuleSearchPathsSet;
        public PyWideStringList ModuleSearchPaths;
        public IntPtr StdlibDir;
        public IntPtr Executable;
        public IntPtr BaseExecutable;
        public IntPtr Prefix;
        public IntPtr BasePrefix;
        public IntPtr ExecPrefix;
        public IntPtr BaseExecPrefix;
        public int SkipSourceFirstLine;
        public IntPtr RunCommand;
        public IntPtr RunModule;
        public IntPtr RunFilename;
        public IntPtr SysPath0;
        public int InstallImportlib;
        public int InitMain;
        public int IsPythonBuild;
    }

    // =====================================================================================
    // initialization
    // =====================================================================================

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Py_IsInitialized();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyConfig_InitIsolatedConfig(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyConfig_Clear(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern PyStatus PyConfig_SetBytesString(IntPtr config, IntPtr field, byte[] value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern PyStatus PyWideStringList_Append(IntPtr list, IntPtr item);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern PyStatus Py_InitializeFromConfig(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr Py_DecodeLocale(byte[] argument, IntPtr size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyMem_RawFree(IntPtr pointer);

    // =====================================================================================
    // threads and interruption
    // =====================================================================================

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyEval_SaveThread();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PyGILState_Ensure();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyGILState_Release(int state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyErr_SetInterrupt();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Py_AddPendingCall(delegate* unmanaged<IntPtr, int> function, IntPtr argument);

    // =====================================================================================
    // running and compiling
    // =====================================================================================

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PyRun_SimpleString(byte[] source);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyRun_String(byte[] source, int start, IntPtr globals, IntPtr locals);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr Py_CompileString(byte[] source, byte[] filename, int start);

    // =====================================================================================
    // objects
    // =====================================================================================

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Py_IncRef(IntPtr o);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Py_DecRef(IntPtr o);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyImport_AddModule(byte[] name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyObject_GetAttrString(IntPtr o, byte[] name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PyObject_SetAttrString(IntPtr o, byte[] name, IntPtr value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyObject_CallOneArg(IntPtr callable, IntPtr argument);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyObject_Str(IntPtr o);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyUnicode_FromString(byte[] value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyUnicode_AsUTF8AndSize(IntPtr o, out nint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nint PyLong_AsLong(IntPtr o);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyLong_FromSsize_t(nint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nint PyTuple_Size(IntPtr tuple);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyTuple_GetItem(IntPtr tuple, nint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyDict_New();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PyDict_SetItemString(IntPtr dictionary, byte[] key, IntPtr value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyCFunction_NewEx(IntPtr definition, IntPtr self, IntPtr module);

    // =====================================================================================
    // errors
    // =====================================================================================

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyErr_Occurred();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr PyErr_GetRaisedException();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyErr_Clear();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void PyErr_SetString(IntPtr type, byte[] message);

    // =====================================================================================
    // small helpers over the above
    // =====================================================================================

    /// <summary>The UTF-8 of a <c>str</c> object, or null when it is not one.</summary>
    internal static string? ReadString(IntPtr unicode)
    {
        if (unicode == IntPtr.Zero)
            return null;
        IntPtr buffer = PyUnicode_AsUTF8AndSize(unicode, out nint size);
        if (buffer == IntPtr.Zero)
        {
            PyErr_Clear();
            return null;
        }
        return Encoding.UTF8.GetString((byte*)buffer, checked((int)size));
    }

    /// <summary>
    /// Takes the pending exception and renders it the way a caller wants to read
    /// it — <c>TypeError: ...</c> — clearing it in the process. Returns null when
    /// nothing was raised.
    /// </summary>
    internal static string? TakeError()
    {
        if (PyErr_Occurred() == IntPtr.Zero)
            return null;
        IntPtr exception = PyErr_GetRaisedException();
        if (exception == IntPtr.Zero)
        {
            PyErr_Clear();
            return "an unreadable error was raised";
        }
        try
        {
            string name = TypeName(exception) ?? "Exception";
            IntPtr text = PyObject_Str(exception);
            string message = ReadString(text) ?? string.Empty;
            if (text != IntPtr.Zero)
                Py_DecRef(text);
            return message.Length == 0 ? name : $"{name}: {message}";
        }
        finally
        {
            Py_DecRef(exception);
        }
    }

    /// <summary>The <c>__class__.__name__</c> of an object, for error text.</summary>
    internal static string? TypeName(IntPtr o)
    {
        IntPtr type = PyObject_GetAttrString(o, Utf8("__class__"));
        if (type == IntPtr.Zero)
        {
            PyErr_Clear();
            return null;
        }
        try
        {
            IntPtr name = PyObject_GetAttrString(type, Utf8("__name__"));
            if (name == IntPtr.Zero)
            {
                PyErr_Clear();
                return null;
            }
            try
            {
                return ReadString(name);
            }
            finally
            {
                Py_DecRef(name);
            }
        }
        finally
        {
            Py_DecRef(type);
        }
    }

    /// <summary>An attribute as an <c>int</c>, or <paramref name="fallback"/> when it is absent or not one.</summary>
    internal static long AttributeAsLong(IntPtr o, string name, long fallback)
    {
        IntPtr value = PyObject_GetAttrString(o, Utf8(name));
        if (value == IntPtr.Zero)
        {
            PyErr_Clear();
            return fallback;
        }
        try
        {
            nint number = PyLong_AsLong(value);
            if (number == -1 && PyErr_Occurred() != IntPtr.Zero)
            {
                PyErr_Clear();
                return fallback;
            }
            return number;
        }
        finally
        {
            Py_DecRef(value);
        }
    }

    /// <summary>An attribute as a <c>str</c>, or null.</summary>
    internal static string? AttributeAsString(IntPtr o, string name)
    {
        IntPtr value = PyObject_GetAttrString(o, Utf8(name));
        if (value == IntPtr.Zero)
        {
            PyErr_Clear();
            return null;
        }
        try
        {
            return ReadString(value);
        }
        finally
        {
            Py_DecRef(value);
        }
    }
}

/// <summary>
/// The guard that decides whether <see cref="PythonNative.PyConfig"/> may be
/// written to.
///
/// <para>
/// <c>PyConfig_InitIsolatedConfig</c> leaves a known pattern behind: an isolated
/// config with signal handlers off, bytecode writing on, the path warnings off
/// and importlib installed. Reading that pattern back through this build's field
/// offsets is a checksum on the whole declaration — a field added or removed
/// anywhere in CPython's struct shifts the tail and the last three values stop
/// reading <c>1, 1, 0</c>. If any probe disagrees the interpreter is not
/// initialized at all and the mismatch is named, because a wrong offset here is
/// a silent memory corruption and the house rule is that a fallback is never
/// silent.
/// </para>
/// </summary>
internal static class PythonConfigLayout
{
    // Every value here was read back out of a real PyConfig_InitIsolatedConfig,
    // not inferred from CPython's documentation: configure_c_stdio in particular
    // is 0 in an isolated config and 1 only in a "Python" one, and expecting the
    // documented 1 would have failed this guard against a perfectly good
    // interpreter. int_max_str_digits earns its place by being the one
    // distinctive value (4300, from sys.int_info) in the region where 3.12 and
    // 3.13 diverge; the run of 1, 1, 0 at the end is what catches a struct whose
    // tail has shifted by a field.
    private static readonly (string Field, int Expected)[] Probes =
    [
        (nameof(PythonNative.PyConfig.ConfigInit), 3),            // _PyConfig_INIT_ISOLATED
        (nameof(PythonNative.PyConfig.Isolated), 1),
        (nameof(PythonNative.PyConfig.UseEnvironment), 0),
        (nameof(PythonNative.PyConfig.InstallSignalHandlers), 0),
        (nameof(PythonNative.PyConfig.UseHashSeed), 0),
        (nameof(PythonNative.PyConfig.CodeDebugRanges), 1),
        (nameof(PythonNative.PyConfig.ParseArgv), 0),
        (nameof(PythonNative.PyConfig.SiteImport), 1),
        (nameof(PythonNative.PyConfig.WriteBytecode), 1),
        (nameof(PythonNative.PyConfig.UserSiteDirectory), 0),
        (nameof(PythonNative.PyConfig.ConfigureCStdio), 0),
        (nameof(PythonNative.PyConfig.BufferedStdio), 1),
        (nameof(PythonNative.PyConfig.UseFrozenModules), 1),
        (nameof(PythonNative.PyConfig.SafePath), 1),
        (nameof(PythonNative.PyConfig.IntMaxStrDigits), 4300),
        (nameof(PythonNative.PyConfig.PathConfigWarnings), 0),
        (nameof(PythonNative.PyConfig.ModuleSearchPathsSet), 0),
        (nameof(PythonNative.PyConfig.InstallImportlib), 1),
        (nameof(PythonNative.PyConfig.InitMain), 1),
        (nameof(PythonNative.PyConfig.IsPythonBuild), 0),
    ];

    /// <summary>
    /// How many bytes to hand <c>PyConfig_InitIsolatedConfig</c>. Generous on
    /// purpose: if the interpreter's struct is bigger than this declaration
    /// believes, the extra fields still land inside the allocation and the
    /// probes below report the mismatch, instead of the write landing in
    /// somebody else's heap block.
    /// </summary>
    internal static int AllocationSize => Math.Max(Marshal.SizeOf<PythonNative.PyConfig>() + 512, 2048);

    /// <summary>The byte offset of one field of the mirrored struct.</summary>
    internal static int OffsetOf(string field) => (int)Marshal.OffsetOf<PythonNative.PyConfig>(field);

    /// <summary>
    /// Reads the probe fields out of a freshly initialized isolated config.
    /// Returns null when the declaration matches, or the first disagreement
    /// phrased for a human.
    /// </summary>
    internal static string? Verify(IntPtr config)
    {
        foreach ((string field, int expected) in Probes)
        {
            int offset = OffsetOf(field);
            int actual = Marshal.ReadInt32(config, offset);
            if (actual != expected)
            {
                return $"this build's PyConfig layout does not match the embedded CPython: "
                    + $"{field} at byte {offset} read {actual}, expected {expected}. "
                    + "The interpreter was not initialized; rebuild TensorAgent against the CPython version it ships.";
            }
        }
        return null;
    }
}
