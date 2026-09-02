// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.RegularExpressions;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>
/// <c>test</c> / <c>[ ]</c> and the <c>[[ ]]</c> conditional.
///
/// <para>
/// The two differ in one way that matters to a model writing shell: inside
/// <c>[[ ]]</c> the right-hand side of <c>=</c>/<c>==</c>/<c>!=</c> is a glob PATTERN
/// and <c>=~</c> is a regular expression, while <c>test</c> compares strings. The
/// pattern therefore has to be expanded with quoting preserved, which is why the
/// conditional evaluates <see cref="Word"/>s rather than the already-expanded argv.
/// </para>
/// <para>
/// A file test asks the confinement first: a path outside the session's roots answers
/// false rather than leaking whether it exists, so <c>[ -f /etc/passwd ]</c> is false
/// on this host the same way <c>cat</c> refuses it.
/// </para>
/// </summary>
internal static class ShellTest
{
    /// <summary>The <c>[[ ... ]]</c> form, over unexpanded words.</summary>
    public static bool EvaluateConditional(ShellExec exec, List<Word> words)
    {
        var parser = new ConditionalParser(exec, words);
        bool value = parser.ParseOr();
        parser.ExpectEnd();
        return value;
    }

    /// <summary>The <c>test</c> / <c>[</c> builtin, over expanded arguments (argv[0] excluded).</summary>
    public static bool EvaluateTest(ShellExec exec, IReadOnlyList<string> args)
    {
        // POSIX fixes the meaning of one, two and three arguments before any parsing,
        // so that `[ -f ]` tests the string "-f" and `[ x = y ]` is a comparison even
        // when x looks like an operator.
        switch (args.Count)
        {
            case 0:
                return false;
            case 1:
                return args[0].Length > 0;
            case 2 when args[0] == "!":
                return args[1].Length == 0;
            case 2 when IsUnaryOperator(args[0]):
                return Unary(exec, args[0], args[1]);
            case 3 when IsBinaryOperator(args[1]):
                return Binary(exec, args[0], args[1], args[2], pattern: false);
            case 3 when args[0] == "!":
                return !EvaluateTest(exec, args.Skip(1).ToList());
            case 4 when args[0] == "!":
                return !EvaluateTest(exec, args.Skip(1).ToList());
        }

        var parser = new TestParser(exec, args);
        bool result = parser.ParseOr();
        parser.ExpectEnd();
        return result;
    }

    private static bool IsUnaryOperator(string token) => token.Length == 2 && token[0] == '-'
        && "bcdefgknprstuwxzLShON".IndexOf(token[1]) >= 0;

    private static bool IsBinaryOperator(string token) => token is "=" or "==" or "!=" or "<" or ">"
        or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge" or "-nt" or "-ot" or "-ef" or "=~";

    private static bool Unary(ShellExec exec, string op, string operand)
    {
        switch (op)
        {
            case "-z": return operand.Length == 0;
            case "-n": return operand.Length > 0;
            case "-t": return false;                 // no terminal in this host, ever
            case "-o": return Option(exec, operand);
        }

        // Every remaining unary operator is a file test.
        if (!TryResolveForTest(exec, operand, out string path))
            return false;

        return op switch
        {
            "-e" => File.Exists(path) || Directory.Exists(path),
            "-f" => File.Exists(path),
            "-d" => Directory.Exists(path),
            "-s" => File.Exists(path) && new FileInfo(path).Length > 0,
            "-r" => File.Exists(path) || Directory.Exists(path),
            "-w" => (File.Exists(path) || Directory.Exists(path)) && exec.Paths.IsAllowed(ConfinedPaths.RealPath(path), PathAccess.Write),
            "-x" => Directory.Exists(path) || (File.Exists(path) && IsExecutable(path)),
            "-L" or "-h" => IsSymlink(path),
            "-p" or "-S" or "-b" or "-c" or "-g" or "-u" or "-k" => false,
            "-O" or "-G" => File.Exists(path) || Directory.Exists(path),
            "-N" => File.Exists(path),
            _ => false,
        };
    }

    private static bool Option(ShellExec exec, string name) => name switch
    {
        "errexit" => exec.State.ErrExit,
        "xtrace" => exec.State.XTrace,
        "nounset" => exec.State.NoUnset,
        "pipefail" => exec.State.PipeFail,
        "noglob" => exec.State.NoGlob,
        _ => false,
    };

    private static bool IsSymlink(string path)
    {
        try
        {
            FileSystemInfo? info = File.Exists(path) ? new FileInfo(path)
                : Directory.Exists(path) ? new DirectoryInfo(path) : null;
            return info?.LinkTarget is not null;
        }
        catch (IOException) { return false; }
    }

    private static bool IsExecutable(string path)
    {
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A path outside the session is not an error here: the test is simply false.</summary>
    private static bool TryResolveForTest(ShellExec exec, string operand, out string path)
    {
        path = string.Empty;
        if (operand.Length == 0)
            return false;
        return exec.Paths.TryResolve(operand, exec.State.Cwd, PathAccess.Read, out path, out _);
    }

    private static bool Binary(ShellExec exec, string left, string op, string right, bool pattern)
    {
        switch (op)
        {
            case "=" or "==":
                return pattern ? GlobMatcher.IsMatch(right, left) : string.Equals(left, right, StringComparison.Ordinal);
            case "!=":
                return pattern ? !GlobMatcher.IsMatch(right, left) : !string.Equals(left, right, StringComparison.Ordinal);
            case "=~":
                try { return Regex.IsMatch(left, PosixRegex.Translate(right, extended: true), RegexOptions.None, TimeSpan.FromSeconds(2)); }
                catch (ArgumentException ex) { throw new ShellUsageException($"invalid regular expression: {ex.Message}", 2); }
            case "<":
                return string.CompareOrdinal(left, right) < 0;
            case ">":
                return string.CompareOrdinal(left, right) > 0;
        }

        if (op is "-nt" or "-ot" or "-ef")
        {
            if (!TryResolveForTest(exec, left, out string lp) || !TryResolveForTest(exec, right, out string rp))
                return false;
            if (op == "-ef")
                return string.Equals(ConfinedPaths.RealPath(lp), ConfinedPaths.RealPath(rp), StringComparison.Ordinal);
            DateTime lt = LastWrite(lp), rt = LastWrite(rp);
            return op == "-nt" ? lt > rt : lt < rt;
        }

        long a = Number(left, op), b = Number(right, op);
        return op switch
        {
            "-eq" => a == b,
            "-ne" => a != b,
            "-lt" => a < b,
            "-le" => a <= b,
            "-gt" => a > b,
            "-ge" => a >= b,
            _ => throw new ShellUsageException($"unknown operator {op}", 2),
        };
    }

    private static DateTime LastWrite(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
    }

    private static long Number(string text, string op)
    {
        if (long.TryParse(text.Trim(), out long value))
            return value;
        throw new ShellUsageException($"{text}: integer expression expected ({op})", 2);
    }

    /// <summary>Recursive-descent over the already-expanded arguments of <c>test</c>.</summary>
    private sealed class TestParser(ShellExec exec, IReadOnlyList<string> args)
    {
        private int _i;

        private bool AtEnd => _i >= args.Count;
        private string Peek => args[_i];

        public void ExpectEnd()
        {
            if (!AtEnd)
                throw new ShellUsageException($"unexpected argument '{Peek}'", 2);
        }

        public bool ParseOr()
        {
            bool value = ParseAnd();
            while (!AtEnd && Peek == "-o")
            {
                _i++;
                bool right = ParseAnd();
                value = value || right;
            }
            return value;
        }

        private bool ParseAnd()
        {
            bool value = ParseUnary();
            while (!AtEnd && Peek == "-a")
            {
                _i++;
                bool right = ParseUnary();
                value = value && right;
            }
            return value;
        }

        private bool ParseUnary()
        {
            if (!AtEnd && Peek == "!")
            {
                _i++;
                return !ParseUnary();
            }
            if (!AtEnd && Peek == "(")
            {
                _i++;
                bool inner = ParseOr();
                if (AtEnd || Peek != ")")
                    throw new ShellUsageException("missing ')'", 2);
                _i++;
                return inner;
            }
            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            if (AtEnd)
                throw new ShellUsageException("argument expected", 2);
            string token = args[_i];

            // A binary operator one token ahead wins over a unary reading of this token.
            if (_i + 2 < args.Count + 0 && _i + 1 < args.Count && IsBinaryOperator(args[_i + 1]))
            {
                string left = token, op = args[_i + 1], right = _i + 2 < args.Count ? args[_i + 2] : string.Empty;
                _i += 3;
                return Binary(exec, left, op, right, pattern: false);
            }
            if (IsUnaryOperator(token) || token is "-o" && _i + 1 < args.Count)
            {
                if (_i + 1 >= args.Count)
                    throw new ShellUsageException($"{token}: argument expected", 2);
                string operand = args[_i + 1];
                _i += 2;
                return Unary(exec, token, operand);
            }
            _i++;
            return token.Length > 0;
        }
    }

    /// <summary>Recursive-descent over the words of <c>[[ ]]</c>, expanding lazily so a
    /// pattern keeps its quoting.</summary>
    private sealed class ConditionalParser(ShellExec exec, List<Word> words)
    {
        private int _i;

        private bool AtEnd => _i >= words.Count;
        private string Source => words[_i].Source;

        public void ExpectEnd()
        {
            if (!AtEnd)
                throw new ShellUsageException($"unexpected token '{Source}' in [[ ]]", 2);
        }

        public bool ParseOr()
        {
            bool value = ParseAnd();
            while (!AtEnd && (Source == "||" || Source == "-o"))
            {
                _i++;
                bool right = ParseAnd();
                value = value || right;
            }
            return value;
        }

        private bool ParseAnd()
        {
            bool value = ParseUnary();
            while (!AtEnd && (Source == "&&" || Source == "-a"))
            {
                _i++;
                bool right = ParseUnary();
                value = value && right;
            }
            return value;
        }

        private bool ParseUnary()
        {
            if (!AtEnd && Source == "!")
            {
                _i++;
                return !ParseUnary();
            }
            if (!AtEnd && Source == "(")
            {
                _i++;
                bool inner = ParseOr();
                if (AtEnd || Source != ")")
                    throw new ShellUsageException("missing ')' in [[ ]]", 2);
                _i++;
                return inner;
            }
            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            if (AtEnd)
                throw new ShellUsageException("argument expected in [[ ]]", 2);

            string token = words[_i].Source;
            if (IsUnaryOperator(token))
            {
                if (_i + 1 >= words.Count)
                    throw new ShellUsageException($"{token}: argument expected", 2);
                string operand = exec.ExpandSingle(words[_i + 1]);
                _i += 2;
                return Unary(exec, token, operand);
            }

            string left = exec.ExpandSingle(words[_i]);
            if (_i + 1 < words.Count && IsBinaryOperator(words[_i + 1].Source))
            {
                string op = words[_i + 1].Source;
                if (_i + 2 >= words.Count)
                    throw new ShellUsageException($"{op}: argument expected", 2);
                // == and != take a PATTERN; =~ takes a regular expression written
                // literally; everything else compares text.
                bool pattern = op is "=" or "==" or "!=";
                string right = pattern ? exec.ExpandPattern(words[_i + 2]) : exec.ExpandSingle(words[_i + 2]);
                _i += 3;
                return Binary(exec, left, op, right, pattern);
            }
            _i++;
            return left.Length > 0;
        }
    }
}
