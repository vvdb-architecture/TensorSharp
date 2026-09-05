// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>Archives, the embedded interpreters, and the two network clients.</summary>
internal static partial class ShellBuiltins
{
    // =====================================================================================
    // archives
    // =====================================================================================

    /// <summary>
    /// Where one archive member is allowed to land. A member name comes from the
    /// archive, not from the user, so it gets two checks: it must stay inside the
    /// extraction directory (the zip-slip / tar-slip rule that real unzip and tar
    /// enforce), and it must still satisfy the session's own write confinement.
    /// </summary>
    private static string ArchiveTarget(ShellExec exec, string destination, string member, string tool)
    {
        string name = member.Replace('\\', '/').TrimStart('/');
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string combined = Path.GetFullPath(Path.Combine(root, name));
        if (combined != root && !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ShellUsageException($"{tool}: refusing to extract '{member}' outside the destination directory");
        return exec.Paths.Resolve(combined, exec.State.Cwd, PathAccess.Write);
    }

    private static int Unzip(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "d");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("usage: unzip [-o] [-d dir] archive [members...]", 2);
        string archive = exec.ResolveRead(opts.Operands[0]);
        if (!File.Exists(archive))
            throw new ShellUsageException($"cannot find {opts.Operands[0]}");
        bool list = opts.Has("l", "Z");
        bool overwrite = opts.Has("o");
        bool never = opts.Has("n");
        string destination = exec.ResolveWrite(opts.Value("d") ?? ".");
        var members = opts.Operands.Skip(1).ToList();

        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (list)
        {
            io.Out.WriteLine("  Length      Date    Time    Name");
            long total = 0;
            int count = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (members.Count > 0 && !members.Any(m => GlobMatcher.IsMatch(m, entry.FullName))) continue;
                io.Out.WriteLine($"{entry.Length,9}  {entry.LastWriteTime:yyyy-MM-dd HH:mm}   {entry.FullName}");
                total += entry.Length;
                count++;
            }
            io.Out.WriteLine($"{total,9}                     {count} file{(count == 1 ? string.Empty : "s")}");
            return 0;
        }

        Directory.CreateDirectory(destination);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            exec.CheckCancel();
            if (members.Count > 0 && !members.Any(m => GlobMatcher.IsMatch(m, entry.FullName)))
                continue;
            // A member path is attacker-controlled: it must resolve inside the target.
            string target = ArchiveTarget(exec, destination, entry.FullName, "unzip");
            if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (File.Exists(target))
            {
                if (never) continue;
                if (!overwrite) { io.Error("unzip", $"{entry.FullName} already exists; pass -o to replace it"); return 1; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            io.Out.WriteLine($"  inflating: {entry.FullName}");
        }
        return 0;
    }

    private static int Zip(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: zip [-r] archive file...", 2);
        string archive = exec.ResolveWrite(opts.Operands[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? opts.Operands[0] : opts.Operands[0] + ".zip");
        bool recursive = opts.Has("r", "R");

        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using FileStream stream = File.Create(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (string operand in opts.Operands.Skip(1))
        {
            exec.CheckCancel();
            string path = exec.ResolveRead(operand);
            if (Directory.Exists(path))
            {
                if (!recursive) { io.Error("zip", $"{operand} is a directory (use -r)"); continue; }
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    string name = Path.Combine(Path.GetFileName(path.TrimEnd('/')), Path.GetRelativePath(path, file));
                    zip.CreateEntryFromFile(file, name.Replace('\\', '/'));
                    io.Out.WriteLine($"  adding: {name}");
                }
                continue;
            }
            if (!File.Exists(path)) { io.Error("zip", $"{operand}: No such file or directory"); continue; }
            zip.CreateEntryFromFile(path, Path.GetFileName(path));
            io.Out.WriteLine($"  adding: {Path.GetFileName(path)}");
        }
        return 0;
    }

    private static int Tar(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "fC");
        string? file = opts.Value("f", "file");
        string directory = opts.Value("C", "directory") ?? ".";
        bool create = opts.Has("c", "create");
        bool extract = opts.Has("x", "extract");
        bool list = opts.Has("t", "list");
        bool gzip = opts.Has("z", "gzip");
        bool verbose = opts.Has("v", "verbose");
        if (file is null)
            throw new ShellUsageException("this host's tar needs -f archive", 2);
        if (!create && !extract && !list)
            throw new ShellUsageException("one of -c, -x or -t is required", 2);
        if (file.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            gzip = true;

        if (create)
        {
            string archive = exec.ResolveWrite(file);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            using FileStream raw = File.Create(archive);
            using Stream stream = gzip ? new GZipStream(raw, CompressionLevel.Optimal) : raw;
            using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            foreach (string operand in opts.Operands)
            {
                exec.CheckCancel();
                string path = exec.ResolveRead(Path.Combine(directory, operand));
                if (Directory.Exists(path))
                {
                    foreach (string entry in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.Combine(operand, Path.GetRelativePath(path, entry)).Replace('\\', '/');
                        writer.WriteEntry(entry, name);
                        if (verbose) io.Out.WriteLine(name);
                    }
                    continue;
                }
                if (!File.Exists(path)) { io.Error("tar", $"{operand}: No such file or directory"); continue; }
                writer.WriteEntry(path, operand);
                if (verbose) io.Out.WriteLine(operand);
            }
            return 0;
        }

        string source = exec.ResolveRead(file);
        if (!File.Exists(source))
            throw new ShellUsageException($"{file}: No such file or directory");
        using (FileStream raw = File.OpenRead(source))
        using (Stream stream = gzip ? new GZipStream(raw, CompressionMode.Decompress) : raw)
        using (var reader = new TarReader(stream))
        {
            string destination = exec.ResolveWrite(directory);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                exec.CheckCancel();
                if (list)
                {
                    io.Out.WriteLine(entry.Name);
                    continue;
                }
                string target = ArchiveTarget(exec, destination, entry.Name, "tar");
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (verbose) io.Out.WriteLine(entry.Name);
            }
        }
        return 0;
    }

    private static int Gzip(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        if (opts.Has("d", "decompress"))
            return Gunzip(exec, argv, io);
        if (opts.Operands.Count == 0)
        {
            byte[] input = ShellText.ReadAllBytes(io.In);
            using var buffer = new MemoryStream();
            using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(input);
            io.Out.Write(buffer.ToArray());
            return 0;
        }
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveWrite(operand);
            if (!File.Exists(path)) { io.Error("gzip", $"{operand}: No such file or directory"); return 1; }
            using (FileStream source = File.OpenRead(path))
            using (FileStream destination = File.Create(path + ".gz"))
            using (var gz = new GZipStream(destination, CompressionLevel.Optimal))
                source.CopyTo(gz);
            if (!opts.Has("k", "keep"))
                File.Delete(path);
        }
        return 0;
    }

    private static int Gunzip(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        if (opts.Operands.Count == 0)
        {
            using var input = new MemoryStream(ShellText.ReadAllBytes(io.In));
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gz.CopyTo(buffer);
            io.Out.Write(buffer.ToArray());
            return 0;
        }
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveRead(operand);
            if (!File.Exists(path)) { io.Error("gunzip", $"{operand}: No such file or directory"); return 1; }
            string target = exec.ResolveWrite(path.EndsWith(".gz", StringComparison.Ordinal) ? path[..^3] : path + ".out");
            using (FileStream source = File.OpenRead(path))
            using (var gz = new GZipStream(source, CompressionMode.Decompress))
            using (FileStream destination = File.Create(target))
                gz.CopyTo(destination);
            if (!opts.Has("k", "keep"))
                File.Delete(path);
        }
        return 0;
    }

    // =====================================================================================
    // interpreters
    // =====================================================================================

    private static InterpreterContext ContextFor(ShellExec exec, ShellStreams io)
        => new(exec.State.Cwd, exec.State.ExportedEnvironment(), exec.Policy)
        {
            Timeout = exec.Context.Timeout,
            StandardInput = null,
            OnStdoutLine = null,
            OnStderrLine = null,
        };

    /// <summary>Runs an interpreter and folds its captured output into this shell's streams.</summary>
    private static int RunInterpreter(ShellExec exec, ShellStreams io, Func<InterpreterContext, CancellationToken, Task<ExecutionResult>> run, string? standardInput = null)
    {
        InterpreterContext context = ContextFor(exec, io) with { StandardInput = standardInput ?? ShellText.ReadAllText(io.In) };
        ExecutionResult result = run(context, exec.Cancellation).GetAwaiter().GetResult();
        if (result.Stdout.Length > 0) io.Out.Write(result.Stdout);
        if (result.Stderr.Length > 0) io.Err.Write(result.Stderr);
        if (result.TimedOut)
            io.Error("sh", $"the program timed out after {context.EffectiveTimeout.TotalSeconds:0.#} seconds");
        return result.ExitCode;
    }

    private static int Python(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (!exec.Policy.AllowScripts)
        {
            io.Error(argv[0], ExecutionPolicy.ScriptsDisabledMessage);
            return 126;
        }
        Python.IPythonRuntime? runtime = exec.Context.Python;
        if (runtime is null || !runtime.IsAvailable)
        {
            io.Error(argv[0], runtime?.UnavailableReason ?? "no Python interpreter is embedded in this build");
            return ExecutionResult.CommandNotFoundExitCode;
        }

        string? code = null, module = null, script = null;
        var arguments = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (script is not null || code is not null || module is not null)
            {
                arguments.Add(arg);
                continue;
            }
            switch (arg)
            {
                case "-c":
                    code = i + 1 < argv.Length ? argv[++i] : throw new ShellUsageException("-c needs an argument", 2);
                    continue;
                case "-m":
                    module = i + 1 < argv.Length ? argv[++i] : throw new ShellUsageException("-m needs an argument", 2);
                    continue;
                case "-u" or "-B" or "-E" or "-s" or "-S" or "-I" or "-q" or "-b" or "-O" or "-OO":
                    continue;
                case "-V" or "--version":
                    io.Out.WriteLine("Python " + runtime.Version);
                    return 0;
                case "-":
                    script = "-";
                    continue;
                default:
                    if (arg.StartsWith('-'))
                        throw new ShellUsageException($"unsupported option {arg}", 2);
                    script = arg;
                    continue;
            }
        }

        // pip is intentionally not staged into embedded CPython: installs are a
        // host operation, and package inspection reads the session's dist-info
        // directly. Keep Python's common spelling as an alias for that exact same
        // builtin instead of asking runpy for a module that does not exist. In the
        // assembled app the raw installer hook is null, so a nested install remains
        // refused while list/freeze stay useful.
        if (string.Equals(module, "pip", StringComparison.Ordinal))
        {
            var pipArguments = new string[arguments.Count + 1];
            pipArguments[0] = "pip";
            arguments.CopyTo(pipArguments, 1);
            return Pip(exec, pipArguments, io);
        }

        if (code is not null)
            return RunInterpreter(exec, io, (ctx, ct) => runtime.RunCodeAsync(code, arguments, ctx, ct));
        if (module is not null)
            return RunInterpreter(exec, io, (ctx, ct) => runtime.RunModuleAsync(module, arguments, ctx, ct));
        if (script is null || script == "-")
        {
            string source = ShellText.ReadAllText(io.In);
            return RunInterpreter(exec, io, (ctx, ct) => runtime.RunCodeAsync(source, arguments, ctx, ct), standardInput: string.Empty);
        }

        string path = exec.ResolveRead(script);
        if (!File.Exists(path))
        {
            io.Error(argv[0], $"can't open file '{script}': No such file or directory");
            return 2;
        }
        var scriptArguments = new List<string> { script };
        scriptArguments.AddRange(arguments);
        return RunInterpreter(exec, io, (ctx, ct) => runtime.RunScriptAsync(path, scriptArguments, ctx, ct));
    }

    private static int Node(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (!exec.Policy.AllowScripts)
        {
            io.Error(argv[0], ExecutionPolicy.ScriptsDisabledMessage);
            return 126;
        }
        JavaScript.IJavaScriptRuntime? runtime = exec.Context.JavaScript;
        if (runtime is null || !runtime.IsAvailable)
        {
            io.Error(argv[0], runtime?.UnavailableReason ?? "no JavaScript engine is embedded in this build");
            return ExecutionResult.CommandNotFoundExitCode;
        }

        string? code = null, script = null;
        var arguments = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (script is not null || code is not null)
            {
                arguments.Add(arg);
                continue;
            }
            switch (arg)
            {
                case "-e" or "--eval":
                    code = i + 1 < argv.Length ? argv[++i] : throw new ShellUsageException("-e needs an argument", 2);
                    continue;
                // node's -p is -e plus "print what the expression evaluated to". Running
                // it as a bare -e discards the value and prints nothing at all, which
                // looks to a model like a command that succeeded and produced no output.
                case "-p" or "--print":
                    code = i + 1 < argv.Length
                        ? "console.log(" + argv[++i] + ")"
                        : throw new ShellUsageException("-p needs an argument", 2);
                    continue;
                case "-v" or "--version":
                    io.Out.WriteLine("v(JavaScriptCore)");
                    return 0;
                default:
                    if (arg.StartsWith('-'))
                        continue;
                    script = arg;
                    continue;
            }
        }

        if (code is not null)
            return RunInterpreter(exec, io, (ctx, ct) => runtime.RunCodeAsync(code, arguments, ctx, ct));
        if (script is null)
        {
            io.Error(argv[0], "reading a program from standard input is not supported; pass a file or -e");
            return 2;
        }
        string path = exec.ResolveRead(script);
        if (!File.Exists(path))
        {
            io.Error(argv[0], $"Cannot find module '{script}'");
            return 1;
        }
        var scriptArguments = new List<string> { script };
        scriptArguments.AddRange(arguments);
        return RunInterpreter(exec, io, (ctx, ct) => runtime.RunScriptAsync(path, scriptArguments, ctx, ct));
    }

    private static int Pip(ShellExec exec, string[] argv, ShellStreams io)
    {
        // The AgentHost rule, kept: an install is READ out of the command line and
        // performed by the host, never executed by the model's own process. Here the
        // host is an installer hook; without one there is nothing to install with.
        var operands = new List<string>();
        string? command = null;
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (command is null && !arg.StartsWith('-')) { command = arg; continue; }
            if (arg is "--index-url" or "-i" or "--extra-index-url" or "--find-links" or "-f")
                throw new ShellUsageException($"{arg} is refused: the host chooses where packages come from", 2);
            if (arg is "--target" or "-t" or "--prefix" or "--root")
                throw new ShellUsageException($"{arg} is refused: the host chooses where packages are installed", 2);
            if (arg.StartsWith('-')) continue;
            operands.Add(arg);
        }

        switch (command)
        {
            case "install":
                break;
            case "list" or "freeze":
            {
                string? root = exec.Policy.PackageRoot;
                if (root is null || !Directory.Exists(root))
                    return 0;
                foreach (string info in Directory.GetDirectories(root, "*.dist-info").OrderBy(d => d, StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(info);
                    int dash = name.LastIndexOf('-');
                    io.Out.WriteLine(dash > 0 ? $"{name[..dash]}=={name[(dash + 1)..]}" : name);
                }
                return 0;
            }
            case null:
                throw new ShellUsageException("usage: pip install package...", 2);
            default:
                throw new ShellUsageException($"pip {command} is not available on this host", 2);
        }

        if (operands.Count == 0)
            throw new ShellUsageException("no packages named", 2);
        IInstallHook? installer = exec.Context.Installer;
        if (installer is null || !installer.CanInstall)
        {
            io.Error("pip", installer?.UnavailableReason ?? ExecutionPolicy.InstallsByHostMessage);
            return 1;
        }
        if (!exec.Policy.AllowNetwork)
        {
            io.Error("pip", ExecutionPolicy.NetworkDisabledMessage);
            return 1;
        }
        string target = exec.Policy.PackageRoot ?? Path.Combine(exec.Policy.WorkRoot, ".packages");
        var request = new InstallRequest("python", operands, target, exec.Policy) { OnOutputLine = line => io.Out.WriteLine(line) };
        ExecutionResult result = installer.InstallAsync(request, exec.Cancellation).GetAwaiter().GetResult();
        if (result.Stdout.Length > 0) io.Out.Write(result.Stdout);
        if (result.Stderr.Length > 0) io.Err.Write(result.Stderr);
        return result.ExitCode;
    }

    private static int Sh(ShellExec exec, string[] argv, ShellStreams io)
    {
        string? command = null, script = null;
        var arguments = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (command is not null || script is not null) { arguments.Add(arg); continue; }
            switch (arg)
            {
                case "-c":
                    command = i + 1 < argv.Length ? argv[++i] : throw new ShellUsageException("-c needs an argument", 2);
                    continue;
                case "-e" or "-x" or "-u" or "-l" or "--login" or "-s":
                    continue;
                default:
                    if (arg.StartsWith('-')) continue;
                    script = arg;
                    continue;
            }
        }

        if (command is not null)
            return exec.RunTextInSubshell(command, io, arguments, argv[0]);
        if (script is null)
            return exec.RunTextInSubshell(ShellText.ReadAllText(io.In), io, arguments, argv[0]);

        string path = exec.ResolveRead(script);
        if (!File.Exists(path))
        {
            io.Error(argv[0], $"{script}: No such file or directory");
            return ExecutionResult.CommandNotFoundExitCode;
        }
        return exec.RunTextInSubshell(File.ReadAllText(path, ShellText.Utf8), io, arguments, script);
    }

    // =====================================================================================
    // network
    // =====================================================================================

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static int Curl(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "oXHdA", false, "output", "request", "header", "data", "user-agent");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("usage: curl [-s] [-o file] [-X method] [-H header] [-d data] url", 2);
        string url = opts.Operands[0];
        string? output = opts.Value("o", "output");
        if (opts.Has("O", "remote-name"))
            output = Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).LocalPath);
        return Fetch(exec, io, url, output, opts.Value("X", "request"), opts.Values("H", "header"),
            opts.Value("d", "data", "data-raw"), silent: opts.Has("s", "silent"), showBody: output is null, "curl");
    }

    private static int Wget(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "O", false, "output-document");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("usage: wget [-q] [-O file] url", 2);
        string url = opts.Operands[0];
        string? output = opts.Value("O", "output-document");
        bool toStdout = output == "-";
        output = toStdout ? null : output ?? Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).LocalPath);
        if (string.IsNullOrEmpty(output) && !toStdout)
            output = "index.html";
        return Fetch(exec, io, url, output, null, Array.Empty<string>(), null,
            silent: opts.Has("q", "quiet"), showBody: toStdout, "wget");
    }

    private static int Fetch(ShellExec exec, ShellStreams io, string url, string? output, string? method,
        IReadOnlyList<string> headers, string? body, bool silent, bool showBody, string name)
    {
        if (!exec.Policy.AllowNetwork)
        {
            io.Error(name, ExecutionPolicy.NetworkDisabledMessage);
            return 6;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            io.Error(name, $"unsupported URL: {url}");
            return 1;
        }
        if (!exec.Policy.IsHostAllowed(uri.Host))
        {
            io.Error(name, ExecutionPolicy.HostNotAllowedMessage(uri.Host, exec.Policy.NetworkHosts));
            return 6;
        }

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method ?? (body is null ? "GET" : "POST")), uri);
            foreach (string header in headers)
            {
                int colon = header.IndexOf(':');
                if (colon > 0)
                    request.Headers.TryAddWithoutValidation(header[..colon].Trim(), header[(colon + 1)..].Trim());
            }
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            using HttpResponseMessage response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, exec.Cancellation);
            byte[] content;
            using (Stream stream = response.Content.ReadAsStream(exec.Cancellation))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                content = buffer.ToArray();
            }
            if (!silent)
                io.Err.WriteLine($"{(int)response.StatusCode} {response.ReasonPhrase} ({content.Length} bytes)");
            if (output is not null)
            {
                string path = exec.ResolveWrite(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
            if (showBody)
                io.Out.Write(content);
            return response.IsSuccessStatusCode ? 0 : 22;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            io.Error(name, ex.Message);
            return 7;
        }
    }
}
