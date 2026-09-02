// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Python;

/// <summary>
/// The Python half of the embedded interpreter: the streams, the sandbox and the
/// entry point every run goes through.
///
/// <para>
/// It lives here as source rather than as a file in the bundle for two reasons.
/// It has to run before anything else can — including before <c>import</c> is
/// trusted to read a file — and it has to be impossible for a run to have edited
/// it, which a string compiled into the app is and a file under the work root is
/// not.
/// </para>
/// <para>
/// Two pieces are executed, in this order: <see cref="InstallSource"/> once per
/// process (it only defines a function, because the argument is a native
/// callable that Python source cannot name), and <see cref="CreatePolicySource"/>
/// once per run. <see cref="CreateSandboxSource"/> is the two of them joined, and
/// is what tests read to see the sandbox a given policy produces.
/// </para>
/// </summary>
internal static class PythonBootstrap
{
    /// <summary>The name the bootstrap publishes itself under in <c>sys.modules</c>.</summary>
    internal const string ModuleName = "_tensoragent";

    /// <summary>The name of the installer function <see cref="InstallSource"/> defines.</summary>
    internal const string InstallFunction = "_tensoragent_install";

    /// <summary>The wording for anything that would have needed a second process.</summary>
    internal const string ProcessMessage =
        ExecutionPolicy.ProcessesUnavailableMessage
        + ": iOS does not let an app start another program, so there is no setting that turns this on";

    /// <summary>The wording for reaching around every other check through a native library.</summary>
    internal const string NativeMessage =
        "loading a native library is not permitted: it would step around every other check in this sandbox";

    /// <summary>
    /// Defines the installer. Executed once, immediately after
    /// <c>Py_InitializeFromConfig</c>, with <c>PyRun_SimpleString</c>.
    ///
    /// <para>
    /// <b>Output capture is real streaming.</b> <c>sys.stdout.write</c> calls
    /// straight into managed code through a <c>PyCFunction</c> built over an
    /// <c>[UnmanagedCallersOnly]</c> method — no buffering on the Python side, so
    /// a line reaches <c>OnStdoutLine</c> while the script is still running, and
    /// nothing is captured by redirecting file descriptors (which would have
    /// swallowed the host's own logging too).
    /// </para>
    /// </summary>
    internal static string InstallSource { get; } = $$"""
        # TensorAgent's embedded-CPython bootstrap. Defines the installer only:
        # the host calls it from C because its argument is a native callable.
        def {{InstallFunction}}(_native_write):
            import builtins, io, json, os, runpy, sys, threading, traceback, types

            # The live policy. A dict rather than a closure constant because the
            # audit hook below can never be replaced, while the policy changes
            # with every run. It starts closed: until a run sets it, nothing is
            # readable, nothing is writable and there is no network.
            _state = {'writable': (), 'readable': (), 'network': False, 'configured': False}
            _guard = threading.local()
            _boot_main = sys.modules['__main__']

            # ---------------------------------------------------------- streams
            class _TextSink(io.TextIOBase):
                # write() hands text to managed code immediately: the run's
                # OutputCapture appends it and fires the per-line tap, so the UI
                # streams a line the moment print() produces it.
                def __init__(self, stream, name):
                    self._stream = stream
                    self._label = name

                @property
                def name(self):
                    return self._label

                @property
                def encoding(self):
                    return 'utf-8'

                @property
                def errors(self):
                    return 'backslashreplace'

                def writable(self):
                    return True

                def readable(self):
                    return False

                def seekable(self):
                    return False

                def isatty(self):
                    return False

                def fileno(self):
                    raise io.UnsupportedOperation('the embedded interpreter has no file descriptors')

                def write(self, text):
                    if not isinstance(text, str):
                        raise TypeError('write() argument must be str, not ' + type(text).__name__)
                    if text:
                        _native_write(self._stream, text)
                    return len(text)

                def writelines(self, lines):
                    for line in lines:
                        self.write(line)

                def flush(self):
                    pass

            class _ByteSink(io.RawIOBase):
                # sys.stdout.buffer, for the libraries that write bytes. Decoded
                # here so the decoder state stays with the stream it belongs to.
                def __init__(self, text):
                    self._text = text

                def writable(self):
                    return True

                def write(self, data):
                    raw = bytes(data)
                    self._text.write(raw.decode('utf-8', 'backslashreplace'))
                    return len(raw)

                def flush(self):
                    pass

            _out = _TextSink(1, '<stdout>')
            _err = _TextSink(2, '<stderr>')
            _out.buffer = _ByteSink(_out)
            _err.buffer = _ByteSink(_err)
            sys.stdout = sys.__stdout__ = _out
            sys.stderr = sys.__stderr__ = _err

            # ---------------------------------------------------------- sandbox
            # Paths named by one argument that the call is about to write.
            _WRITE_ONE = frozenset((
                'os.mkdir', 'os.rmdir', 'os.remove', 'os.truncate', 'os.chmod',
                'os.chown', 'os.utime', 'os.setxattr', 'os.removexattr'))
            # Both arguments are writes: a hard link shares the inode, and a
            # symlink that leaves the sandbox is refused where it is made rather
            # than every time something follows it.
            _WRITE_TWO = frozenset(('os.rename', 'os.replace', 'os.link', 'os.symlink'))
            _READ_ONE = frozenset((
                'os.listdir', 'os.scandir', 'os.chdir', 'os.getxattr', 'os.listxattr'))
            # There is no child process on this host, ever, under any policy.
            _NO_PROCESS = frozenset((
                'os.system', 'os.exec', 'os.spawn', 'os.posix_spawn', 'os.fork',
                'os.forkpty', 'os.startfile', 'pty.spawn', 'subprocess.Popen',
                'os.kill', 'os.killpg'))
            # Refused under every policy: a dlopen/dlsym pair reaches the same
            # syscalls the rules above cover, from outside Python.
            _NO_NATIVE = frozenset((
                'ctypes.dlopen', 'ctypes.dlsym', 'ctypes.dlsym/handle',
                'ctypes.call_function', 'ctypes.cdata'))
            # Anything that opens a socket, resolves a name or speaks a protocol.
            _NETWORK = ('socket.', 'urllib.', 'http.client.', 'ftplib.', 'smtplib.',
                        'imaplib.', 'poplib.', 'nntplib.', 'telnetlib.', 'webbrowser.')

            def _denied(path, write):
                roots = _state['writable'] if write else _state['readable']
                verb = 'write under' if write else 'read under'
                if not roots:
                    return path + ': Permission denied (this run may not touch the file system)'
                return path + ': Permission denied (this run may only ' + verb + ' ' + ', '.join(roots) + ')'

            def _check(path, write, event):
                if path is None or isinstance(path, int):
                    # An int is an already-open descriptor, which only an
                    # allowed open could have produced.
                    return
                # Resolving the path itself must not re-enter the hook.
                _guard.busy = True
                try:
                    try:
                        text = os.fsdecode(path)
                    except Exception:
                        raise PermissionError(event + ': Permission denied (unreadable path)')
                    if text == '/dev/null':
                        return
                    # realpath, not the literal path: a symlink inside the work
                    # directory pointing at /etc would pass a prefix test and
                    # then read anything. This is the same walk ConfinedPaths
                    # does on the managed side.
                    real = os.path.realpath(text)
                    roots = _state['writable'] if write else _state['readable']
                    for root in roots:
                        if real == root or real.startswith(root + os.sep):
                            return
                    raise PermissionError(_denied(real, write))
                finally:
                    _guard.busy = False

            def _check_open(args):
                path = args[0] if len(args) > 0 else None
                mode = args[1] if len(args) > 1 else None
                flags = args[2] if len(args) > 2 else None
                write = False
                if isinstance(mode, str):
                    write = any(c in mode for c in 'wax+')
                if isinstance(flags, int) and flags:
                    write = write or bool(flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_APPEND | os.O_TRUNC))
                _check(path, write, 'open')

            def _hook(event, args):
                if getattr(_guard, 'busy', False):
                    return
                if event in _NO_PROCESS:
                    raise PermissionError(event + ': {{ProcessMessage}}')
                if event in _NO_NATIVE:
                    raise PermissionError(event + ': {{NativeMessage}}')
                if event.startswith(_NETWORK) and not _state['network']:
                    raise PermissionError(event + ': {{ExecutionPolicy.NetworkDisabledMessage}}')
                if event == 'open':
                    _check_open(args)
                elif event in _WRITE_ONE:
                    _check(args[0], True, event)
                elif event in _WRITE_TWO:
                    _check(args[0], True, event)
                    _check(args[1], True, event)
                elif event in _READ_ONE:
                    _check(args[0], False, event)

            sys.addaudithook(_hook)
            # CPython has no API that removes an audit hook -- not even for the
            # code that added it -- so this one runs for the life of the
            # interpreter and a run cannot uninstall its own sandbox. That is
            # also why the policy lives in a dict the hook reads: a later run
            # with a different policy replaces the contents, never the hook.
            #
            # Honest limit: this contains accidents and casual attempts, not an
            # adversary. Code running in this address space shares it with the
            # host, and no in-process check can change that.

            def _set_policy(writable, readable, network):
                _state['writable'] = tuple(writable)
                _state['readable'] = tuple(readable)
                _state['network'] = bool(network)
                _state['configured'] = True

            # ------------------------------------------------------------- run
            def _run(payload):
                request = json.loads(payload)
                mode = request['mode']
                target = request['target']
                argv = [str(a) for a in request['argv']]
                cwd = request['cwd']
                front = [p for p in request['path_front'] if p]
                back = [p for p in request['path_back'] if p]

                previous_cwd = os.getcwd()
                previous_argv = sys.argv
                previous_path = list(sys.path)
                previous_env = os.environ
                previous_stdin = sys.stdin
                code = 0
                try:
                    os.chdir(cwd)
                    sys.argv = argv
                    # A plain dict, not the process environment: nothing here can
                    # start a child that would inherit it, and mutating the app's
                    # real environment from a script would be a surprise.
                    os.environ = dict(request['env'])
                    sys.stdin = io.StringIO(request['stdin'] or '')
                    for entry in back:
                        if entry not in sys.path:
                            sys.path.append(entry)
                    for entry in reversed(front):
                        if entry in sys.path:
                            sys.path.remove(entry)
                        sys.path.insert(0, entry)

                    main = types.ModuleType('__main__')
                    main.__dict__['__builtins__'] = builtins
                    main.__dict__['__loader__'] = None
                    main.__dict__['__spec__'] = None
                    main.__dict__['__package__'] = None
                    main.__dict__['__annotations__'] = {}
                    sys.modules['__main__'] = main
                    try:
                        if mode == 'module':
                            runpy.run_module(target, run_name='__main__', alter_sys=True)
                        else:
                            if mode == 'script':
                                with open(target, 'rb') as handle:
                                    source = handle.read()
                                filename = target
                                main.__dict__['__file__'] = target
                            else:
                                source = target
                                filename = '<string>'
                            exec(compile(source, filename, 'exec', dont_inherit=True), main.__dict__)
                    except SystemExit as leaving:
                        value = leaving.code
                        if value is None:
                            code = 0
                        elif isinstance(value, int):
                            # What the OS would have kept of it.
                            code = value & 0xFF
                        else:
                            sys.stderr.write(str(value) + '\n')
                            code = 1
                    except BaseException:
                        raised = sys.exception()
                        trace = raised.__traceback__
                        if trace is not None:
                            # Drop this function's frame so the traceback starts
                            # where the real interpreter's would.
                            trace = trace.tb_next
                        traceback.print_exception(type(raised), raised, trace, file=sys.stderr)
                        code = 130 if isinstance(raised, KeyboardInterrupt) else 1
                finally:
                    sys.modules['__main__'] = _boot_main
                    sys.stdin = previous_stdin
                    os.environ = previous_env
                    sys.path[:] = previous_path
                    sys.argv = previous_argv
                    try:
                        sys.stdout.flush()
                        sys.stderr.flush()
                    except Exception:
                        pass
                    try:
                        os.chdir(previous_cwd)
                    except Exception:
                        pass
                return code

            module = types.ModuleType('{{ModuleName}}')
            module.set_policy = _set_policy
            module.run = _run
            module.version = '%d.%d.%d' % sys.version_info[:3]
            sys.modules['{{ModuleName}}'] = module
            return module
        """;

    /// <summary>
    /// The per-run half: the roots and the network switch, written out as Python
    /// literals. Mirroring them costs a few lines of generated source and buys a
    /// hook that decides without calling back into managed code — which matters
    /// because the hook fires on every <c>import</c> the standard library does.
    /// The roots are the REAL ones <see cref="ConfinedPaths"/> computed, and the
    /// hook resolves symlinks before comparing, so the two sides agree about
    /// what is inside the sandbox.
    /// </summary>
    /// <param name="policy">The run's policy.</param>
    /// <param name="extraReadable">
    /// Trees the interpreter itself must read that no policy would mention — the
    /// bundled standard library and the pre-staged packages.
    /// </param>
    internal static string CreatePolicySource(ExecutionPolicy policy, IEnumerable<string>? extraReadable = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var confined = new ConfinedPaths(policy, extraReadable);
        return CreatePolicySource(confined, policy.AllowNetwork);
    }

    /// <summary>The same, when the caller already built the confinement.</summary>
    internal static string CreatePolicySource(ConfinedPaths confined, bool allowNetwork)
    {
        ArgumentNullException.ThrowIfNull(confined);
        var source = new StringBuilder();
        source.AppendLine("# Generated for one run. The audit hook installed at startup reads these lists;");
        source.AppendLine("# it is never reinstalled, so this is the only thing a new policy changes.");
        source.AppendLine($"{ModuleName} = globals().get('{ModuleName}') or __import__('sys').modules['{ModuleName}']");
        source.AppendLine($"{ModuleName}.set_policy(");
        AppendList(source, "writable", confined.WritableRoots);
        AppendList(source, "readable", confined.ReadableRoots);
        source.AppendLine($"    network={(allowNetwork ? "True" : "False")},");
        source.AppendLine(")");
        return source.ToString();
    }

    /// <summary>
    /// The whole sandbox for one policy, as the interpreter sees it: the
    /// installer followed by the policy it will be given. The host executes the
    /// two halves separately — the call to <c>_tensoragent_install</c> happens
    /// from C in between, because its argument is a native callable — so this is
    /// a reading and testing view, not a script that is run as one piece.
    /// </summary>
    internal static string CreateSandboxSource(ExecutionPolicy policy, IEnumerable<string>? extraReadable = null)
        => InstallSource + "\n\n" + CreatePolicySource(policy, extraReadable);

    /// <summary>
    /// The managed twin of the hook's path test, for the decisions the host has
    /// to make before Python is involved — chiefly whether a script it was asked
    /// to run is one this run may read.
    /// </summary>
    internal static bool IsPathAllowed(ConfinedPaths confined, string path, string workingDirectory, bool forWrite)
    {
        ArgumentNullException.ThrowIfNull(confined);
        if (string.IsNullOrEmpty(path))
            return false;
        if (ConfinedPaths.IsDevNull(path))
            return true;
        return confined.TryResolve(path, workingDirectory, forWrite ? PathAccess.Write : PathAccess.Read, out _, out _);
    }

    /// <summary>
    /// The refusal the hook would have raised, in the same words, so a host-side
    /// refusal and an in-Python one read identically to the model.
    /// </summary>
    internal static string DeniedMessage(string path, bool forWrite, IReadOnlyList<string> roots)
    {
        string verb = forWrite ? "write under" : "read under";
        if (roots.Count == 0)
            return $"{path}: Permission denied (this run may not touch the file system)";
        return $"{path}: Permission denied (this run may only {verb} {string.Join(", ", roots)})";
    }

    private static void AppendList(StringBuilder source, string name, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            source.AppendLine($"    {name}=[],");
            return;
        }
        source.AppendLine($"    {name}=[");
        foreach (string root in roots)
            source.Append("        ").Append(Literal(root)).AppendLine(",");
        source.AppendLine("    ],");
    }

    /// <summary>A Python string literal. Paths are not trusted to be free of quotes or backslashes.</summary>
    internal static string Literal(string value)
    {
        var text = new StringBuilder(value.Length + 2);
        text.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': text.Append("\\\\"); break;
                case '\'': text.Append("\\'"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\0': text.Append("\\x00"); break;
                default: text.Append(c); break;
            }
        }
        text.Append('\'');
        return text.ToString();
    }
}
