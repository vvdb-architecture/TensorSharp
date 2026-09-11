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

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// <c>require</c> and the three modules behind it.
///
/// <para>
/// The module set is short and finishes there. A host with no npm cannot pretend
/// otherwise, and the failure mode that matters is what a model gets when it
/// reaches for something absent: a named error saying this host has no npm, not
/// an empty object that turns into <c>undefined is not a function</c> somewhere
/// downstream. Every absent module fails at the <c>require</c>.
/// </para>
/// </summary>
internal sealed partial class NodeHost
{
    /// <summary>Everything <c>require</c> will answer to, besides a relative file.</summary>
    private static readonly string[] BuiltinModules = { "fs", "path", "os" };

    private readonly Dictionary<string, IntPtr> _builtins = new(StringComparer.Ordinal);

    /// <summary>Loaded files, keyed by REAL path, so one file loaded by two spellings is one module.</summary>
    private readonly Dictionary<string, IntPtr> _modules = new(StringComparer.Ordinal);

    private void InstallRequire() => _js.SetProperty(_js.Global, "require", MakeRequire(Cwd));

    /// <summary>
    /// A <c>require</c> bound to one directory. Each module gets its own, because
    /// <c>'./util'</c> means something different depending on who wrote it.
    /// </summary>
    private IntPtr MakeRequire(string directory)
    {
        IntPtr require = _js.NewFunction("require", (js, _, args) => Require(js.ToStringValue(At(args, 0)), directory));
        _js.SetFunction(require, "resolve", (js, _, args) =>
        {
            string request = js.ToStringValue(At(args, 0));
            string? file = ResolveModuleFile(request, directory);
            return file is null
                ? throw new JsHostException($"Cannot find module '{request}'") { Code = "MODULE_NOT_FOUND" }
                : js.String(file);
        });
        _js.SetProperty(require, "main", _js.Undefined);
        return require;
    }

    private IntPtr Require(string request, string fromDirectory)
    {
        string name = request.StartsWith("node:", StringComparison.Ordinal) ? request[5..] : request;
        if (Array.IndexOf(BuiltinModules, name) >= 0)
            return Builtin(name);

        bool relative = request.StartsWith("./", StringComparison.Ordinal)
            || request.StartsWith("../", StringComparison.Ordinal)
            || request.StartsWith("/", StringComparison.Ordinal)
            || request is "." or "..";
        if (!relative)
        {
            throw new JsHostException(
                $"Cannot find module '{request}'. This host runs JavaScript in an embedded JavaScriptCore VM with no npm "
                + $"and no node_modules directory, so only relative files ('./lib.js') and the built-in modules "
                + $"{string.Join(", ", BuiltinModules)} can be required.")
            { Code = "MODULE_NOT_FOUND" };
        }

        // Resolve the bare request first so a path outside the sandbox is refused
        // with the confinement wording rather than reported as a missing file.
        ResolveRead(PosixPath.Resolve(fromDirectory, new[] { request }));

        string? file = ResolveModuleFile(request, fromDirectory);
        if (file is null)
            throw new JsHostException($"Cannot find module '{request}' from '{fromDirectory}'") { Code = "MODULE_NOT_FOUND" };
        return LoadModule(file);
    }

    /// <summary>Node's extension probe, trimmed to what this host serves: exact, <c>.js</c>, <c>.json</c>, <c>/index.js</c>.</summary>
    private string? ResolveModuleFile(string request, string fromDirectory)
    {
        string basePath = PosixPath.Resolve(fromDirectory, new[] { request });
        foreach (string candidate in new[] { basePath, basePath + ".js", basePath + ".json", basePath + "/index.js" })
        {
            if (_paths.TryResolve(candidate, Cwd, PathAccess.Read, out string real, out _) && File.Exists(real))
                return real;
        }
        return null;
    }

    private IntPtr Builtin(string name)
    {
        if (_builtins.TryGetValue(name, out IntPtr cached))
            return cached;
        IntPtr module = name switch
        {
            "fs" => BuildFileSystemModule(),
            "path" => BuildPathModule(),
            "os" => BuildOsModule(),
            _ => throw new JsHostException($"Cannot find module '{name}'") { Code = "MODULE_NOT_FOUND" },
        };
        _js.ProtectUntilDispose(module);
        _builtins[name] = module;
        return module;
    }

    // ---- loading a file --------------------------------------------------------------

    private IntPtr LoadModule(string realPath)
    {
        if (_modules.TryGetValue(realPath, out IntPtr cached))
            return _js.GetProperty(cached, "exports");

        IntPtr module = NewModuleObject(realPath);
        _js.ProtectUntilDispose(module);
        // Cached BEFORE evaluation: a cycle has to see the partial exports rather
        // than re-entering the file, which is exactly what Node does.
        _modules[realPath] = module;

        string source = File.ReadAllText(realPath, Encoding.UTF8);
        if (realPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr parsed = _js.FromJson(source);
            if (parsed == IntPtr.Zero)
                throw new JsHostException($"Unexpected token in JSON at {realPath}") { Code = "ERR_INVALID_JSON" };
            _js.SetProperty(module, "exports", parsed);
            _js.SetProperty(module, "loaded", _js.Boolean(true));
            return parsed;
        }

        EvaluateModule(module, source, realPath);
        return _js.GetProperty(module, "exports");
    }

    private IntPtr NewModuleObject(string filename)
    {
        IntPtr module = _js.NewObject();
        _js.SetProperty(module, "id", _js.String(filename));
        _js.SetProperty(module, "filename", _js.String(filename));
        _js.SetProperty(module, "path", _js.String(PosixPath.Dirname(filename)));
        _js.SetProperty(module, "loaded", _js.Boolean(false));
        _js.SetProperty(module, "exports", _js.NewObject());
        return module;
    }

    /// <summary>
    /// CommonJS, actually implemented: the file is compiled inside Node's own
    /// five-parameter wrapper and then called.
    ///
    /// <para>
    /// The wrapper's header deliberately carries no newline, so line 1 of the file
    /// is still line 1 of what the engine compiled and a stack trace points at the
    /// line the author wrote. It is also what gives a module its own scope — a
    /// <c>var</c> at the top of a required file must not become a global, and
    /// concatenating files instead of wrapping them is precisely the bug that
    /// would cause.
    /// </para>
    /// </summary>
    private void EvaluateModule(IntPtr module, string source, string filename)
    {
        IntPtr wrapper = _js.Evaluate(WrapModule(source), filename, 1, out JsErrorInfo? syntaxError);
        if (syntaxError is not null)
            throw new JsHostException(syntaxError.Summary) { Code = "ERR_SYNTAX", Path = filename };

        string directory = PosixPath.Dirname(filename);
        IntPtr[] arguments =
        {
            _js.GetProperty(module, "exports"),
            MakeRequire(directory),
            module,
            _js.String(filename),
            _js.String(directory),
        };
        _js.TryCall(wrapper, _js.Undefined, arguments, out JsErrorInfo? error, out IntPtr thrown);
        if (error is not null)
        {
            // A module that fails is not a loaded module; a later require retries it.
            _modules.Remove(filename);
            _js.ProtectUntilDispose(thrown);
            throw new JsRethrowException(thrown, error.Summary);
        }
        _js.SetProperty(module, "loaded", _js.Boolean(true));
    }

    private static string WrapModule(string source)
        => "(function (exports, require, module, __filename, __dirname) {" + StripShebang(source) + "\n});";

    /// <summary>Drops a leading <c>#!</c> line, keeping the newline so line numbers do not shift.</summary>
    private static string StripShebang(string source)
    {
        if (!source.StartsWith("#!", StringComparison.Ordinal))
            return source;
        int newline = source.IndexOf('\n');
        return newline < 0 ? string.Empty : source[newline..];
    }

    /// <summary>
    /// Runs the entry point in the same CommonJS wrapper every other module gets,
    /// so <c>__dirname</c>, <c>module.exports</c> and a working <c>require</c> mean
    /// the same thing at the top of the program as inside it.
    /// </summary>
    internal JsErrorInfo? RunMain(string source, string filename, string directory)
    {
        IntPtr wrapper = _js.Evaluate(WrapModule(source), filename, 1, out JsErrorInfo? syntaxError);
        if (syntaxError is not null)
            return syntaxError;

        IntPtr module = NewModuleObject(filename);
        _js.ProtectUntilDispose(module);
        _modules[filename] = module;

        IntPtr require = MakeRequire(directory);
        _js.SetProperty(require, "main", module);
        _js.SetProperty(_js.Global, "require", require);
        IntPtr[] arguments =
        {
            _js.GetProperty(module, "exports"),
            require,
            module,
            _js.String(filename),
            _js.String(directory),
        };
        _js.TryCall(wrapper, _js.Undefined, arguments, out JsErrorInfo? error);
        if (error is null)
            _js.SetProperty(module, "loaded", _js.Boolean(true));
        return error;
    }

    // ---- fs ----------------------------------------------------------------------------

    private IntPtr BuildFileSystemModule()
    {
        IntPtr fs = _js.NewObject();
        _js.SetFunction(fs, "readFileSync", ReadFileSync);
        _js.SetFunction(fs, "writeFileSync", (js, _, args) => WriteFile(js, args, append: false));
        _js.SetFunction(fs, "appendFileSync", (js, _, args) => WriteFile(js, args, append: true));
        _js.SetFunction(fs, "existsSync", ExistsSync);
        _js.SetFunction(fs, "mkdirSync", MkdirSync);
        _js.SetFunction(fs, "readdirSync", ReaddirSync);
        _js.SetFunction(fs, "statSync", StatSync);
        _js.SetFunction(fs, "rmSync", RmSync);
        _js.SetProperty(fs, "constants", _js.NewObject());

        // The promise half is JavaScript over these same calls; see NodeShim.
        _js.TryCall(_js.GetProperty(_shim, "addFsPromises"), _shim, new[] { fs }, out JsErrorInfo? error);
        if (error is not null)
            throw new JsHostException("fs.promises could not be installed: " + error.Summary);
        return fs;
    }

    private IntPtr ReadFileSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        IntPtr target = At(args, 0);
        IntPtr options = At(args, 1);
        bool decoded = !js.IsNullish(options) && (js.IsString(options) || !js.IsNullish(js.GetProperty(options, "encoding")));

        byte[] bytes;
        if (js.TypeOf(target) == JsType.Number)
        {
            // fs.readFileSync(0) is how a Node script slurps standard input.
            if ((int)js.ToNumber(target) != 0)
                throw new JsHostException("only file descriptor 0 (stdin) can be read on this host") { Code = "EBADF" };
            bytes = Encoding.UTF8.GetBytes(_run.StandardInput ?? string.Empty);
        }
        else
        {
            string spelled = js.ToStringValue(target);
            string path = ResolveRead(spelled);
            if (Directory.Exists(path))
                throw new JsHostException($"EISDIR: illegal operation on a directory, read") { Code = "EISDIR", Path = spelled };
            if (!File.Exists(path))
                throw Missing("open", spelled);
            bytes = File.ReadAllBytes(path);
        }

        return decoded ? js.String(DecodeBytes(bytes, EncodingName(js, options))) : MakeBuffer(js, bytes);
    }

    private IntPtr WriteFile(JsContext js, IntPtr[] args, bool append)
    {
        string spelled = js.ToStringValue(At(args, 0));
        string path = ResolveWrite(spelled);
        byte[] bytes = DataBytes(js, At(args, 1), EncodingName(js, At(args, 2)));

        string? parent = Path.GetDirectoryName(path);
        if (parent is not null && !Directory.Exists(parent))
            throw Missing("open", spelled);
        if (append)
            File.AppendAllBytes(path, bytes);
        else
            File.WriteAllBytes(path, bytes);
        return js.Undefined;
    }

    /// <summary>
    /// Node's <c>existsSync</c> never throws; it answers false for anything it
    /// cannot see. A path the policy refuses is exactly that from the script's
    /// side, so it answers false too rather than raising — the refusal shows up
    /// the moment the script tries to use the path for something.
    /// </summary>
    private IntPtr ExistsSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        if (!_paths.TryResolve(js.ToStringValue(At(args, 0)), Cwd, PathAccess.Read, out string path, out _))
            return js.Boolean(false);
        return js.Boolean(File.Exists(path) || Directory.Exists(path));
    }

    private IntPtr MkdirSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        string spelled = js.ToStringValue(At(args, 0));
        string path = ResolveWrite(spelled);
        IntPtr options = At(args, 1);
        bool recursive = js.IsObject(options) && js.ToBoolean(js.GetProperty(options, "recursive"));

        if (Directory.Exists(path))
        {
            if (recursive)
                return js.Undefined;
            throw new JsHostException($"EEXIST: file already exists, mkdir '{spelled}'") { Code = "EEXIST", Path = spelled };
        }
        string? parent = Path.GetDirectoryName(path);
        if (!recursive && parent is not null && !Directory.Exists(parent))
            throw Missing("mkdir", spelled);
        Directory.CreateDirectory(path);
        return js.Undefined;
    }

    /// <summary>Sorted, unlike Node, which returns directory order; a stable list is easier to diff and to test.</summary>
    private IntPtr ReaddirSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        string spelled = js.ToStringValue(At(args, 0));
        string path = ResolveRead(spelled);
        if (!Directory.Exists(path))
            throw Missing("scandir", spelled);
        var names = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            names.Add(Path.GetFileName(entry));
        names.Sort(StringComparer.Ordinal);
        return js.NewStringArray(names);
    }

    private IntPtr StatSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        string spelled = js.ToStringValue(At(args, 0));
        string path = ResolveRead(spelled);
        bool directory = Directory.Exists(path);
        if (!directory && !File.Exists(path))
            throw Missing("stat", spelled);

        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        IntPtr raw = js.NewObject();
        js.SetProperty(raw, "size", js.Number(directory ? 0 : ((FileInfo)info).Length));
        js.SetProperty(raw, "mode", js.Number(directory ? 0x41ED : 0x81A4));
        js.SetProperty(raw, "mtimeMs", js.Number(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        js.SetProperty(raw, "ctimeMs", js.Number(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        js.SetProperty(raw, "birthtimeMs", js.Number(new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds()));
        js.SetProperty(raw, "file", js.Boolean(!directory));
        js.SetProperty(raw, "directory", js.Boolean(directory));
        // The path was already resolved through every link, so a stat here can
        // never be looking at one.
        js.SetProperty(raw, "symlink", js.Boolean(false));

        IntPtr stats = js.TryCall(js.GetProperty(_shim, "makeStats"), _shim, new[] { raw }, out JsErrorInfo? error);
        return error is null ? stats : raw;
    }

    private IntPtr RmSync(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        string spelled = js.ToStringValue(At(args, 0));
        string path = ResolveWrite(spelled);
        IntPtr options = At(args, 1);
        bool recursive = js.IsObject(options) && js.ToBoolean(js.GetProperty(options, "recursive"));
        bool force = js.IsObject(options) && js.ToBoolean(js.GetProperty(options, "force"));

        if (Directory.Exists(path))
        {
            if (!recursive)
                throw new JsHostException($"ERR_FS_EISDIR: Path is a directory, rm '{spelled}'") { Code = "ERR_FS_EISDIR", Path = spelled };
            Directory.Delete(path, recursive: true);
            return js.Undefined;
        }
        if (File.Exists(path))
        {
            File.Delete(path);
            return js.Undefined;
        }
        if (!force)
            throw Missing("unlink", spelled);
        return js.Undefined;
    }

    private IntPtr MakeBuffer(JsContext js, byte[] bytes)
    {
        IntPtr buffer = js.TryCall(_bufferFrom, js.Undefined, new[] { js.NewUint8Array(bytes) }, out JsErrorInfo? error);
        return error is null ? buffer : js.NewUint8Array(bytes);
    }

    private byte[] DataBytes(JsContext js, IntPtr value, string encoding)
        => js.ToBytes(value) ?? EncodeText(js.ToStringValue(value), encoding);

    private static JsHostException Missing(string syscall, string path)
        => new($"ENOENT: no such file or directory, {syscall} '{path}'") { Code = "ENOENT", Path = path };

    // ---- path confinement --------------------------------------------------------------

    /// <summary>
    /// Every path a script names goes through here.
    ///
    /// <para>
    /// The check is <see cref="ConfinedPaths"/>'s, not a new one, and the refusal
    /// keeps its wording — <c>PATH: Permission denied</c> — so a model reads the
    /// same sentence from <c>fs.writeFileSync</c> that it reads from the shell's
    /// <c>cp</c>. Two spellings of one rule is how a model learns that the rule is
    /// negotiable.
    /// </para>
    /// </summary>
    private string ResolveRead(string spelled) => ResolveConfined(spelled, PathAccess.Read);

    private string ResolveWrite(string spelled) => ResolveConfined(spelled, PathAccess.Write);

    private string ResolveConfined(string spelled, PathAccess access)
    {
        try
        {
            return _paths.Resolve(spelled, Cwd, access);
        }
        catch (ConfinementException ex)
        {
            throw new JsHostException(ex.Message) { Code = "EACCES", Path = spelled };
        }
    }

    // ---- path --------------------------------------------------------------------------

    private IntPtr BuildPathModule()
    {
        IntPtr path = _js.NewObject();
        _js.SetFunction(path, "join", (js, _, args) => js.String(PosixPath.Join(Strings(js, args))));
        _js.SetFunction(path, "resolve", (js, _, args) => js.String(PosixPath.Resolve(Cwd, Strings(js, args))));
        _js.SetFunction(path, "normalize", (js, _, args) => js.String(PosixPath.Normalize(Text(js, At(args, 0)))));
        _js.SetFunction(path, "dirname", (js, _, args) => js.String(PosixPath.Dirname(Text(js, At(args, 0)))));
        _js.SetFunction(path, "basename", (js, _, args) => js.String(
            PosixPath.Basename(Text(js, At(args, 0)), js.IsNullish(At(args, 1)) ? null : js.ToStringValue(args[1]))));
        _js.SetFunction(path, "extname", (js, _, args) => js.String(PosixPath.Extname(Text(js, At(args, 0)))));
        _js.SetFunction(path, "relative", (js, _, args) => js.String(
            PosixPath.Relative(Cwd, Text(js, At(args, 0)), Text(js, At(args, 1)))));
        _js.SetFunction(path, "isAbsolute", (js, _, args) => js.Boolean(PosixPath.IsAbsolute(Text(js, At(args, 0)))));
        _js.SetProperty(path, "sep", _js.String("/"));
        _js.SetProperty(path, "delimiter", _js.String(":"));
        // path.posix is path here, because there is no other kind on this host.
        _js.SetProperty(path, "posix", path);
        return path;
    }

    // ---- os ----------------------------------------------------------------------------

    private IntPtr BuildOsModule()
    {
        IntPtr os = _js.NewObject();
        _js.SetFunction(os, "platform", (js, _, _) => js.String(PlatformName));
        _js.SetFunction(os, "type", (js, _, _) => js.String("Darwin"));
        _js.SetFunction(os, "arch", (js, _, _) => js.String("arm64"));
        _js.SetFunction(os, "tmpdir", (js, _, _) => js.String(_policy.TempRoot));
        _js.SetFunction(os, "homedir", (js, _, _) => js.String(HomeDirectory));
        _js.SetProperty(os, "EOL", _js.String("\n"));
        return os;
    }

    /// <summary>The session's <c>HOME</c> when it has one, and the writable root otherwise.</summary>
    private string HomeDirectory
    {
        get
        {
            string home = _run.EnvironmentValue("HOME");
            return home.Length > 0 ? home : _policy.WorkRoot;
        }
    }

    // ---- shared argument helpers -------------------------------------------------------

    private static string Text(JsContext js, IntPtr value) => js.IsNullish(value) ? string.Empty : js.ToStringValue(value);

    private static IReadOnlyList<string> Strings(JsContext js, IntPtr[] args)
    {
        var values = new List<string>(args.Length);
        foreach (IntPtr arg in args)
            values.Add(Text(js, arg));
        return values;
    }
}
