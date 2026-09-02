// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Globalization;
using System.Text;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>The filesystem half of the builtin set.</summary>
internal static partial class ShellBuiltins
{
    private static int Ls(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool longFormat = opts.Has("l");
        bool all = opts.Has("a", "A", "all");
        bool recursive = opts.Has("R", "recursive");
        bool human = opts.Has("h", "human-readable");
        bool onePerLine = opts.Has("1") || longFormat;
        bool directoryItself = opts.Has("d");
        bool sortByTime = opts.Has("t");
        bool reverse = opts.Has("r");

        List<string> operands = opts.Operands.Count > 0 ? opts.Operands : new List<string> { "." };
        int status = 0;
        bool headers = operands.Count > 1 || recursive;
        bool first = true;

        foreach (string operand in operands)
        {
            exec.CheckCancel();
            string path;
            try { path = exec.ResolveRead(operand); }
            catch (ConfinementException ex) { io.Error("ls", ex.Message); status = 1; continue; }

            if (File.Exists(path) || (directoryItself && Directory.Exists(path)))
            {
                Emit(io, new[] { path }, operand, longFormat, human, onePerLine, nameOverride: operand);
                continue;
            }
            if (!Directory.Exists(path))
            {
                io.Error("ls", $"{operand}: No such file or directory");
                status = 1;
                continue;
            }
            status |= ListDirectory(exec, io, path, operand, longFormat, all, recursive, human, onePerLine, sortByTime, reverse, headers, ref first);
        }
        return status;
    }

    private static int ListDirectory(ShellExec exec, ShellStreams io, string path, string label,
        bool longFormat, bool all, bool recursive, bool human, bool onePerLine, bool sortByTime, bool reverse,
        bool headers, ref bool first)
    {
        exec.CheckCancel();
        if (headers)
        {
            if (!first) io.Out.WriteLine(string.Empty);
            io.Out.WriteLine(label + ":");
        }
        first = false;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(path); }
        catch (UnauthorizedAccessException) { io.Error("ls", $"{label}: Permission denied"); return 1; }

        IEnumerable<string> visible = all ? entries : entries.Where(e => !Path.GetFileName(e).StartsWith('.'));
        List<string> ordered = sortByTime
            ? visible.OrderByDescending(LastWriteOf).ToList()
            : visible.OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        if (reverse) ordered.Reverse();

        Emit(io, ordered, label, longFormat, human, onePerLine, nameOverride: null);

        if (recursive)
        {
            foreach (string entry in ordered.Where(Directory.Exists))
                ListDirectory(exec, io, entry, label.TrimEnd('/') + "/" + Path.GetFileName(entry),
                    longFormat, all, recursive: true, human, onePerLine, sortByTime, reverse, headers: true, ref first);
        }
        return 0;
    }

    private static DateTime LastWriteOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
    }

    private static void Emit(ShellStreams io, IEnumerable<string> paths, string label, bool longFormat, bool human, bool onePerLine, string? nameOverride)
    {
        var names = new List<string>();
        foreach (string entry in paths)
        {
            string name = nameOverride ?? Path.GetFileName(entry);
            if (!longFormat)
            {
                names.Add(name);
                continue;
            }
            bool isDirectory = Directory.Exists(entry);
            long size = 0;
            DateTime written = DateTime.MinValue;
            try
            {
                if (isDirectory) { written = Directory.GetLastWriteTime(entry); size = 4096; }
                else { var info = new FileInfo(entry); size = info.Length; written = info.LastWriteTime; }
            }
            catch (IOException) { }
            string sizeText = human ? ShellText.HumanSize(size) : size.ToString(CultureInfo.InvariantCulture);
            io.Out.WriteLine(string.Join(' ',
                (isDirectory ? "drwxr-xr-x" : "-rw-r--r--"),
                "1", "mobile", "staff",
                sizeText.PadLeft(human ? 6 : 9),
                written.ToString("MMM d HH:mm", CultureInfo.InvariantCulture),
                name));
        }
        if (longFormat)
            return;
        if (onePerLine)
        {
            foreach (string name in names)
                io.Out.WriteLine(name);
        }
        else if (names.Count > 0)
        {
            io.Out.WriteLine(string.Join("  ", names));
        }
    }

    private static int Cat(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool number = opts.Has("n");
        bool squeeze = opts.Has("s");
        bool showEnds = opts.Has("E");

        if (opts.Operands.Count == 0)
        {
            if (!number && !showEnds && !squeeze)
            {
                // Straight copy: a binary file must survive `cat a > b`.
                byte[] bytes = ShellText.ReadAllBytes(io.In);
                io.Out.Write(bytes);
                return 0;
            }
        }

        int status = 0, line = 0;
        bool lastBlank = false;
        foreach (string operand in opts.Operands.Count > 0 ? opts.Operands : new List<string> { "-" })
        {
            exec.CheckCancel();
            byte[] content;
            if (operand == "-")
            {
                content = ShellText.ReadAllBytes(io.In);
            }
            else
            {
                string path;
                try { path = exec.ResolveRead(operand); }
                catch (ConfinementException ex) { io.Error("cat", ex.Message); status = 1; continue; }
                if (Directory.Exists(path)) { io.Error("cat", $"{operand}: Is a directory"); status = 1; continue; }
                if (!File.Exists(path)) { io.Error("cat", $"{operand}: No such file or directory"); status = 1; continue; }
                content = File.ReadAllBytes(path);
            }

            if (!number && !showEnds && !squeeze)
            {
                io.Out.Write(content);
                continue;
            }
            foreach (string text in ShellText.Lines(ShellText.Utf8.GetString(content)))
            {
                if (squeeze && text.Length == 0 && lastBlank)
                    continue;
                lastBlank = text.Length == 0;
                string rendered = showEnds ? text + "$" : text;
                io.Out.WriteLine(number ? $"{++line,6}\t{rendered}" : rendered);
            }
        }
        return status;
    }

    private static int Mkdir(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "m");
        bool parents = opts.Has("p", "parents");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("missing operand", 2);
        int status = 0;
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveWrite(operand);
            if (Directory.Exists(path))
            {
                if (parents) continue;
                io.Error("mkdir", $"{operand}: File exists");
                status = 1;
                continue;
            }
            string? parent = Path.GetDirectoryName(path);
            if (!parents && parent is not null && !Directory.Exists(parent))
            {
                io.Error("mkdir", $"{operand}: No such file or directory");
                status = 1;
                continue;
            }
            Directory.CreateDirectory(path);
        }
        return status;
    }

    private static int Rmdir(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        int status = 0;
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveWrite(operand);
            if (!Directory.Exists(path)) { io.Error("rmdir", $"{operand}: No such file or directory"); status = 1; continue; }
            if (Directory.EnumerateFileSystemEntries(path).Any()) { io.Error("rmdir", $"{operand}: Directory not empty"); status = 1; continue; }
            Directory.Delete(path);
        }
        return status;
    }

    private static int Rm(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool recursive = opts.Has("r", "R", "recursive");
        bool force = opts.Has("f", "force");
        if (opts.Operands.Count == 0)
            return force ? 0 : throw new ShellUsageException("missing operand", 2);

        int status = 0;
        foreach (string operand in opts.Operands)
        {
            exec.CheckCancel();
            string path;
            try { path = exec.ResolveWrite(operand); }
            catch (ConfinementException ex)
            {
                if (force) continue;
                io.Error("rm", ex.Message);
                status = 1;
                continue;
            }
            if (Directory.Exists(path))
            {
                if (!recursive)
                {
                    io.Error("rm", $"{operand}: is a directory");
                    status = 1;
                    continue;
                }
                Directory.Delete(path, recursive: true);
                continue;
            }
            if (!File.Exists(path))
            {
                if (force) continue;
                io.Error("rm", $"{operand}: No such file or directory");
                status = 1;
                continue;
            }
            File.Delete(path);
        }
        return status;
    }

    private static int Cp(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool recursive = opts.Has("r", "R", "recursive", "a");
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: cp [-r] source... target", 2);

        string targetOperand = opts.Operands[^1];
        List<string> sources = opts.Operands[..^1];
        string target = exec.ResolveWrite(targetOperand);
        bool targetIsDirectory = Directory.Exists(target);
        if (sources.Count > 1 && !targetIsDirectory)
            throw new ShellUsageException($"target '{targetOperand}' is not a directory", 2);

        int status = 0;
        foreach (string sourceOperand in sources)
        {
            exec.CheckCancel();
            string source = exec.ResolveRead(sourceOperand);
            string destination = targetIsDirectory ? Path.Combine(target, Path.GetFileName(source.TrimEnd('/'))) : target;
            exec.Paths.Resolve(destination, exec.State.Cwd, PathAccess.Write);

            if (Directory.Exists(source))
            {
                if (!recursive) { io.Error("cp", $"{sourceOperand} is a directory (not copied)"); status = 1; continue; }
                CopyTree(exec, source, destination);
                continue;
            }
            if (!File.Exists(source)) { io.Error("cp", $"{sourceOperand}: No such file or directory"); status = 1; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        return status;
    }

    private static void CopyTree(ShellExec exec, string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            exec.CheckCancel();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            exec.CheckCancel();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int Mv(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: mv source... target", 2);
        string targetOperand = opts.Operands[^1];
        List<string> sources = opts.Operands[..^1];
        string target = exec.ResolveWrite(targetOperand);
        bool targetIsDirectory = Directory.Exists(target);
        if (sources.Count > 1 && !targetIsDirectory)
            throw new ShellUsageException($"target '{targetOperand}' is not a directory", 2);

        int status = 0;
        foreach (string sourceOperand in sources)
        {
            string source = exec.ResolveWrite(sourceOperand);
            string destination = targetIsDirectory ? Path.Combine(target, Path.GetFileName(source.TrimEnd('/'))) : target;
            exec.Paths.Resolve(destination, exec.State.Cwd, PathAccess.Write);
            if (Directory.Exists(source))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.Move(source, destination);
                continue;
            }
            if (!File.Exists(source)) { io.Error("mv", $"{sourceOperand}: No such file or directory"); status = 1; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: true);
        }
        return status;
    }

    private static int Ln(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: ln -s target linkname", 2);
        if (!opts.Has("s", "symbolic"))
            throw new ShellUsageException("only symbolic links are supported on this host", 1);
        string target = opts.Operands[0];
        string link = exec.ResolveWrite(opts.Operands[1]);
        if (File.Exists(link) || Directory.Exists(link))
        {
            if (!opts.Has("f", "force"))
                throw new ShellUsageException($"{opts.Operands[1]}: File exists");
            File.Delete(link);
        }
        // The link's TARGET must also be inside the session, or the link would be a way
        // to read outside it; the path guard walks links, but refusing here is clearer.
        exec.Paths.Resolve(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(link)!, target),
            exec.State.Cwd, PathAccess.Read);
        File.CreateSymbolicLink(link, target);
        return 0;
    }

    private static int Touch(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "dt");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("missing file operand", 2);
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveWrite(operand);
            if (!File.Exists(path))
            {
                if (opts.Has("c", "no-create")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Array.Empty<byte>());
                continue;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        return 0;
    }

    private static int Stat(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "f");
        string? format = opts.Value("f", "format", "printf");
        int status = 0;
        foreach (string operand in opts.Operands)
        {
            string path;
            try { path = exec.ResolveRead(operand); }
            catch (ConfinementException ex) { io.Error("stat", ex.Message); status = 1; continue; }
            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path)) { io.Error("stat", $"{operand}: No such file or directory"); status = 1; continue; }
            long size = isDirectory ? 4096 : new FileInfo(path).Length;
            DateTime modified = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);

            if (format is null)
            {
                io.Out.WriteLine($"  File: {operand}");
                io.Out.WriteLine($"  Size: {size}\t{(isDirectory ? "directory" : "regular file")}");
                io.Out.WriteLine($"Modify: {modified:yyyy-MM-dd HH:mm:ss} +0000");
                continue;
            }
            string rendered = format
                .Replace("%n", operand, StringComparison.Ordinal)
                .Replace("%s", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%z", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%Y", new DateTimeOffset(modified, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%F", isDirectory ? "directory" : "regular file", StringComparison.Ordinal);
            io.Out.WriteLine(Unescape(rendered, out _));
        }
        return status;
    }

    private static int ReadLink(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        int status = 0;
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveRead(operand);
            if (opts.Has("f", "canonicalize"))
            {
                io.Out.WriteLine(ConfinedPaths.RealPath(path));
                continue;
            }
            FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            if (info.LinkTarget is null) { io.Error("readlink", $"{operand}: Invalid argument"); status = 1; continue; }
            io.Out.WriteLine(info.LinkTarget);
        }
        return status;
    }

    private static int Basename(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length < 2)
            throw new ShellUsageException("usage: basename string [suffix]", 2);
        string name = Path.GetFileName(argv[1].TrimEnd('/'));
        if (name.Length == 0) name = "/";
        if (argv.Length > 2 && name.EndsWith(argv[2], StringComparison.Ordinal) && name != argv[2])
            name = name[..^argv[2].Length];
        io.Out.WriteLine(name);
        return 0;
    }

    private static int Dirname(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length < 2)
            throw new ShellUsageException("usage: dirname string", 2);
        string directory = Path.GetDirectoryName(argv[1].TrimEnd('/')) ?? string.Empty;
        io.Out.WriteLine(directory.Length == 0 ? "." : directory);
        return 0;
    }

    private static int RealPath(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        int status = 0;
        foreach (string operand in opts.Operands)
        {
            try { io.Out.WriteLine(ConfinedPaths.RealPath(exec.ResolveRead(operand))); }
            catch (ConfinementException ex) { io.Error("realpath", ex.Message); status = 1; }
        }
        return status;
    }

    private static int Find(ShellExec exec, string[] argv, ShellStreams io)
    {
        var roots = new List<string>();
        int i = 1;
        for (; i < argv.Length; i++)
        {
            if (argv[i].StartsWith('-') || argv[i] is "(" or "!")
                break;
            roots.Add(argv[i]);
        }
        if (roots.Count == 0)
            roots.Add(".");

        string? namePattern = null, inamePattern = null, pathPattern = null, typeFilter = null, newerThan = null;
        int maxDepth = int.MaxValue, minDepth = 0;
        bool delete = false, printPaths = true;
        long? sizeGreaterThan = null;
        for (; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "-name": namePattern = Next(argv, ref i); break;
                case "-iname": inamePattern = Next(argv, ref i); break;
                case "-path" or "-wholename": pathPattern = Next(argv, ref i); break;
                case "-type": typeFilter = Next(argv, ref i); break;
                case "-maxdepth": maxDepth = int.Parse(Next(argv, ref i), CultureInfo.InvariantCulture); break;
                case "-mindepth": minDepth = int.Parse(Next(argv, ref i), CultureInfo.InvariantCulture); break;
                case "-newer": newerThan = Next(argv, ref i); break;
                case "-size": sizeGreaterThan = ParseFindSize(Next(argv, ref i)); break;
                case "-delete": delete = true; printPaths = false; break;
                case "-print" or "-print0": printPaths = true; break;
                case "-prune" or "-depth" or "-follow": break;
                default:
                    throw new ShellUsageException($"unknown predicate '{argv[i]}'", 2);
            }
        }

        DateTime? newerThanTime = null;
        if (newerThan is not null)
            newerThanTime = LastWriteOf(exec.ResolveRead(newerThan));

        int status = 0;
        foreach (string rootOperand in roots)
        {
            exec.CheckCancel();
            string root;
            try { root = exec.ResolveRead(rootOperand); }
            catch (ConfinementException ex) { io.Error("find", ex.Message); status = 1; continue; }
            if (!Directory.Exists(root) && !File.Exists(root))
            {
                io.Error("find", $"{rootOperand}: No such file or directory");
                status = 1;
                continue;
            }
            var matches = new List<(string Display, string Path, bool IsDirectory)>();
            Walk(exec, root, rootOperand, 0, maxDepth, matches);
            foreach ((string display, string path, bool isDirectory) in matches)
            {
                int depth = display == rootOperand ? 0 : display.Count(c => c == '/') - rootOperand.TrimEnd('/').Count(c => c == '/');
                if (depth < minDepth) continue;
                string fileName = Path.GetFileName(path);
                if (namePattern is not null && !GlobMatcher.IsMatch(namePattern, fileName)) continue;
                if (inamePattern is not null && !GlobMatcher.IsMatch(inamePattern, fileName, ignoreCase: true)) continue;
                if (pathPattern is not null && !GlobMatcher.IsMatch(pathPattern, display)) continue;
                if (typeFilter == "f" && isDirectory) continue;
                if (typeFilter == "d" && !isDirectory) continue;
                if (newerThanTime is not null && LastWriteOf(path) <= newerThanTime) continue;
                if (sizeGreaterThan is not null && (isDirectory || new FileInfo(path).Length <= sizeGreaterThan)) continue;

                if (delete)
                {
                    exec.Paths.Resolve(path, exec.State.Cwd, PathAccess.Write);
                    if (isDirectory) { if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
                    else File.Delete(path);
                }
                if (printPaths)
                    io.Out.WriteLine(display);
            }
        }
        return status;

        static string Next(string[] args, ref int index)
            => index + 1 < args.Length ? args[++index] : throw new ShellUsageException($"{args[index]}: missing argument", 2);
    }

    private static long? ParseFindSize(string spec)
    {
        if (spec.Length == 0) return null;
        char sign = spec[0];
        string body = sign is '+' or '-' ? spec[1..] : spec;
        char unit = body.Length > 0 && !char.IsDigit(body[^1]) ? body[^1] : 'c';
        string digits = char.IsDigit(body[^1]) ? body : body[..^1];
        if (!long.TryParse(digits, out long value)) return null;
        long multiplier = unit switch { 'c' => 1, 'k' => 1024, 'M' => 1024 * 1024, 'G' => 1024L * 1024 * 1024, 'b' => 512, _ => 1 };
        return value * multiplier;
    }

    private static void Walk(ShellExec exec, string path, string display, int depth, int maxDepth,
        List<(string Display, string Path, bool IsDirectory)> into)
    {
        exec.CheckCancel();
        bool isDirectory = Directory.Exists(path);
        into.Add((display, path, isDirectory));
        if (!isDirectory || depth >= maxDepth)
            return;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(path); }
        catch (UnauthorizedAccessException) { return; }
        foreach (string entry in entries.OrderBy(e => e, StringComparer.Ordinal))
        {
            // A symlink out of the session is not descended: the walk stays inside.
            if (!exec.Paths.IsAllowed(ConfinedPaths.RealPath(entry), PathAccess.Read))
                continue;
            Walk(exec, entry, display.TrimEnd('/') + "/" + Path.GetFileName(entry), depth + 1, maxDepth, into);
        }
    }

    private static int Du(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool summarize = opts.Has("s", "summarize");
        bool human = opts.Has("h", "human-readable");
        List<string> operands = opts.Operands.Count > 0 ? opts.Operands : new List<string> { "." };
        int status = 0;
        foreach (string operand in operands)
        {
            string path;
            try { path = exec.ResolveRead(operand); }
            catch (ConfinementException ex) { io.Error("du", ex.Message); status = 1; continue; }
            if (!Directory.Exists(path) && !File.Exists(path)) { io.Error("du", $"{operand}: No such file or directory"); status = 1; continue; }
            long total = Size(exec, path, operand, io, human, summarize);
            io.Out.WriteLine($"{Format(total, human)}\t{operand}");
        }
        return status;

        static string Format(long bytes, bool human)
            => human ? ShellText.HumanSize(bytes) : ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

        static long Size(ShellExec exec, string path, string display, ShellStreams io, bool human, bool summarize)
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            long total = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                exec.CheckCancel();
                long size = Size(exec, entry, display.TrimEnd('/') + "/" + Path.GetFileName(entry), io, human, summarize);
                if (!summarize && Directory.Exists(entry))
                    io.Out.WriteLine($"{Format(size, human)}\t{display.TrimEnd('/')}/{Path.GetFileName(entry)}");
                total += size;
            }
            return total;
        }
    }

    private static int Df(ShellExec exec, string[] argv, ShellStreams io)
    {
        // There is one volume and the app cannot see the device's real free space
        // without an API the shell has no business calling; report the workspace.
        var opts = new Opts(argv);
        bool human = opts.Has("h", "human-readable");
        var drive = new DriveInfo(Path.GetPathRoot(exec.Policy.WorkRoot) ?? "/");
        long total = 0, free = 0;
        try { total = drive.TotalSize; free = drive.AvailableFreeSpace; } catch (IOException) { }
        long used = total - free;
        io.Out.WriteLine("Filesystem      Size   Used  Avail Capacity Mounted on");
        string size = human ? ShellText.HumanSize(total) : (total / 1024).ToString(CultureInfo.InvariantCulture);
        string usedText = human ? ShellText.HumanSize(used) : (used / 1024).ToString(CultureInfo.InvariantCulture);
        string avail = human ? ShellText.HumanSize(free) : (free / 1024).ToString(CultureInfo.InvariantCulture);
        int percent = total > 0 ? (int)(used * 100 / total) : 0;
        io.Out.WriteLine($"app-container {size,6} {usedText,6} {avail,6} {percent,7}% {exec.Policy.WorkRoot}");
        return 0;
    }

    private static int Chmod(ShellExec exec, string[] argv, ShellStreams io)
    {
        // Modes are meaningless inside a single-user app container, but a script that
        // chmods a file it just wrote must not fail: the paths are still checked.
        var opts = new Opts(argv, stopAtFirstOperand: true);
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: chmod mode file...", 2);
        int status = 0;
        for (int i = 1; i < opts.Operands.Count; i++)
        {
            try
            {
                string path = exec.ResolveWrite(opts.Operands[i]);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    io.Error("chmod", $"{opts.Operands[i]}: No such file or directory");
                    status = 1;
                    continue;
                }
                if (opts.Operands[0].Contains('x') || opts.Operands[0].StartsWith('7') || opts.Operands[0].Contains("755", StringComparison.Ordinal))
                {
                    UnixFileMode mode = File.GetUnixFileMode(path);
                    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
                }
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                io.Error("chmod", ex.Message);
                status = 1;
            }
        }
        return status;
    }

    private static int MkTemp(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "p");
        bool directory = opts.Has("d");
        string template = opts.Operands.Count > 0 ? opts.Operands[0] : "tmp.XXXXXX";
        string directoryPart = opts.Value("p") ?? exec.Policy.TempRoot;
        string name = Path.GetFileName(template).Replace("XXXXXX", Guid.NewGuid().ToString("N")[..6], StringComparison.Ordinal);
        string path = exec.ResolveWrite(Path.Combine(directoryPart, name));
        if (directory)
            Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
        io.Out.WriteLine(path);
        return 0;
    }
}
