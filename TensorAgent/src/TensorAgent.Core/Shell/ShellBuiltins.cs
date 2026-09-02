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

/// <summary>
/// Every command the in-process shell answers itself.
///
/// <para>
/// The list is not "a shell's builtins" — there is no <c>/usr/bin</c> behind it, so a
/// utility that is missing here simply does not exist on this host. It is therefore
/// exactly the set the code-execution tool declaration teaches a model to use
/// (<c>ls</c>, <c>cat</c>, <c>grep</c>, <c>sed</c>, <c>head</c>, <c>mkdir -p</c>,
/// <c>python3</c>, …) plus what those need to be useful, and
/// <see cref="Names"/> is what the declaration's "on this host" line reports.
/// </para>
/// <para>
/// Each builtin takes the argument vector and the three streams, returns an exit
/// status, and reaches the filesystem only through <see cref="ShellExec.ResolveRead"/>
/// / <see cref="ShellExec.ResolveWrite"/>, so confinement cannot be forgotten in one
/// utility. A usage error throws <see cref="ShellUsageException"/> and is reported as
/// <c>name: message</c>, the way a real utility writes to stderr.
/// </para>
/// </summary>
internal static partial class ShellBuiltins
{
    public static IReadOnlyDictionary<string, ShellBuiltin> Table { get; } = Build();

    public static IReadOnlyCollection<string> Names { get; } = Table.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, ShellBuiltin> Build()
    {
        var t = new Dictionary<string, ShellBuiltin>(StringComparer.Ordinal);

        // --- shell ---------------------------------------------------------------
        t[":"] = static (_, _, _) => 0;
        t["true"] = static (_, _, _) => 0;
        t["false"] = static (_, _, _) => 1;
        t["echo"] = Echo;
        t["printf"] = Printf;
        t["cd"] = Cd;
        t["pwd"] = Pwd;
        t["export"] = Export;
        t["unset"] = Unset;
        t["set"] = Set;
        t["shift"] = Shift;
        t["exit"] = Exit;
        t["return"] = Return;
        t["break"] = Break;
        t["continue"] = Continue;
        t["local"] = Local;
        t["read"] = Read;
        t["eval"] = Eval;
        t["source"] = Source;
        t["."] = Source;
        t["test"] = Test;
        t["["] = Test;

        // --- version control ------------------------------------------------------
        // Pure C# against the on-disk format; see ShellBuiltins.Git.cs for why, and for
        // the list of subcommands that are refused rather than approximated.
        t["git"] = GitCommand;
        t["type"] = Type;
        t["command"] = CommandBuiltin;
        t["which"] = Which;
        t["env"] = Env;
        t["printenv"] = PrintEnv;
        t["let"] = Let;
        t["expr"] = Expr;
        t["sleep"] = Sleep;
        t["seq"] = Seq;
        t["date"] = Date;
        t["yes"] = Yes;
        t["true:"] = static (_, _, _) => 0;
        t["alias"] = static (_, _, _) => 0;          // no alias table; accepting it keeps scripts running
        t["hash"] = static (_, _, _) => 0;
        t["umask"] = static (_, _, _) => 0;
        t["trap"] = static (_, _, _) => 0;           // nothing to signal in-process
        t["wait"] = static (_, _, _) => 0;
        t["times"] = static (_, _, _) => 0;
        t["exec"] = Exec;
        t["getopts"] = GetOpts;

        // --- files ---------------------------------------------------------------
        t["ls"] = Ls;
        t["cat"] = Cat;
        t["mkdir"] = Mkdir;
        t["rmdir"] = Rmdir;
        t["rm"] = Rm;
        t["cp"] = Cp;
        t["mv"] = Mv;
        t["ln"] = Ln;
        t["touch"] = Touch;
        t["stat"] = Stat;
        t["readlink"] = ReadLink;
        t["basename"] = Basename;
        t["dirname"] = Dirname;
        t["realpath"] = RealPath;
        t["find"] = Find;
        t["du"] = Du;
        t["df"] = Df;
        t["chmod"] = Chmod;
        t["mktemp"] = MkTemp;

        // --- text ----------------------------------------------------------------
        t["head"] = Head;
        t["tail"] = Tail;
        t["wc"] = Wc;
        t["grep"] = Grep;
        t["egrep"] = Grep;
        t["fgrep"] = Grep;
        t["rg"] = Grep;                               // ripgrep's common flags overlap; -rn is the same request
        t["sed"] = Sed;
        t["awk"] = Awk;
        t["sort"] = Sort;
        t["uniq"] = Uniq;
        t["cut"] = Cut;
        t["tr"] = Tr;
        t["rev"] = Rev;
        t["nl"] = Nl;
        t["paste"] = Paste;
        t["tee"] = Tee;
        t["xargs"] = Xargs;
        t["diff"] = Diff;
        t["base64"] = Base64;
        t["md5"] = Digest;
        t["md5sum"] = Digest;
        t["shasum"] = Digest;
        t["sha1sum"] = Digest;
        t["sha256sum"] = Digest;
        t["cksum"] = Digest;

        // --- archives -------------------------------------------------------------
        t["unzip"] = Unzip;
        t["zip"] = Zip;
        t["tar"] = Tar;
        t["gzip"] = Gzip;
        t["gunzip"] = Gunzip;

        // --- interpreters and the network ----------------------------------------
        t["python"] = Python;
        t["python3"] = Python;
        foreach (string minor in new[] { "3.9", "3.10", "3.11", "3.12", "3.13", "3.14" })
            t["python" + minor] = Python;
        t["pip"] = Pip;
        t["pip3"] = Pip;
        t["node"] = Node;
        t["nodejs"] = Node;
        t["sh"] = Sh;
        t["bash"] = Sh;
        t["curl"] = Curl;
        t["wget"] = Wget;

        return t;
    }

    // =====================================================================================
    // helpers
    // =====================================================================================

    /// <summary>Splits an argument vector into flags and operands, clustering short options
    /// (<c>-rn</c> = <c>-r -n</c>) and stopping at <c>--</c>. Options listed in
    /// <paramref name="valueOptions"/> take the next argument (or the rest of the cluster).</summary>
    internal sealed class Opts
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public List<string> Operands { get; } = new();

        public Opts(IReadOnlyList<string> argv, string valueOptions = "", bool stopAtFirstOperand = false, params string[] longValueOptions)
        {
            var longValues = new HashSet<string>(longValueOptions, StringComparer.Ordinal);
            bool noMore = false;
            for (int i = 1; i < argv.Count; i++)
            {
                string arg = argv[i];
                if (noMore || arg.Length == 0 || arg == "-" || arg[0] != '-')
                {
                    Operands.Add(arg);
                    if (stopAtFirstOperand)
                    {
                        for (int j = i + 1; j < argv.Count; j++)
                            Operands.Add(argv[j]);
                        return;
                    }
                    continue;
                }
                if (arg == "--")
                {
                    noMore = true;
                    continue;
                }
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = arg[2..];
                    int eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        Add(name[..eq], name[(eq + 1)..]);
                    }
                    else if (longValues.Contains(name) && i + 1 < argv.Count)
                    {
                        Add(name, argv[++i]);
                    }
                    else
                    {
                        _flags.Add(name);
                    }
                    continue;
                }
                for (int c = 1; c < arg.Length; c++)
                {
                    string name = arg[c].ToString();
                    if (valueOptions.Contains(arg[c]))
                    {
                        string value = c + 1 < arg.Length ? arg[(c + 1)..]
                            : i + 1 < argv.Count ? argv[++i]
                            : throw new ShellUsageException($"option requires an argument -- {name}", 2);
                        Add(name, value);
                        break;
                    }
                    _flags.Add(name);
                }
            }
        }

        private void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out List<string>? list))
                _values[name] = list = new List<string>();
            list.Add(value);
            _flags.Add(name);
        }

        public bool Has(params string[] names) => names.Any(_flags.Contains);

        public string? Value(params string[] names)
        {
            foreach (string name in names)
                if (_values.TryGetValue(name, out List<string>? list) && list.Count > 0)
                    return list[^1];
            return null;
        }

        public IReadOnlyList<string> Values(params string[] names)
        {
            var all = new List<string>();
            foreach (string name in names)
                if (_values.TryGetValue(name, out List<string>? list))
                    all.AddRange(list);
            return all;
        }

        public int Int(string[] names, int fallback)
        {
            string? text = Value(names);
            return text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }
    }

    /// <summary>One input to a text utility: a named file or standard input.</summary>
    internal readonly record struct TextInput(string Name, string Text);

    /// <summary>Reads every operand as a file, or standard input when there is none.</summary>
    private static List<TextInput> ReadInputs(ShellExec exec, ShellStreams io, IReadOnlyList<string> operands)
    {
        var inputs = new List<TextInput>();
        if (operands.Count == 0)
        {
            inputs.Add(new TextInput("-", ShellText.ReadAllText(io.In)));
            return inputs;
        }
        foreach (string operand in operands)
        {
            exec.CheckCancel();
            if (operand == "-")
            {
                inputs.Add(new TextInput("-", ShellText.ReadAllText(io.In)));
                continue;
            }
            string path = exec.ResolveRead(operand);
            if (Directory.Exists(path))
                throw new ShellUsageException($"{operand}: Is a directory");
            if (!File.Exists(path))
                throw new ShellUsageException($"{operand}: No such file or directory");
            inputs.Add(new TextInput(operand, File.ReadAllText(path, ShellText.Utf8)));
        }
        return inputs;
    }

    /// <summary>Every line of every input, joined; the shape most filters want.</summary>
    private static List<string> ReadLines(ShellExec exec, ShellStreams io, IReadOnlyList<string> operands)
    {
        var lines = new List<string>();
        foreach (TextInput input in ReadInputs(exec, io, operands))
            lines.AddRange(ShellText.Lines(input.Text));
        return lines;
    }

    private static void WriteLines(ShellStreams io, IEnumerable<string> lines)
    {
        foreach (string line in lines)
            io.Out.WriteLine(line);
    }

    // =====================================================================================
    // shell builtins
    // =====================================================================================

    private static int Echo(ShellExec exec, string[] argv, ShellStreams io)
    {
        bool newline = true, escapes = false;
        int i = 1;
        for (; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg == "-n") { newline = false; continue; }
            if (arg == "-e") { escapes = true; continue; }
            if (arg == "-E") { escapes = false; continue; }
            if (arg.Length > 1 && arg[0] == '-' && arg.Skip(1).All(c => c is 'n' or 'e' or 'E'))
            {
                foreach (char c in arg[1..])
                {
                    if (c == 'n') newline = false;
                    else if (c == 'e') escapes = true;
                    else escapes = false;
                }
                continue;
            }
            break;
        }
        string text = string.Join(' ', argv.Skip(i));
        if (escapes)
            text = Unescape(text, out bool suppress);
        io.Out.Write(newline ? text + "\n" : text);
        return 0;
    }

    /// <summary>C escapes for <c>echo -e</c> and <c>printf</c>.</summary>
    private static string Unescape(string text, out bool suppressNewline)
    {
        suppressNewline = false;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }
            char c = text[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0':
                {
                    int value = 0, digits = 0;
                    while (digits < 3 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7')
                    {
                        value = value * 8 + (text[++i] - '0');
                        digits++;
                    }
                    sb.Append((char)value);
                    break;
                }
                case 'x':
                {
                    int value = 0, digits = 0;
                    while (digits < 2 && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1]))
                    {
                        value = value * 16 + Convert.ToInt32(text[++i].ToString(), 16);
                        digits++;
                    }
                    sb.Append((char)value);
                    break;
                }
                case 'c': suppressNewline = true; return sb.ToString();
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static int Printf(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length < 2)
            throw new ShellUsageException("usage: printf format [arguments]", 2);
        string format = argv[1];
        var args = new Queue<string>(argv.Skip(2));
        var sb = new StringBuilder();
        bool consumed;
        do
        {
            consumed = false;
            int before = args.Count;
            FormatOnce(format, args, sb, ref consumed);
            // A format with conversions repeats until the arguments run out.
            if (args.Count == 0 || args.Count == before || !consumed)
                break;
        } while (true);
        io.Out.Write(sb.ToString());
        return 0;
    }

    private static void FormatOnce(string format, Queue<string> args, StringBuilder sb, ref bool consumed)
    {
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                string piece = Unescape(format.Substring(i, 2), out _);
                sb.Append(piece);
                i++;
                continue;
            }
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }

            int start = i++;
            while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0) i++;
            while (i < format.Length && (char.IsDigit(format[i]) || format[i] == '*')) i++;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                while (i < format.Length && (char.IsDigit(format[i]) || format[i] == '*')) i++;
            }
            if (i >= format.Length)
            {
                sb.Append(format[start..]);
                return;
            }
            char conversion = format[i];
            string spec = format[start..i];
            string next = args.Count > 0 ? args.Dequeue() : string.Empty;
            consumed = true;

            string rendered = conversion switch
            {
                's' => next,
                'd' or 'i' => ParseLong(next).ToString(CultureInfo.InvariantCulture),
                'u' => ((ulong)ParseLong(next)).ToString(CultureInfo.InvariantCulture),
                'x' => ParseLong(next).ToString("x", CultureInfo.InvariantCulture),
                'X' => ParseLong(next).ToString("X", CultureInfo.InvariantCulture),
                'o' => Convert.ToString(ParseLong(next), 8),
                'c' => next.Length > 0 ? next[0].ToString() : string.Empty,
                'f' or 'F' => ParseDouble(next).ToString("F" + Precision(spec, 6), CultureInfo.InvariantCulture),
                'e' or 'E' => ParseDouble(next).ToString((conversion == 'e' ? "e" : "E") + Precision(spec, 6), CultureInfo.InvariantCulture),
                'g' or 'G' => ParseDouble(next).ToString("G", CultureInfo.InvariantCulture),
                'b' => Unescape(next, out _),
                _ => next,
            };
            sb.Append(Pad(rendered, spec, conversion));
        }
    }

    private static int Precision(string spec, int fallback)
    {
        int dot = spec.IndexOf('.');
        if (dot < 0) return fallback;
        string digits = new(spec[(dot + 1)..].TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : 0;
    }

    private static string Pad(string text, string spec, char conversion)
    {
        bool left = spec.Contains('-');
        bool zero = spec.Contains('0') && spec.IndexOf('0') < spec.Length && !left
                    && spec.TrimStart('%', '-', '+', ' ', '#').StartsWith('0');
        string widthDigits = new(spec.TrimStart('%', '-', '+', ' ', '#', '0').TakeWhile(char.IsDigit).ToArray());
        if (widthDigits.Length == 0)
            return text;
        int width = int.Parse(widthDigits, CultureInfo.InvariantCulture);
        if (text.Length >= width)
            return text;
        char padding = zero && conversion is 'd' or 'i' or 'x' or 'X' or 'o' or 'f' or 'F' or 'u' ? '0' : ' ';
        return left ? text.PadRight(width) : text.PadLeft(width, padding);
    }

    private static long ParseLong(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
            return hex;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }

    private static double ParseDouble(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    private static int Cd(ShellExec exec, string[] argv, ShellStreams io)
    {
        string target = argv.Length > 1 ? argv[1] : exec.State.Get("HOME");
        if (target.Length == 0)
            target = exec.Policy.WorkRoot;
        if (target == "-")
        {
            target = exec.State.Get("OLDPWD");
            if (target.Length == 0)
                throw new ShellUsageException("OLDPWD not set");
            io.Out.WriteLine(target);
        }
        string resolved = exec.ResolveRead(target);
        if (!Directory.Exists(resolved))
            throw new ShellUsageException($"{target}: No such file or directory");
        exec.State.Set("OLDPWD", exec.State.Cwd);
        exec.State.Cwd = resolved;
        exec.State.Set("PWD", resolved);
        return 0;
    }

    private static int Pwd(ShellExec exec, string[] argv, ShellStreams io)
    {
        io.Out.WriteLine(exec.State.Cwd);
        return 0;
    }

    private static int Export(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length == 1)
        {
            foreach (string name in exec.State.Exported.OrderBy(n => n, StringComparer.Ordinal))
                io.Out.WriteLine($"export {name}=\"{exec.State.Get(name)}\"");
            return 0;
        }
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg is "-p") continue;
            if (arg is "-n")
            {
                for (int j = i + 1; j < argv.Length; j++)
                    exec.State.Exported.Remove(argv[j]);
                return 0;
            }
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                string name = arg[..eq];
                exec.State.Set(name, arg[(eq + 1)..]);
                exec.State.Exported.Add(name);
            }
            else
            {
                exec.State.Exported.Add(arg);
            }
        }
        return 0;
    }

    private static int Unset(ShellExec exec, string[] argv, ShellStreams io)
    {
        for (int i = 1; i < argv.Length; i++)
        {
            string name = argv[i];
            if (name is "-v" or "-f") continue;
            exec.State.Unset(name);
            exec.State.Functions.Remove(name);
        }
        return 0;
    }

    private static int Set(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length == 1)
        {
            foreach (KeyValuePair<string, string> entry in exec.State.Vars.OrderBy(e => e.Key, StringComparer.Ordinal))
                io.Out.WriteLine($"{entry.Key}={entry.Value}");
            return 0;
        }

        var positional = new List<string>();
        bool sawPositional = false;
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (sawPositional)
            {
                positional.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                sawPositional = true;
                continue;
            }
            if (arg.Length > 1 && (arg[0] == '-' || arg[0] == '+'))
            {
                bool on = arg[0] == '-';
                if (arg[1] == 'o')
                {
                    string? name = i + 1 < argv.Length ? argv[++i] : null;
                    if (name is null)
                    {
                        io.Out.WriteLine($"errexit\t{(exec.State.ErrExit ? "on" : "off")}");
                        io.Out.WriteLine($"pipefail\t{(exec.State.PipeFail ? "on" : "off")}");
                        io.Out.WriteLine($"nounset\t{(exec.State.NoUnset ? "on" : "off")}");
                        io.Out.WriteLine($"xtrace\t{(exec.State.XTrace ? "on" : "off")}");
                        io.Out.WriteLine($"noglob\t{(exec.State.NoGlob ? "on" : "off")}");
                        continue;
                    }
                    switch (name)
                    {
                        case "errexit": exec.State.ErrExit = on; break;
                        case "pipefail": exec.State.PipeFail = on; break;
                        case "nounset": exec.State.NoUnset = on; break;
                        case "xtrace": exec.State.XTrace = on; break;
                        case "noglob": exec.State.NoGlob = on; break;
                        default: throw new ShellUsageException($"set: {name}: invalid option name", 2);
                    }
                    continue;
                }
                foreach (char c in arg[1..])
                {
                    switch (c)
                    {
                        case 'e': exec.State.ErrExit = on; break;
                        case 'x': exec.State.XTrace = on; break;
                        case 'u': exec.State.NoUnset = on; break;
                        case 'f': exec.State.NoGlob = on; break;
                        case 'o': break;
                        default: throw new ShellUsageException($"set: -{c}: invalid option", 2);
                    }
                }
                continue;
            }
            sawPositional = true;
            positional.Add(arg);
        }
        if (sawPositional)
            exec.State.Positional = positional;
        return 0;
    }

    private static int Shift(ShellExec exec, string[] argv, ShellStreams io)
    {
        int count = argv.Length > 1 && int.TryParse(argv[1], out int n) ? n : 1;
        if (count < 0 || count > exec.State.Positional.Count)
            return 1;
        exec.State.Positional.RemoveRange(0, count);
        return 0;
    }

    private static int Exit(ShellExec exec, string[] argv, ShellStreams io)
        => throw new ShellExitException(argv.Length > 1 && int.TryParse(argv[1], out int code) ? code & 0xFF : exec.State.LastStatus);

    private static int Return(ShellExec exec, string[] argv, ShellStreams io)
        => throw new ShellReturnException(argv.Length > 1 && int.TryParse(argv[1], out int code) ? code & 0xFF : exec.State.LastStatus);

    private static int Break(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (!exec.InLoop)
            return 0;
        throw new ShellBreakException(argv.Length > 1 && int.TryParse(argv[1], out int n) && n > 0 ? n : 1);
    }

    private static int Continue(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (!exec.InLoop)
            return 0;
        throw new ShellContinueException(argv.Length > 1 && int.TryParse(argv[1], out int n) && n > 0 ? n : 1);
    }

    private static int Local(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (exec.State.LocalScopes.Count == 0)
            throw new ShellUsageException("can only be used in a function");
        Dictionary<string, (bool Existed, string? Value)> scope = exec.State.LocalScopes.Peek();
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            int eq = arg.IndexOf('=');
            string name = eq > 0 ? arg[..eq] : arg;
            if (!scope.ContainsKey(name))
                scope[name] = (exec.State.Vars.TryGetValue(name, out string? existing), existing);
            exec.State.Vars[name] = eq > 0 ? arg[(eq + 1)..] : string.Empty;
        }
        return 0;
    }

    private static int Read(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "d");
        bool raw = opts.Has("r");
        string? line = ShellText.ReadLine(io.In);
        if (line is null)
            return 1;
        if (!raw)
            line = line.Replace("\\", string.Empty, StringComparison.Ordinal);

        List<string> names = opts.Operands.Count > 0 ? opts.Operands : new List<string> { "REPLY" };
        string ifs = exec.State.Get("IFS");
        char[] separators = ifs.Length > 0 ? ifs.ToCharArray() : new[] { ' ', '\t', '\n' };
        string[] fields = line.Split(separators, names.Count, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < names.Count; i++)
            exec.State.Set(names[i], i < fields.Length ? fields[i].Trim() : string.Empty);
        return 0;
    }

    private static int Eval(ShellExec exec, string[] argv, ShellStreams io)
    {
        string text = string.Join(' ', argv.Skip(1));
        return text.Length == 0 ? 0 : exec.RunText(text, io);
    }

    private static int Source(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length < 2)
            throw new ShellUsageException("filename argument required", 2);
        string path = exec.ResolveRead(argv[1]);
        if (!File.Exists(path))
            throw new ShellUsageException($"{argv[1]}: No such file or directory");
        string text = File.ReadAllText(path, ShellText.Utf8);

        List<string> savedPositional = exec.State.Positional;
        if (argv.Length > 2)
            exec.State.Positional = argv.Skip(2).ToList();
        try
        {
            return exec.RunText(text, io);
        }
        finally
        {
            exec.State.Positional = savedPositional;
        }
    }

    private static int Test(ShellExec exec, string[] argv, ShellStreams io)
    {
        List<string> args = argv.Skip(1).ToList();
        if (argv[0] == "[")
        {
            if (args.Count == 0 || args[^1] != "]")
                throw new ShellUsageException("missing ']'", 2);
            args.RemoveAt(args.Count - 1);
        }
        return ShellTest.EvaluateTest(exec, args) ? 0 : 1;
    }

    private static int Type(ShellExec exec, string[] argv, ShellStreams io)
    {
        int status = 0;
        for (int i = 1; i < argv.Length; i++)
        {
            string name = argv[i];
            if (name.StartsWith('-')) continue;
            if (exec.State.Functions.ContainsKey(name))
                io.Out.WriteLine($"{name} is a function");
            else if (Table.ContainsKey(name))
                io.Out.WriteLine($"{name} is a shell builtin");
            else
            {
                io.Error("type", $"{name}: not found");
                status = 1;
            }
        }
        return status;
    }

    private static int CommandBuiltin(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, stopAtFirstOperand: true);
        if (opts.Has("v", "V"))
        {
            int status = 0;
            foreach (string name in opts.Operands)
            {
                if (exec.State.Functions.ContainsKey(name) || Table.ContainsKey(name))
                    io.Out.WriteLine(opts.Has("V") ? $"{name} is a shell builtin" : name);
                else
                    status = 1;
            }
            return status;
        }
        if (opts.Operands.Count == 0)
            return 0;
        return exec.RunArgv(opts.Operands, io);
    }

    private static int Which(ShellExec exec, string[] argv, ShellStreams io)
    {
        int status = 0;
        for (int i = 1; i < argv.Length; i++)
        {
            string name = argv[i];
            if (name.StartsWith('-')) continue;
            if (Table.ContainsKey(name) || exec.State.Functions.ContainsKey(name))
                io.Out.WriteLine("/usr/bin/" + name);
            else
                status = 1;
        }
        return status;
    }

    private static int Env(ShellExec exec, string[] argv, ShellStreams io)
    {
        var assignments = new List<(string Name, string Value)>();
        int i = 1;
        bool ignore = false;
        for (; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg is "-i" or "--ignore-environment") { ignore = true; continue; }
            if (arg == "-") { ignore = true; continue; }
            int eq = arg.IndexOf('=');
            if (eq > 0 && !arg.StartsWith('-'))
            {
                assignments.Add((arg[..eq], arg[(eq + 1)..]));
                continue;
            }
            break;
        }

        if (i >= argv.Length)
        {
            Dictionary<string, string> env = ignore ? new(StringComparer.Ordinal) : exec.State.ExportedEnvironment();
            foreach ((string name, string value) in assignments)
                env[name] = value;
            foreach (KeyValuePair<string, string> entry in env.OrderBy(e => e.Key, StringComparer.Ordinal))
                io.Out.WriteLine($"{entry.Key}={entry.Value}");
            return 0;
        }

        var saved = new List<(string Name, bool Existed, string? Value)>();
        foreach ((string name, string value) in assignments)
        {
            saved.Add((name, exec.State.Vars.TryGetValue(name, out string? old), old));
            exec.State.Set(name, value);
            exec.State.Exported.Add(name);
        }
        try
        {
            return exec.RunArgv(argv.Skip(i).ToList(), io);
        }
        finally
        {
            foreach ((string name, bool existed, string? value) in saved)
            {
                if (existed) exec.State.Vars[name] = value!;
                else exec.State.Unset(name);
            }
        }
    }

    private static int PrintEnv(ShellExec exec, string[] argv, ShellStreams io)
    {
        Dictionary<string, string> env = exec.State.ExportedEnvironment();
        if (argv.Length > 1)
        {
            int status = 0;
            for (int i = 1; i < argv.Length; i++)
            {
                if (env.TryGetValue(argv[i], out string? value))
                    io.Out.WriteLine(value);
                else
                    status = 1;
            }
            return status;
        }
        foreach (KeyValuePair<string, string> entry in env.OrderBy(e => e.Key, StringComparer.Ordinal))
            io.Out.WriteLine($"{entry.Key}={entry.Value}");
        return 0;
    }

    private static int Let(ShellExec exec, string[] argv, ShellStreams io)
    {
        long last = 0;
        for (int i = 1; i < argv.Length; i++)
            last = exec.Arithmetic(argv[i]);
        return last != 0 ? 0 : 1;
    }

    private static int Expr(ShellExec exec, string[] argv, ShellStreams io)
    {
        // expr's own grammar is a subset of the arithmetic evaluator's, with the
        // operators arriving as separate words. Two string forms have no arithmetic
        // equivalent and are answered directly.
        List<string> args = argv.Skip(1).ToList();
        if (args.Count == 3 && args[1] == ":")
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                args[0], "^(?:" + PosixRegex.Translate(args[2], extended: false) + ")");
            string result = m.Success ? (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Length.ToString(CultureInfo.InvariantCulture)) : "0";
            io.Out.WriteLine(result);
            return result is "0" or "" ? 1 : 0;
        }
        if (args.Count >= 2 && args[0] == "length")
        {
            io.Out.WriteLine(args[1].Length.ToString(CultureInfo.InvariantCulture));
            return args[1].Length == 0 ? 1 : 0;
        }
        long value = exec.Arithmetic(string.Join(' ', args));
        io.Out.WriteLine(value.ToString(CultureInfo.InvariantCulture));
        return value != 0 ? 0 : 1;
    }

    private static int Sleep(ShellExec exec, string[] argv, ShellStreams io)
    {
        double seconds = 0;
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            double scale = 1;
            if (arg.Length > 1)
            {
                switch (arg[^1])
                {
                    case 's': arg = arg[..^1]; break;
                    case 'm': arg = arg[..^1]; scale = 60; break;
                    case 'h': arg = arg[..^1]; scale = 3600; break;
                }
            }
            seconds += ParseDouble(arg) * scale;
        }
        if (seconds <= 0)
            return 0;
        // Cancellation (a timeout) must interrupt the wait, so this is not Thread.Sleep.
        exec.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(seconds, 86400)));
        exec.CheckCancel();
        return 0;
    }

    private static int Seq(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "s");
        string separator = opts.Value("s") ?? "\n";
        List<string> operands = opts.Operands;
        if (operands.Count == 0)
            throw new ShellUsageException("usage: seq [first [incr]] last", 2);

        decimal first = 1, increment = 1, last;
        if (operands.Count == 1)
            last = decimal.Parse(operands[0], CultureInfo.InvariantCulture);
        else if (operands.Count == 2)
        {
            first = decimal.Parse(operands[0], CultureInfo.InvariantCulture);
            last = decimal.Parse(operands[1], CultureInfo.InvariantCulture);
        }
        else
        {
            first = decimal.Parse(operands[0], CultureInfo.InvariantCulture);
            increment = decimal.Parse(operands[1], CultureInfo.InvariantCulture);
            last = decimal.Parse(operands[2], CultureInfo.InvariantCulture);
        }
        if (increment == 0)
            throw new ShellUsageException("zero increment", 2);

        var values = new List<string>();
        for (decimal v = first; increment > 0 ? v <= last : v >= last; v += increment)
        {
            values.Add(v.ToString(v == decimal.Truncate(v) ? "0" : "0.######", CultureInfo.InvariantCulture));
            if (values.Count > 1_000_000)
                throw new ShellUsageException("refusing to generate more than a million values");
        }
        if (values.Count > 0)
            io.Out.Write(string.Join(separator, values) + (separator == "\n" ? "\n" : "\n"));
        return 0;
    }

    private static int Date(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "dr");
        DateTimeOffset now = DateTimeOffset.Now;
        if (opts.Has("u")) now = now.ToUniversalTime();
        string? reference = opts.Value("r");
        if (reference is not null)
            now = new DateTimeOffset(File.GetLastWriteTimeUtc(exec.ResolveRead(reference)), TimeSpan.Zero);

        string? format = opts.Operands.FirstOrDefault(o => o.StartsWith('+'));
        if (format is null)
        {
            io.Out.WriteLine(now.ToString("ddd MMM d HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture));
            return 0;
        }
        io.Out.WriteLine(Strftime(format[1..], now));
        return 0;
    }

    private static string Strftime(string format, DateTimeOffset when)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                sb.Append(format[i]);
                continue;
            }
            char c = format[++i];
            sb.Append(c switch
            {
                'Y' => when.Year.ToString("D4", CultureInfo.InvariantCulture),
                'y' => (when.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                'm' => when.Month.ToString("D2", CultureInfo.InvariantCulture),
                'd' => when.Day.ToString("D2", CultureInfo.InvariantCulture),
                'e' => when.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2),
                'H' => when.Hour.ToString("D2", CultureInfo.InvariantCulture),
                'I' => (when.Hour % 12 == 0 ? 12 : when.Hour % 12).ToString("D2", CultureInfo.InvariantCulture),
                'M' => when.Minute.ToString("D2", CultureInfo.InvariantCulture),
                'S' => when.Second.ToString("D2", CultureInfo.InvariantCulture),
                'N' => (when.Millisecond * 1_000_000).ToString("D9", CultureInfo.InvariantCulture),
                'j' => when.DayOfYear.ToString("D3", CultureInfo.InvariantCulture),
                'p' => when.Hour < 12 ? "AM" : "PM",
                'a' => when.ToString("ddd", CultureInfo.InvariantCulture),
                'A' => when.ToString("dddd", CultureInfo.InvariantCulture),
                'b' or 'h' => when.ToString("MMM", CultureInfo.InvariantCulture),
                'B' => when.ToString("MMMM", CultureInfo.InvariantCulture),
                's' => when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                'F' => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                'T' => when.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                'Z' => when.Offset == TimeSpan.Zero ? "UTC" : when.ToString("zzz", CultureInfo.InvariantCulture),
                'z' => when.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal),
                '%' => "%",
                'n' => "\n",
                't' => "\t",
                _ => "%" + c,
            });
        }
        return sb.ToString();
    }

    private static int Yes(ShellExec exec, string[] argv, ShellStreams io)
    {
        // `yes | head -1` is a real idiom, and the pipe's reader closing is what stops
        // it. Without a process there is no SIGPIPE, so this is bounded instead.
        string text = argv.Length > 1 ? string.Join(' ', argv.Skip(1)) : "y";
        for (int i = 0; i < 10_000; i++)
        {
            exec.CheckCancel();
            io.Out.WriteLine(text);
        }
        return 0;
    }

    private static int Exec(ShellExec exec, string[] argv, ShellStreams io)
    {
        // `exec` without a command applies its redirections, which the caller has
        // already done; with one it replaces the shell, which here means running it
        // and exiting with its status.
        if (argv.Length == 1)
            return 0;
        throw new ShellExitException(exec.RunArgv(argv.Skip(1).ToList(), io));
    }

    private static int GetOpts(ShellExec exec, string[] argv, ShellStreams io)
    {
        if (argv.Length < 3)
            throw new ShellUsageException("usage: getopts optstring name [args]", 2);
        string optstring = argv[1];
        string name = argv[2];
        List<string> args = argv.Length > 3 ? argv.Skip(3).ToList() : exec.State.Positional;

        int index = int.TryParse(exec.State.Get("OPTIND"), out int oi) && oi > 0 ? oi : 1;
        if (index > args.Count)
            return 1;
        string current = args[index - 1];
        if (current.Length < 2 || current[0] != '-' || current == "--")
        {
            exec.State.Set("OPTIND", (index + (current == "--" ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
            return 1;
        }

        char option = current[1];
        int position = optstring.IndexOf(option);
        if (position < 0)
        {
            exec.State.Set(name, "?");
            exec.State.Set("OPTARG", option.ToString());
            exec.State.Set("OPTIND", (index + 1).ToString(CultureInfo.InvariantCulture));
            return 0;
        }
        bool takesValue = position + 1 < optstring.Length && optstring[position + 1] == ':';
        exec.State.Set(name, option.ToString());
        if (takesValue)
        {
            string value = current.Length > 2 ? current[2..] : index < args.Count ? args[index] : string.Empty;
            exec.State.Set("OPTARG", value);
            exec.State.Set("OPTIND", (index + (current.Length > 2 ? 1 : 2)).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            exec.State.Unset("OPTARG");
            exec.State.Set("OPTIND", (index + 1).ToString(CultureInfo.InvariantCulture));
        }
        return 0;
    }
}
