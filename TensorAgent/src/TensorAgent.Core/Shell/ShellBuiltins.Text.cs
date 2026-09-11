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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>The text-processing half of the builtin set.</summary>
internal static partial class ShellBuiltins
{
    /// <summary>
    /// <c>head -20</c> and <c>tail -5</c>: the count written as a bare option, which
    /// every real head and tail still accepts and which models write constantly.
    /// </summary>
    private static string[] ExpandCountShorthand(string[] argv)
    {
        string[]? rewritten = null;
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg.Length < 2 || arg[0] != '-')
                continue;
            bool digits = true;
            for (int c = 1; c < arg.Length && digits; c++)
                digits = char.IsAsciiDigit(arg[c]);
            if (!digits)
                continue;
            rewritten ??= (string[])argv.Clone();
            rewritten[i] = "-n" + arg[1..];
        }
        return rewritten ?? argv;
    }

    private static int Head(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(ExpandCountShorthand(argv), "nc");
        int lines = opts.Int(new[] { "n", "lines" }, 10);
        int? bytes = opts.Value("c", "bytes") is string b ? int.Parse(b, CultureInfo.InvariantCulture) : null;
        bool quiet = opts.Has("q");
        List<TextInput> inputs = ReadInputs(exec, io, opts.Operands);
        bool headers = inputs.Count > 1 && !quiet;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (headers)
                io.Out.WriteLine((i > 0 ? "\n" : string.Empty) + $"==> {inputs[i].Name} <==");
            if (bytes is not null)
            {
                string text = inputs[i].Text;
                io.Out.Write(text[..Math.Min(text.Length, bytes.Value)]);
                continue;
            }
            List<string> all = ShellText.Lines(inputs[i].Text);
            int take = lines >= 0 ? Math.Min(lines, all.Count) : Math.Max(0, all.Count + lines);
            WriteLines(io, all.Take(take));
        }
        return 0;
    }

    private static int Tail(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(ExpandCountShorthand(argv), "nc");
        string? spec = opts.Value("n", "lines");
        bool fromStart = spec is not null && spec.StartsWith('+');
        int lines = spec is null ? 10 : int.Parse(spec.TrimStart('+', '-'), CultureInfo.InvariantCulture);
        int? bytes = opts.Value("c", "bytes") is string b ? int.Parse(b.TrimStart('+', '-'), CultureInfo.InvariantCulture) : null;
        bool quiet = opts.Has("q");
        if (opts.Has("f", "follow"))
            throw new ShellUsageException("-f is not available on this host: there is no background writer to follow");

        List<TextInput> inputs = ReadInputs(exec, io, opts.Operands);
        bool headers = inputs.Count > 1 && !quiet;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (headers)
                io.Out.WriteLine((i > 0 ? "\n" : string.Empty) + $"==> {inputs[i].Name} <==");
            if (bytes is not null)
            {
                string text = inputs[i].Text;
                io.Out.Write(text[Math.Max(0, text.Length - bytes.Value)..]);
                continue;
            }
            List<string> all = ShellText.Lines(inputs[i].Text);
            WriteLines(io, fromStart ? all.Skip(Math.Max(0, lines - 1)) : all.Skip(Math.Max(0, all.Count - lines)));
        }
        return 0;
    }

    private static int Wc(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool wantLines = opts.Has("l", "lines"), wantWords = opts.Has("w", "words");
        bool wantChars = opts.Has("c", "bytes", "m", "chars");
        if (!wantLines && !wantWords && !wantChars)
            wantLines = wantWords = wantChars = true;

        long totalLines = 0, totalWords = 0, totalChars = 0;
        List<TextInput> inputs = ReadInputs(exec, io, opts.Operands);
        foreach (TextInput input in inputs)
        {
            long lines = input.Text.Length == 0 ? 0 : input.Text.Count(c => c == '\n') + (input.Text.EndsWith('\n') ? 0 : 1);
            long words = input.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
            long chars = ShellText.Utf8.GetByteCount(input.Text);
            totalLines += lines; totalWords += words; totalChars += chars;
            io.Out.WriteLine(Row(lines, words, chars, inputs.Count > 1 || opts.Operands.Count > 0 ? input.Name : null));
        }
        if (inputs.Count > 1)
            io.Out.WriteLine(Row(totalLines, totalWords, totalChars, "total"));
        return 0;

        string Row(long lines, long words, long chars, string? name)
        {
            var parts = new List<string>();
            if (wantLines) parts.Add(lines.ToString(CultureInfo.InvariantCulture).PadLeft(7));
            if (wantWords) parts.Add(words.ToString(CultureInfo.InvariantCulture).PadLeft(7));
            if (wantChars) parts.Add(chars.ToString(CultureInfo.InvariantCulture).PadLeft(7));
            string row = string.Join(string.Empty, parts);
            return name is null || name == "-" ? row : row + " " + name;
        }
    }

    private static int Grep(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "efm", false, "include", "exclude", "regexp", "file", "max-count");
        bool recursive = opts.Has("r", "R", "recursive");
        bool ignoreCase = opts.Has("i", "ignore-case");
        bool invert = opts.Has("v", "invert-match");
        bool number = opts.Has("n", "line-number");
        bool countOnly = opts.Has("c", "count");
        bool namesOnly = opts.Has("l", "files-with-matches");
        bool noFilename = opts.Has("h", "no-filename");
        bool onlyMatching = opts.Has("o", "only-matching");
        bool quiet = opts.Has("q", "quiet", "silent");
        bool wholeWord = opts.Has("w", "word-regexp");
        bool wholeLine = opts.Has("x", "line-regexp");
        bool fixedStrings = opts.Has("F", "fixed-strings") || argv[0] == "fgrep";
        bool extended = opts.Has("E", "extended-regexp") || argv[0] is "egrep" or "rg";
        int maxCount = opts.Int(new[] { "m", "max-count" }, int.MaxValue);
        string? include = opts.Value("include");
        string? exclude = opts.Value("exclude");

        var patterns = new List<string>(opts.Values("e", "regexp"));
        foreach (string file in opts.Values("f", "file"))
            patterns.AddRange(ShellText.Lines(File.ReadAllText(exec.ResolveRead(file), ShellText.Utf8)));
        List<string> operands = opts.Operands;
        if (patterns.Count == 0)
        {
            if (operands.Count == 0)
                throw new ShellUsageException("usage: grep [options] pattern [file...]", 2);
            patterns.Add(operands[0]);
            operands = operands.Skip(1).ToList();
        }

        var regexes = new List<Regex>();
        foreach (string pattern in patterns)
        {
            string body = fixedStrings ? Regex.Escape(pattern) : PosixRegex.Translate(pattern, extended);
            if (wholeWord) body = @"\b(?:" + body + @")\b";
            if (wholeLine) body = "^(?:" + body + ")$";
            regexes.Add(new Regex(body, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(5)));
        }

        // Which files: the operands, or every file underneath them with -r, or stdin.
        var sources = new List<TextInput>();
        if (operands.Count == 0)
        {
            sources.Add(new TextInput("(standard input)", ShellText.ReadAllText(io.In)));
        }
        else
        {
            foreach (string operand in operands)
            {
                exec.CheckCancel();
                string path;
                try { path = exec.ResolveRead(operand); }
                catch (ConfinementException ex) { io.Error("grep", ex.Message); continue; }
                if (Directory.Exists(path))
                {
                    if (!recursive) { io.Error("grep", $"{operand}: Is a directory"); continue; }
                    foreach (string file in EnumerateFiles(exec, path))
                    {
                        string name = Path.Combine(operand.TrimEnd('/'), Path.GetRelativePath(path, file));
                        if (include is not null && !GlobMatcher.IsMatch(include, Path.GetFileName(file))) continue;
                        if (exclude is not null && GlobMatcher.IsMatch(exclude, Path.GetFileName(file))) continue;
                        byte[] bytes = File.ReadAllBytes(file);
                        if (ShellText.LooksBinary(bytes)) continue;
                        sources.Add(new TextInput(name, ShellText.Utf8.GetString(bytes)));
                    }
                    continue;
                }
                if (!File.Exists(path)) { io.Error("grep", $"{operand}: No such file or directory"); continue; }
                sources.Add(new TextInput(operand, File.ReadAllText(path, ShellText.Utf8)));
            }
        }

        bool showNames = !noFilename && (sources.Count > 1 || recursive);
        bool any = false;
        foreach (TextInput source in sources)
        {
            exec.CheckCancel();
            int matches = 0;
            List<string> lines = ShellText.Lines(source.Text);
            for (int i = 0; i < lines.Count && matches < maxCount; i++)
            {
                bool hit = regexes.Any(r => r.IsMatch(lines[i]));
                if (hit == invert)
                    continue;
                matches++;
                any = true;
                if (quiet || countOnly || namesOnly)
                    continue;
                string prefix = (showNames ? source.Name + ":" : string.Empty) + (number ? (i + 1).ToString(CultureInfo.InvariantCulture) + ":" : string.Empty);
                if (onlyMatching && !invert)
                {
                    foreach (Regex regex in regexes)
                        foreach (Match m in regex.Matches(lines[i]))
                            io.Out.WriteLine(prefix + m.Value);
                }
                else
                {
                    io.Out.WriteLine(prefix + lines[i]);
                }
            }
            if (countOnly)
                io.Out.WriteLine((showNames ? source.Name + ":" : string.Empty) + matches.ToString(CultureInfo.InvariantCulture));
            if (namesOnly && matches > 0)
                io.Out.WriteLine(source.Name);
            if (quiet && any)
                return 0;
        }
        return any ? 0 : 1;
    }

    private static IEnumerable<string> EnumerateFiles(ShellExec exec, string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            exec.CheckCancel();
            string directory = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string entry in entries.OrderBy(e => e, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(entry);
                if (name is ".git" or "node_modules" or "__pycache__" or ".venv" or "venv" or ".mypy_cache")
                    continue;
                if (!exec.Paths.IsAllowed(ConfinedPaths.RealPath(entry), PathAccess.Read))
                    continue;
                if (Directory.Exists(entry)) stack.Push(entry);
                else yield return entry;
            }
        }
    }

    /// <summary>
    /// Pull <c>-i</c> out of the argument list by hand. It cannot go through the
    /// normal option parser: GNU takes an optional <em>attached</em> suffix and never
    /// eats the next word, so parsing it as a value option swallows the script.
    /// </summary>
    private static string[] SplitInPlace(string[] argv, out bool inPlace, out string? suffix)
    {
        inPlace = false;
        suffix = null;
        var kept = new List<string> { argv.Length > 0 ? argv[0] : "sed" };
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg == "--")
            {
                for (; i < argv.Length; i++)
                    kept.Add(argv[i]);
                break;
            }
            if (arg == "-i" || arg == "--in-place")
            {
                inPlace = true;
                continue;
            }
            if (arg.StartsWith("--in-place=", StringComparison.Ordinal))
            {
                inPlace = true;
                suffix = arg["--in-place=".Length..];
                continue;
            }
            if (arg.Length > 2 && arg[0] == '-' && arg[1] == 'i')
            {
                inPlace = true;
                suffix = arg[2..];
                continue;
            }
            kept.Add(arg);
        }
        return kept.ToArray();
    }

    private static int Sed(ShellExec exec, string[] argv, ShellStreams io)
    {
        string[] rest = SplitInPlace(argv, out bool inPlace, out string? backupSuffix);
        var opts = new Opts(rest, "e", false, "expression");
        bool quiet = opts.Has("n", "quiet", "silent");
        bool extended = opts.Has("E", "r", "regexp-extended");
        inPlace |= opts.Has("i", "in-place");

        var scripts = new List<string>(opts.Values("e", "expression"));
        List<string> operands = opts.Operands;
        // BSD spells the no-backup form `sed -i '' script file`, which leaves an
        // empty string where GNU has nothing at all. Drop it before it is mistaken
        // for the script.
        if (inPlace && operands.Count > 0 && operands[0].Length == 0)
            operands = operands.Skip(1).ToList();
        if (scripts.Count == 0)
        {
            if (operands.Count == 0)
                throw new ShellUsageException("usage: sed [-n] [-i] script [file...]", 2);
            scripts.Add(operands[0]);
            operands = operands.Skip(1).ToList();
        }

        var program = new List<SedCommand>();
        foreach (string script in scripts)
            SedParser.Parse(script, extended, program);

        if (inPlace)
        {
            if (operands.Count == 0)
                throw new ShellUsageException("-i requires a file operand", 2);
            foreach (string operand in operands)
            {
                exec.CheckCancel();
                string path = exec.ResolveWrite(operand);
                if (!File.Exists(path))
                    throw new ShellUsageException($"{operand}: No such file or directory");
                string text = File.ReadAllText(path, ShellText.Utf8);
                var buffer = new StringBuilder();
                RunSed(exec, program, ShellText.Lines(text), quiet, line => buffer.Append(line).Append('\n'));
                if (!string.IsNullOrEmpty(backupSuffix))
                    File.WriteAllText(exec.ResolveWrite(operand + backupSuffix), text, ShellText.Utf8);
                File.WriteAllText(path, buffer.ToString(), ShellText.Utf8);
            }
            return 0;
        }

        foreach (TextInput input in ReadInputs(exec, io, operands))
            RunSed(exec, program, ShellText.Lines(input.Text), quiet, io.Out.WriteLine);
        return 0;
    }

    private static void RunSed(ShellExec exec, List<SedCommand> program, List<string> lines, bool quiet, Action<string> emit)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            exec.CheckCancel();
            string pattern = lines[index];
            bool deleted = false, printed = false;
            foreach (SedCommand command in program)
            {
                if (!command.Matches(index + 1, lines.Count, pattern))
                    continue;
                switch (command.Kind)
                {
                    case SedKind.Substitute:
                        pattern = command.Substitute(pattern);
                        break;
                    case SedKind.Delete:
                        deleted = true;
                        break;
                    case SedKind.Print:
                        emit(pattern);
                        printed = true;
                        break;
                    case SedKind.Append:
                        emit(pattern);
                        emit(command.Text!);
                        printed = true;
                        deleted = true;
                        break;
                    case SedKind.Insert:
                        emit(command.Text!);
                        break;
                    case SedKind.Change:
                        emit(command.Text!);
                        deleted = true;
                        break;
                    case SedKind.Quit:
                        if (!quiet && !deleted) emit(pattern);
                        return;
                }
                if (deleted)
                    break;
            }
            if (!quiet && !deleted)
                emit(pattern);
            else if (quiet && printed)
            {
                // already emitted by the p command
            }
        }
    }

    private enum SedKind { Substitute, Delete, Print, Append, Insert, Change, Quit }

    private sealed class SedCommand
    {
        public SedKind Kind { get; init; }
        public Regex? AddressRegex { get; init; }
        public int AddressStart { get; init; }
        public int AddressEnd { get; init; }
        public bool LastLine { get; init; }
        public bool Negated { get; init; }
        public Regex? Pattern { get; init; }
        public string? Replacement { get; init; }
        public bool Global { get; init; }
        public int Occurrence { get; init; } = 1;
        public string? Text { get; init; }

        public bool Matches(int lineNumber, int total, string line)
        {
            bool matched;
            if (AddressRegex is not null)
                matched = AddressRegex.IsMatch(line);
            else if (LastLine)
                matched = lineNumber == total;
            else if (AddressStart == 0)
                matched = true;
            else if (AddressEnd > 0)
                matched = lineNumber >= AddressStart && lineNumber <= AddressEnd;
            else
                matched = lineNumber == AddressStart;
            return Negated ? !matched : matched;
        }

        public string Substitute(string input)
        {
            if (Pattern is null)
                return input;
            if (Global)
                return Pattern.Replace(input, Replacement ?? string.Empty);
            int seen = 0;
            return Pattern.Replace(input, m => ++seen == Occurrence ? m.Result(Replacement ?? string.Empty) : m.Value);
        }
    }

    private static class SedParser
    {
        public static void Parse(string script, bool extended, List<SedCommand> into)
        {
            foreach (string raw in script.Split('\n', ';'))
            {
                string text = raw.Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                    continue;
                into.Add(ParseOne(text, extended));
            }
        }

        private static SedCommand ParseOne(string text, bool extended)
        {
            int i = 0;
            Regex? addressRegex = null;
            int start = 0, end = 0;
            bool lastLine = false;

            if (text[i] == '/')
            {
                int close = FindDelimiter(text, i + 1, '/');
                string body = text[(i + 1)..close];
                addressRegex = new Regex(PosixRegex.Translate(body, extended), RegexOptions.None, TimeSpan.FromSeconds(5));
                i = close + 1;
            }
            else if (char.IsDigit(text[i]))
            {
                int j = i;
                while (j < text.Length && char.IsDigit(text[j])) j++;
                start = int.Parse(text[i..j], CultureInfo.InvariantCulture);
                i = j;
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    if (i < text.Length && text[i] == '$') { lastLine = false; end = int.MaxValue; i++; }
                    else
                    {
                        j = i;
                        while (j < text.Length && char.IsDigit(text[j])) j++;
                        end = int.Parse(text[i..j], CultureInfo.InvariantCulture);
                        i = j;
                    }
                }
            }
            else if (text[i] == '$')
            {
                lastLine = true;
                i++;
            }

            bool negated = false;
            while (i < text.Length && (text[i] == ' ' || text[i] == '!'))
            {
                if (text[i] == '!') negated = true;
                i++;
            }
            if (i >= text.Length)
                throw new ShellUsageException($"missing command in '{text}'", 2);

            char verb = text[i++];
            switch (verb)
            {
                case 's':
                {
                    if (i >= text.Length)
                        throw new ShellUsageException("unterminated `s' command", 2);
                    char delimiter = text[i++];
                    int patternEnd = FindDelimiter(text, i, delimiter);
                    string pattern = text[i..patternEnd];
                    int replacementEnd = FindDelimiter(text, patternEnd + 1, delimiter);
                    string replacement = text[(patternEnd + 1)..replacementEnd];
                    string flags = replacementEnd + 1 <= text.Length ? text[Math.Min(replacementEnd + 1, text.Length)..] : string.Empty;
                    var options = RegexOptions.None;
                    if (flags.Contains('I') || flags.Contains('i')) options |= RegexOptions.IgnoreCase;
                    int occurrence = 1;
                    string digits = new(flags.Where(char.IsDigit).ToArray());
                    if (digits.Length > 0) occurrence = int.Parse(digits, CultureInfo.InvariantCulture);
                    return new SedCommand
                    {
                        Kind = SedKind.Substitute,
                        AddressRegex = addressRegex,
                        AddressStart = start,
                        AddressEnd = end,
                        LastLine = lastLine,
                        Negated = negated,
                        Pattern = new Regex(PosixRegex.Translate(pattern, extended), options, TimeSpan.FromSeconds(5)),
                        Replacement = PosixRegex.TranslateReplacement(replacement),
                        Global = flags.Contains('g'),
                        Occurrence = occurrence,
                    };
                }
                case 'd':
                    return new SedCommand { Kind = SedKind.Delete, AddressRegex = addressRegex, AddressStart = start, AddressEnd = end, LastLine = lastLine, Negated = negated };
                case 'p':
                    return new SedCommand { Kind = SedKind.Print, AddressRegex = addressRegex, AddressStart = start, AddressEnd = end, LastLine = lastLine, Negated = negated };
                case 'q':
                    return new SedCommand { Kind = SedKind.Quit, AddressRegex = addressRegex, AddressStart = start, AddressEnd = end, LastLine = lastLine, Negated = negated };
                case 'a' or 'i' or 'c':
                {
                    string body = text[i..].TrimStart('\\', ' ');
                    SedKind kind = verb switch { 'a' => SedKind.Append, 'i' => SedKind.Insert, _ => SedKind.Change };
                    return new SedCommand { Kind = kind, Text = body, AddressRegex = addressRegex, AddressStart = start, AddressEnd = end, LastLine = lastLine, Negated = negated };
                }
                default:
                    throw new ShellUsageException($"unknown command: `{verb}'", 2);
            }
        }

        private static int FindDelimiter(string text, int from, char delimiter)
        {
            for (int i = from; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == delimiter) return i;
            }
            throw new ShellUsageException("unterminated expression", 2);
        }
    }

    private static int Awk(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "Fvf", true, "field-separator");
        string separator = opts.Value("F", "field-separator") ?? " ";
        List<string> operands = opts.Operands;
        string? programFile = opts.Value("f");
        string program;
        if (programFile is not null)
        {
            program = File.ReadAllText(exec.ResolveRead(programFile), ShellText.Utf8);
        }
        else
        {
            if (operands.Count == 0)
                throw new ShellUsageException("usage: awk [-F sep] 'program' [file...]", 2);
            program = operands[0];
            operands = operands.Skip(1).ToList();
        }

        var interpreter = new AwkInterpreter(program, separator, io.Out.WriteLine);
        foreach (string assignment in opts.Values("v"))
        {
            int eq = assignment.IndexOf('=');
            if (eq > 0) interpreter.SetVariable(assignment[..eq], assignment[(eq + 1)..]);
        }
        interpreter.Begin();
        foreach (TextInput input in ReadInputs(exec, io, operands))
        {
            foreach (string line in ShellText.Lines(input.Text))
            {
                exec.CheckCancel();
                interpreter.Process(line);
            }
        }
        interpreter.End();
        return 0;
    }

    private static int Sort(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "kt");
        bool numeric = opts.Has("n", "numeric-sort", "g");
        bool reverse = opts.Has("r", "reverse");
        bool unique = opts.Has("u", "unique");
        bool ignoreCase = opts.Has("f", "ignore-case");
        string? keySpec = opts.Value("k", "key");
        string separator = opts.Value("t", "field-separator") ?? "\t ";

        List<string> lines = ReadLines(exec, io, opts.Operands);
        int keyField = 0;
        if (keySpec is not null)
        {
            string digits = new(keySpec.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0) keyField = int.Parse(digits, CultureInfo.InvariantCulture);
            if (keySpec.Contains('n')) numeric = true;
            if (keySpec.Contains('r')) reverse = true;
        }

        string KeyOf(string line)
        {
            if (keyField <= 0) return line;
            string[] fields = line.Split(separator.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            return keyField <= fields.Length ? fields[keyField - 1] : string.Empty;
        }

        IEnumerable<string> ordered = numeric
            ? lines.OrderBy(l => ParseDouble(KeyOf(l)))
            : lines.OrderBy(KeyOf, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        List<string> result = ordered.ToList();
        if (reverse) result.Reverse();
        if (unique)
        {
            var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            result = result.Where(l => seen.Add(KeyOf(l))).ToList();
        }
        WriteLines(io, result);
        return 0;
    }

    private static int Uniq(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "f");
        bool count = opts.Has("c", "count");
        bool onlyDuplicates = opts.Has("d", "repeated");
        bool onlyUnique = opts.Has("u", "unique");
        bool ignoreCase = opts.Has("i", "ignore-case");

        List<string> operands = opts.Operands;
        string? outputFile = operands.Count > 1 ? operands[1] : null;
        List<string> lines = ReadLines(exec, io, operands.Count > 0 ? new List<string> { operands[0] } : operands);

        var output = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            int run = 1;
            while (i + run < lines.Count && string.Equals(lines[i + run], lines[i], ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                run++;
            bool emit = (!onlyDuplicates || run > 1) && (!onlyUnique || run == 1);
            if (emit)
                output.Add(count ? $"{run,7} {lines[i]}" : lines[i]);
            i += run;
        }
        if (outputFile is not null)
            File.WriteAllText(exec.ResolveWrite(outputFile), string.Join('\n', output) + (output.Count > 0 ? "\n" : string.Empty), ShellText.Utf8);
        else
            WriteLines(io, output);
        return 0;
    }

    private static int Cut(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "dfc", false, "delimiter", "fields", "characters");
        string delimiter = opts.Value("d", "delimiter") ?? "\t";
        string? fields = opts.Value("f", "fields");
        string? characters = opts.Value("c", "characters");
        bool onlyDelimited = opts.Has("s", "only-delimited");
        if (fields is null && characters is null)
            throw new ShellUsageException("usage: cut -f list [-d delim] | -c list", 2);

        List<(int From, int To)> ranges = ParseRanges(fields ?? characters!);
        foreach (string line in ReadLines(exec, io, opts.Operands))
        {
            exec.CheckCancel();
            if (characters is not null)
            {
                var sb = new StringBuilder();
                foreach ((int from, int to) in ranges)
                    for (int i = from; i <= Math.Min(to, line.Length); i++)
                        if (i >= 1) sb.Append(line[i - 1]);
                io.Out.WriteLine(sb.ToString());
                continue;
            }
            if (!line.Contains(delimiter, StringComparison.Ordinal))
            {
                if (!onlyDelimited) io.Out.WriteLine(line);
                continue;
            }
            string[] parts = line.Split(delimiter);
            var selected = new List<string>();
            foreach ((int from, int to) in ranges)
                for (int i = from; i <= Math.Min(to, parts.Length); i++)
                    if (i >= 1) selected.Add(parts[i - 1]);
            io.Out.WriteLine(string.Join(delimiter, selected));
        }
        return 0;
    }

    private static List<(int From, int To)> ParseRanges(string spec)
    {
        var ranges = new List<(int, int)>();
        foreach (string part in spec.Split(','))
        {
            string piece = part.Trim();
            if (piece.Length == 0) continue;
            int dash = piece.IndexOf('-');
            if (dash < 0)
            {
                int single = int.Parse(piece, CultureInfo.InvariantCulture);
                ranges.Add((single, single));
            }
            else if (dash == 0)
                ranges.Add((1, int.Parse(piece[1..], CultureInfo.InvariantCulture)));
            else if (dash == piece.Length - 1)
                ranges.Add((int.Parse(piece[..dash], CultureInfo.InvariantCulture), int.MaxValue));
            else
                ranges.Add((int.Parse(piece[..dash], CultureInfo.InvariantCulture), int.Parse(piece[(dash + 1)..], CultureInfo.InvariantCulture)));
        }
        return ranges;
    }

    private static int Tr(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool delete = opts.Has("d", "delete");
        bool squeeze = opts.Has("s", "squeeze-repeats");
        bool complement = opts.Has("c", "C", "complement");
        if (opts.Operands.Count == 0)
            throw new ShellUsageException("usage: tr [-cds] set1 [set2]", 2);

        string set1 = ExpandSet(opts.Operands[0]);
        string set2 = opts.Operands.Count > 1 ? ExpandSet(opts.Operands[1]) : string.Empty;
        string text = ShellText.ReadAllText(io.In);
        var sb = new StringBuilder(text.Length);
        char last = '\0';
        bool lastWasReplaced = false;

        foreach (char c in text)
        {
            bool inSet = complement ? !set1.Contains(c) : set1.Contains(c);
            if (delete && inSet)
                continue;
            char output = c;
            if (!delete && inSet && set2.Length > 0)
            {
                int index = complement ? set2.Length - 1 : set1.IndexOf(c);
                output = index < set2.Length ? set2[index] : set2[^1];
            }
            if (squeeze && inSet && lastWasReplaced && output == last)
                continue;
            sb.Append(output);
            last = output;
            lastWasReplaced = inSet;
        }
        io.Out.Write(sb.ToString());
        return 0;
    }

    /// <summary>`a-z`, `[:upper:]` and the C escapes, as a flat character list.</summary>
    private static string ExpandSet(string spec)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < spec.Length; i++)
        {
            if (spec[i] == '[' && i + 1 < spec.Length && spec[i + 1] == ':')
            {
                int close = spec.IndexOf(":]", i, StringComparison.Ordinal);
                if (close > 0)
                {
                    string name = spec[(i + 2)..close];
                    sb.Append(name switch
                    {
                        "upper" => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                        "lower" => "abcdefghijklmnopqrstuvwxyz",
                        "digit" => "0123456789",
                        "alpha" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
                        "alnum" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
                        "space" => " \t\n\r\f\v",
                        "punct" => "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
                        _ => string.Empty,
                    });
                    i = close + 1;
                    continue;
                }
            }
            if (spec[i] == '\\' && i + 1 < spec.Length)
            {
                sb.Append(Unescape(spec.Substring(i, 2), out _));
                i++;
                continue;
            }
            if (i + 2 < spec.Length && spec[i + 1] == '-' && spec[i + 2] >= spec[i])
            {
                for (char c = spec[i]; c <= spec[i + 2]; c++)
                    sb.Append(c);
                i += 2;
                continue;
            }
            sb.Append(spec[i]);
        }
        return sb.ToString();
    }

    private static int Rev(ShellExec exec, string[] argv, ShellStreams io)
    {
        foreach (string line in ReadLines(exec, io, new Opts(argv).Operands))
            io.Out.WriteLine(new string(line.Reverse().ToArray()));
        return 0;
    }

    private static int Nl(ShellExec exec, string[] argv, ShellStreams io)
    {
        int n = 0;
        foreach (string line in ReadLines(exec, io, new Opts(argv, "bs").Operands))
            io.Out.WriteLine($"{++n,6}\t{line}");
        return 0;
    }

    private static int Paste(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "d");
        string delimiter = opts.Value("d") ?? "\t";
        var columns = new List<List<string>>();
        foreach (TextInput input in ReadInputs(exec, io, opts.Operands))
            columns.Add(ShellText.Lines(input.Text));
        int rows = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
        for (int r = 0; r < rows; r++)
            io.Out.WriteLine(string.Join(delimiter, columns.Select(c => r < c.Count ? c[r] : string.Empty)));
        return 0;
    }

    private static int Tee(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv);
        bool append = opts.Has("a", "append");
        byte[] content = ShellText.ReadAllBytes(io.In);
        foreach (string operand in opts.Operands)
        {
            string path = exec.ResolveWrite(operand);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream stream = append ? new FileStream(path, FileMode.Append, FileAccess.Write) : File.Create(path);
            stream.Write(content);
        }
        io.Out.Write(content);
        return 0;
    }

    private static int Xargs(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "nId", true, "max-args", "replace", "delimiter");
        int batch = opts.Int(new[] { "n", "max-args" }, int.MaxValue);
        string? replace = opts.Value("I", "replace");
        string? delimiter = opts.Value("d", "delimiter");
        List<string> command = opts.Operands.Count > 0 ? opts.Operands : new List<string> { "echo" };

        string input = ShellText.ReadAllText(io.In);
        List<string> items = delimiter is not null
            ? input.Split(Unescape(delimiter, out _), StringSplitOptions.RemoveEmptyEntries).ToList()
            : input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (items.Count == 0 && !opts.Has("r", "no-run-if-empty"))
            items.Add(string.Empty);

        int status = 0;
        if (replace is not null)
        {
            foreach (string item in items)
            {
                exec.CheckCancel();
                List<string> expanded = command.Select(word => word.Replace(replace, item, StringComparison.Ordinal)).ToList();
                status = exec.RunArgv(expanded, io);
                if (status != 0) return status;
            }
            return status;
        }
        for (int i = 0; i < items.Count; i += batch == int.MaxValue ? Math.Max(items.Count, 1) : batch)
        {
            exec.CheckCancel();
            var call = new List<string>(command);
            call.AddRange(items.Skip(i).Take(batch == int.MaxValue ? items.Count : batch));
            status = exec.RunArgv(call, io);
            if (status != 0) return status;
        }
        return status;
    }

    private static int Diff(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "U");
        if (opts.Operands.Count < 2)
            throw new ShellUsageException("usage: diff [-u] file1 file2", 2);
        List<string> a = ShellText.Lines(File.ReadAllText(exec.ResolveRead(opts.Operands[0]), ShellText.Utf8));
        List<string> b = ShellText.Lines(File.ReadAllText(exec.ResolveRead(opts.Operands[1]), ShellText.Utf8));
        bool unified = opts.Has("u", "unified", "U");
        string report = unified
            ? DiffAlgorithm.Unified(a, b, opts.Operands[0], opts.Operands[1], opts.Int(new[] { "U", "unified" }, 3))
            : DiffAlgorithm.Normal(a, b);
        if (report.Length == 0)
            return 0;
        io.Out.Write(report);
        return 1;
    }

    private static int Base64(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "w");
        bool decode = opts.Has("d", "D", "decode");
        byte[] input = opts.Operands.Count > 0
            ? File.ReadAllBytes(exec.ResolveRead(opts.Operands[0]))
            : ShellText.ReadAllBytes(io.In);
        if (decode)
        {
            string text = ShellText.Utf8.GetString(input).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
            try { io.Out.Write(Convert.FromBase64String(text)); }
            catch (FormatException) { throw new ShellUsageException("invalid input"); }
            return 0;
        }
        string encoded = Convert.ToBase64String(input);
        int wrap = opts.Int(new[] { "w", "wrap" }, 0);
        if (wrap > 0)
        {
            for (int i = 0; i < encoded.Length; i += wrap)
                io.Out.WriteLine(encoded.Substring(i, Math.Min(wrap, encoded.Length - i)));
        }
        else
        {
            io.Out.WriteLine(encoded);
        }
        return 0;
    }

    private static int Digest(ShellExec exec, string[] argv, ShellStreams io)
    {
        var opts = new Opts(argv, "a");
        string algorithm = argv[0] switch
        {
            "md5" or "md5sum" => "md5",
            "sha1sum" => "sha1",
            "sha256sum" => "sha256",
            "cksum" => "sha256",
            _ => opts.Value("a", "algorithm") ?? "sha1",
        };

        IEnumerable<(string Name, byte[] Data)> inputs = opts.Operands.Count == 0
            ? new[] { ("-", ShellText.ReadAllBytes(io.In)) }
            : opts.Operands.Select(o => (o, File.ReadAllBytes(exec.ResolveRead(o))));

        foreach ((string name, byte[] data) in inputs)
        {
            byte[] hash = algorithm switch
            {
                "md5" => MD5.HashData(data),
                "1" or "sha1" => SHA1.HashData(data),
                "256" or "sha256" => SHA256.HashData(data),
                "512" or "sha512" => SHA512.HashData(data),
                _ => SHA256.HashData(data),
            };
            string hex = Convert.ToHexStringLower(hash);
            io.Out.WriteLine(name == "-" ? hex + "  -" : $"{hex}  {name}");
        }
        return 0;
    }
}
