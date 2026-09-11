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
using System.Text.RegularExpressions;

namespace TensorAgent.Core.Shell;

/// <summary>
/// The part of awk a shell one-liner actually uses: <c>BEGIN</c>/<c>END</c> blocks,
/// pattern-action rules, field references, the built-in variables, arithmetic and
/// string expressions, regular-expression matching, arrays, <c>if</c>/<c>while</c>/
/// <c>for</c>, and the <c>print</c>/<c>printf</c>/<c>split</c>/<c>substr</c>/
/// <c>length</c>/<c>gsub</c> family.
///
/// <para>
/// It is a real interpreter rather than a pattern match on common forms because awk
/// lines a model writes vary endlessly (<c>awk -F: '$3 &gt; 500 {print $1, $NF}'</c>),
/// and answering only the shapes someone anticipated would be a silent wrong answer
/// for the rest.
/// </para>
/// </summary>
internal sealed class AwkInterpreter
{
    private readonly List<Rule> _rules = new();
    private readonly List<Node> _begin = new();
    private readonly List<Node> _end = new();
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _arrays = new(StringComparer.Ordinal);
    private readonly Action<string> _emit;
    private readonly StringBuilder _pending = new();
    private string[] _fields = Array.Empty<string>();
    private string _record = string.Empty;

    public AwkInterpreter(string program, string fieldSeparator, Action<string> emit)
    {
        _emit = emit;
        _vars["FS"] = fieldSeparator;
        _vars["OFS"] = " ";
        _vars["ORS"] = "\n";
        _vars["NR"] = "0";
        _vars["NF"] = "0";
        _vars["RS"] = "\n";
        var parser = new AwkParser(program);
        parser.ParseProgram(_begin, _end, _rules);
    }

    public void SetVariable(string name, string value) => _vars[name] = value;

    public void Begin()
    {
        foreach (Node node in _begin)
            Execute(node);
    }

    public void End()
    {
        foreach (Node node in _end)
            Execute(node);
        Flush();
    }

    /// <summary>
    /// awk's <c>print</c> and <c>printf</c> both write raw bytes: only print adds a
    /// record separator, and printf may leave a line half-written. The sink here is
    /// line-based, so text is buffered and handed over one complete line at a time.
    /// </summary>
    private void Write(string raw)
    {
        _pending.Append(raw);
        int start = 0;
        for (int i = 0; i < _pending.Length; i++)
        {
            if (_pending[i] != '\n')
                continue;
            _emit(_pending.ToString(start, i - start));
            start = i + 1;
        }
        if (start > 0)
            _pending.Remove(0, start);
    }

    /// <summary>Hand over a final line that printf left without a newline.</summary>
    public void Flush()
    {
        if (_pending.Length == 0)
            return;
        string rest = _pending.ToString();
        _pending.Clear();
        _emit(rest);
    }

    public void Process(string line)
    {
        _record = line;
        SplitRecord();
        _vars["NR"] = (long.Parse(_vars["NR"], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        foreach (Rule rule in _rules)
        {
            if (rule.Pattern is not null && !Truthy(Evaluate(rule.Pattern)))
                continue;
            try
            {
                foreach (Node node in rule.Body)
                    Execute(node);
            }
            catch (AwkNextException)
            {
                return;
            }
        }
    }

    private void SplitRecord()
    {
        string fs = _vars.TryGetValue("FS", out string? sep) ? sep : " ";
        _fields = fs switch
        {
            " " => _record.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            _ when fs.Length == 1 => _record.Split(fs[0]),
            _ => Regex.Split(_record, PosixRegex.Translate(fs, extended: true)),
        };
        _vars["NF"] = _fields.Length.ToString(CultureInfo.InvariantCulture);
    }

    private string Field(int index)
    {
        if (index == 0) return _record;
        return index >= 1 && index <= _fields.Length ? _fields[index - 1] : string.Empty;
    }

    private void SetField(int index, string value)
    {
        if (index == 0)
        {
            _record = value;
            SplitRecord();
            return;
        }
        if (index > _fields.Length)
        {
            var grown = new string[index];
            Array.Copy(_fields, grown, _fields.Length);
            for (int i = _fields.Length; i < index; i++) grown[i] = string.Empty;
            _fields = grown;
            _vars["NF"] = index.ToString(CultureInfo.InvariantCulture);
        }
        _fields[index - 1] = value;
        _record = string.Join(_vars.TryGetValue("OFS", out string? ofs) ? ofs : " ", _fields);
    }

    // ---- execution ------------------------------------------------------------------

    private void Execute(Node node)
    {
        switch (node)
        {
            case PrintNode print:
            {
                string ors = _vars.TryGetValue("ORS", out string? o) ? o : "\n";
                string text = print.Arguments.Count == 0
                    ? _record
                    : string.Join(_vars.TryGetValue("OFS", out string? ofs) ? ofs : " ", print.Arguments.Select(a => ToText(Evaluate(a))));
                Write(text + ors);
                break;
            }
            case PrintfNode printf:
            {
                var values = new Queue<string>(printf.Arguments.Skip(1).Select(a => ToText(Evaluate(a))));
                string format = ToText(Evaluate(printf.Arguments[0]));
                var sb = new StringBuilder();
                bool consumed = false;
                AwkFormat(format, values, sb, ref consumed);
                Write(sb.ToString());
                break;
            }
            case ExpressionNode expression:
                Evaluate(expression.Value);
                break;
            case IfNode branch:
                if (Truthy(Evaluate(branch.Condition)))
                    foreach (Node n in branch.Then) Execute(n);
                else
                    foreach (Node n in branch.Else) Execute(n);
                break;
            case WhileNode loop:
            {
                int guard = 0;
                while (Truthy(Evaluate(loop.Condition)))
                {
                    if (++guard > 1_000_000) throw new ShellUsageException("awk: loop did not terminate");
                    try { foreach (Node n in loop.Body) Execute(n); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                }
                break;
            }
            case ForNode loop:
            {
                if (loop.Initial is not null) Evaluate(loop.Initial);
                int guard = 0;
                while (loop.Condition is null || Truthy(Evaluate(loop.Condition)))
                {
                    if (++guard > 1_000_000) throw new ShellUsageException("awk: loop did not terminate");
                    try { foreach (Node n in loop.Body) Execute(n); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                    if (loop.Step is not null) Evaluate(loop.Step);
                }
                break;
            }
            case ForInNode loop:
            {
                Dictionary<string, string> array = ArrayFor(loop.Array);
                foreach (string key in array.Keys.ToList())
                {
                    _vars[loop.Variable] = key;
                    try { foreach (Node n in loop.Body) Execute(n); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                }
                break;
            }
            case BlockNode block:
                foreach (Node n in block.Body) Execute(n);
                break;
            case NextNode:
                throw new AwkNextException();
            case BreakNode:
                throw new AwkBreakException();
            case ContinueNode:
                throw new AwkContinueException();
            case DeleteNode delete:
                ArrayFor(delete.Array).Remove(ToText(Evaluate(delete.Index)));
                break;
        }
    }

    private Dictionary<string, string> ArrayFor(string name)
    {
        if (!_arrays.TryGetValue(name, out Dictionary<string, string>? array))
            _arrays[name] = array = new Dictionary<string, string>(StringComparer.Ordinal);
        return array;
    }

    private static void AwkFormat(string format, Queue<string> values, StringBuilder sb, ref bool consumed)
    {
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                sb.Append(format[i + 1] switch { 'n' => "\n", 't' => "\t", 'r' => "\r", '\\' => "\\", '"' => "\"", _ => "\\" + format[i + 1] });
                i++;
                continue;
            }
            if (c != '%') { sb.Append(c); continue; }
            if (i + 1 < format.Length && format[i + 1] == '%') { sb.Append('%'); i++; continue; }

            int start = i++;
            while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0) i++;
            while (i < format.Length && char.IsDigit(format[i])) i++;
            if (i < format.Length && format[i] == '.') { i++; while (i < format.Length && char.IsDigit(format[i])) i++; }
            if (i >= format.Length) { sb.Append(format[start..]); return; }
            char conversion = format[i];
            string spec = format[start..i];
            string next = values.Count > 0 ? values.Dequeue() : string.Empty;
            consumed = true;
            string rendered = conversion switch
            {
                'd' or 'i' => ((long)ToNumber(next)).ToString(CultureInfo.InvariantCulture),
                'f' or 'F' => ToNumber(next).ToString("F" + PrecisionOf(spec, 6), CultureInfo.InvariantCulture),
                'e' or 'E' => ToNumber(next).ToString("e" + PrecisionOf(spec, 6), CultureInfo.InvariantCulture),
                'g' or 'G' => ToNumber(next).ToString("G", CultureInfo.InvariantCulture),
                'x' => ((long)ToNumber(next)).ToString("x", CultureInfo.InvariantCulture),
                'X' => ((long)ToNumber(next)).ToString("X", CultureInfo.InvariantCulture),
                'o' => Convert.ToString((long)ToNumber(next), 8),
                'c' => next.Length > 0 ? next[0].ToString() : string.Empty,
                _ => next,
            };
            string widthDigits = new(spec.TrimStart('%', '-', '+', ' ', '#').TakeWhile(char.IsDigit).ToArray());
            if (widthDigits.Length > 0)
            {
                int width = int.Parse(widthDigits, CultureInfo.InvariantCulture);
                bool left = spec.Contains('-');
                char pad = spec.TrimStart('%', '-', '+', ' ', '#').StartsWith('0') && !left ? '0' : ' ';
                rendered = left ? rendered.PadRight(width) : rendered.PadLeft(width, pad);
            }
            sb.Append(rendered);
        }
    }

    private static int PrecisionOf(string spec, int fallback)
    {
        int dot = spec.IndexOf('.');
        if (dot < 0) return fallback;
        string digits = new(spec[(dot + 1)..].TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : 0;
    }

    // ---- expression evaluation --------------------------------------------------------

    private object Evaluate(Expr expr)
    {
        switch (expr)
        {
            case NumberExpr n: return n.Value;
            case StringExpr s: return s.Value;
            case RegexExpr r: return Regex.IsMatch(_record, r.Pattern) ? 1.0 : 0.0;
            case FieldExpr f: return Field((int)ToNumber(Evaluate(f.Index)));
            case VariableExpr v: return _vars.TryGetValue(v.Name, out string? value) ? value : string.Empty;
            case IndexExpr ix:
            {
                Dictionary<string, string> array = ArrayFor(ix.Name);
                string key = ToText(Evaluate(ix.Index));
                return array.TryGetValue(key, out string? element) ? element : string.Empty;
            }
            case AssignExpr assign:
            {
                object assigned = Evaluate(assign.Value);
                if (assign.Operator != "=")
                {
                    double current = ToNumber(EvaluateTarget(assign.Target));
                    double operand = ToNumber(assigned);
                    assigned = assign.Operator switch
                    {
                        "+=" => current + operand,
                        "-=" => current - operand,
                        "*=" => current * operand,
                        "/=" => operand == 0 ? throw new ShellUsageException("awk: division by zero") : current / operand,
                        "%=" => operand == 0 ? throw new ShellUsageException("awk: division by zero") : current % operand,
                        "^=" => Math.Pow(current, operand),
                        _ => assigned,
                    };
                }
                Assign(assign.Target, assigned);
                return assigned;
            }
            case IncrementExpr inc:
            {
                double current = ToNumber(EvaluateTarget(inc.Target));
                double updated = current + (inc.Increase ? 1 : -1);
                Assign(inc.Target, updated);
                return inc.Prefix ? updated : current;
            }
            case UnaryExpr unary:
            {
                object operand = Evaluate(unary.Operand);
                return unary.Operator switch
                {
                    "-" => -ToNumber(operand),
                    "+" => ToNumber(operand),
                    "!" => Truthy(operand) ? 0.0 : 1.0,
                    _ => operand,
                };
            }
            case BinaryExpr binary: return Binary(binary);
            case TernaryExpr ternary: return Truthy(Evaluate(ternary.Condition)) ? Evaluate(ternary.Then) : Evaluate(ternary.Else);
            case CallExpr call: return Call(call);
            case InExpr inExpr: return ArrayFor(inExpr.Array).ContainsKey(ToText(Evaluate(inExpr.Index))) ? 1.0 : 0.0;
            case GroupExpr group: return Evaluate(group.Inner);
            default: return string.Empty;
        }
    }

    private object EvaluateTarget(Expr target) => Evaluate(target);

    private void Assign(Expr target, object value)
    {
        switch (target)
        {
            case VariableExpr v:
                _vars[v.Name] = ToText(value);
                if (v.Name == "FS" || v.Name == "OFS") { }
                break;
            case FieldExpr f:
                SetField((int)ToNumber(Evaluate(f.Index)), ToText(value));
                break;
            case IndexExpr ix:
                ArrayFor(ix.Name)[ToText(Evaluate(ix.Index))] = ToText(value);
                break;
            default:
                throw new ShellUsageException("awk: cannot assign to this expression");
        }
    }

    private object Binary(BinaryExpr binary)
    {
        if (binary.Operator == "&&")
            return Truthy(Evaluate(binary.Left)) && Truthy(Evaluate(binary.Right)) ? 1.0 : 0.0;
        if (binary.Operator == "||")
            return Truthy(Evaluate(binary.Left)) || Truthy(Evaluate(binary.Right)) ? 1.0 : 0.0;

        object left = Evaluate(binary.Left);
        if (binary.Operator is "~" or "!~")
        {
            string pattern = binary.Right is RegexExpr re ? re.Pattern : PosixRegex.Translate(ToText(Evaluate(binary.Right)), extended: true);
            bool match = Regex.IsMatch(ToText(left), pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
            return (binary.Operator == "~" ? match : !match) ? 1.0 : 0.0;
        }

        object right = Evaluate(binary.Right);
        switch (binary.Operator)
        {
            case " ": return ToText(left) + ToText(right);
            case "+": return ToNumber(left) + ToNumber(right);
            case "-": return ToNumber(left) - ToNumber(right);
            case "*": return ToNumber(left) * ToNumber(right);
            case "/":
                if (ToNumber(right) == 0) throw new ShellUsageException("awk: division by zero");
                return ToNumber(left) / ToNumber(right);
            case "%":
                if (ToNumber(right) == 0) throw new ShellUsageException("awk: division by zero");
                return ToNumber(left) % ToNumber(right);
            case "^": return Math.Pow(ToNumber(left), ToNumber(right));
        }

        // Comparisons compare numerically when both sides look numeric, else as text.
        bool numeric = IsNumeric(left) && IsNumeric(right);
        int comparison = numeric
            ? ToNumber(left).CompareTo(ToNumber(right))
            : string.CompareOrdinal(ToText(left), ToText(right));
        return binary.Operator switch
        {
            "<" => comparison < 0 ? 1.0 : 0.0,
            "<=" => comparison <= 0 ? 1.0 : 0.0,
            ">" => comparison > 0 ? 1.0 : 0.0,
            ">=" => comparison >= 0 ? 1.0 : 0.0,
            "==" => comparison == 0 ? 1.0 : 0.0,
            "!=" => comparison != 0 ? 1.0 : 0.0,
            _ => 0.0,
        };
    }

    private object Call(CallExpr call)
    {
        string[] Text() => call.Arguments.Select(a => ToText(Evaluate(a))).ToArray();
        switch (call.Name)
        {
            case "length":
                return call.Arguments.Count == 0 ? _record.Length : (double)ToText(Evaluate(call.Arguments[0])).Length;
            case "substr":
            {
                string s = ToText(Evaluate(call.Arguments[0]));
                int start = (int)ToNumber(Evaluate(call.Arguments[1]));
                int length = call.Arguments.Count > 2 ? (int)ToNumber(Evaluate(call.Arguments[2])) : int.MaxValue;
                if (start < 1) { length += start - 1; start = 1; }
                if (start > s.Length || length <= 0) return string.Empty;
                return s.Substring(start - 1, Math.Min(length, s.Length - start + 1));
            }
            case "index":
            {
                string[] args = Text();
                return (double)(args[0].IndexOf(args[1], StringComparison.Ordinal) + 1);
            }
            case "split":
            {
                string s = ToText(Evaluate(call.Arguments[0]));
                string name = call.Arguments[1] is VariableExpr v ? v.Name : throw new ShellUsageException("awk: split needs an array");
                string fs = call.Arguments.Count > 2 ? ToText(Evaluate(call.Arguments[2])) : _vars.GetValueOrDefault("FS", " ");
                string[] parts = fs == " "
                    ? s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    : fs.Length == 1 ? s.Split(fs[0]) : Regex.Split(s, PosixRegex.Translate(fs, extended: true));
                Dictionary<string, string> array = ArrayFor(name);
                array.Clear();
                for (int i = 0; i < parts.Length; i++)
                    array[(i + 1).ToString(CultureInfo.InvariantCulture)] = parts[i];
                return (double)parts.Length;
            }
            case "sub" or "gsub":
            {
                string pattern = call.Arguments[0] is RegexExpr re ? re.Pattern : PosixRegex.Translate(ToText(Evaluate(call.Arguments[0])), extended: true);
                string replacement = ToText(Evaluate(call.Arguments[1]));
                Expr target = call.Arguments.Count > 2 ? call.Arguments[2] : new FieldExpr(new NumberExpr(0));
                string subject = ToText(Evaluate(target));
                var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
                int count = 0;
                string result = call.Name == "sub"
                    ? regex.Replace(subject, m => { count++; return replacement.Replace("&", m.Value, StringComparison.Ordinal); }, 1)
                    : regex.Replace(subject, m => { count++; return replacement.Replace("&", m.Value, StringComparison.Ordinal); });
                Assign(target, result);
                return (double)count;
            }
            case "match":
            {
                string s = ToText(Evaluate(call.Arguments[0]));
                string pattern = call.Arguments[1] is RegexExpr re ? re.Pattern : PosixRegex.Translate(ToText(Evaluate(call.Arguments[1])), extended: true);
                Match m = Regex.Match(s, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
                _vars["RSTART"] = (m.Success ? m.Index + 1 : 0).ToString(CultureInfo.InvariantCulture);
                _vars["RLENGTH"] = (m.Success ? m.Length : -1).ToString(CultureInfo.InvariantCulture);
                return (double)(m.Success ? m.Index + 1 : 0);
            }
            case "toupper": return Text()[0].ToUpperInvariant();
            case "tolower": return Text()[0].ToLowerInvariant();
            case "sprintf":
            {
                var values = new Queue<string>(call.Arguments.Skip(1).Select(a => ToText(Evaluate(a))));
                var sb = new StringBuilder();
                bool consumed = false;
                AwkFormat(ToText(Evaluate(call.Arguments[0])), values, sb, ref consumed);
                return sb.ToString();
            }
            case "int": return Math.Truncate(ToNumber(Evaluate(call.Arguments[0])));
            case "sqrt": return Math.Sqrt(ToNumber(Evaluate(call.Arguments[0])));
            case "exp": return Math.Exp(ToNumber(Evaluate(call.Arguments[0])));
            case "log": return Math.Log(ToNumber(Evaluate(call.Arguments[0])));
            case "sin": return Math.Sin(ToNumber(Evaluate(call.Arguments[0])));
            case "cos": return Math.Cos(ToNumber(Evaluate(call.Arguments[0])));
            case "atan2": return Math.Atan2(ToNumber(Evaluate(call.Arguments[0])), ToNumber(Evaluate(call.Arguments[1])));
            case "rand": return Random.Shared.NextDouble();
            case "srand": return 0.0;
            case "system" or "close" or "getline":
                throw new ShellUsageException($"awk: {call.Name}() is not available on this host");
            default:
                throw new ShellUsageException($"awk: calling undefined function {call.Name}");
        }
    }

    private static bool IsNumeric(object value)
        => value is double || (value is string s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    internal static double ToNumber(object value) => value switch
    {
        double d => d,
        string s => ParsePrefix(s),
        _ => 0,
    };

    /// <summary>awk reads the longest numeric prefix, so "12abc" is 12 and "abc" is 0.</summary>
    private static double ParsePrefix(string s)
    {
        s = s.Trim();
        int end = 0;
        if (end < s.Length && (s[end] == '+' || s[end] == '-')) end++;
        bool dot = false, digits = false;
        while (end < s.Length)
        {
            if (char.IsDigit(s[end])) { digits = true; end++; continue; }
            if (s[end] == '.' && !dot) { dot = true; end++; continue; }
            break;
        }
        if (!digits) return 0;
        return double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    internal static string ToText(object value) => value switch
    {
        string s => s,
        double d => d == Math.Truncate(d) && Math.Abs(d) < 1e15
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("G6", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static bool Truthy(object value) => value switch
    {
        double d => d != 0,
        string s => IsNumeric(s) ? ParsePrefix(s) != 0 : s.Length > 0,
        _ => false,
    };

    // ---- syntax tree -------------------------------------------------------------------

    internal sealed record Rule(Expr? Pattern, List<Node> Body);

    internal abstract record Node;
    internal sealed record PrintNode(List<Expr> Arguments) : Node;
    internal sealed record PrintfNode(List<Expr> Arguments) : Node;
    internal sealed record ExpressionNode(Expr Value) : Node;
    internal sealed record IfNode(Expr Condition, List<Node> Then, List<Node> Else) : Node;
    internal sealed record WhileNode(Expr Condition, List<Node> Body) : Node;
    internal sealed record ForNode(Expr? Initial, Expr? Condition, Expr? Step, List<Node> Body) : Node;
    internal sealed record ForInNode(string Variable, string Array, List<Node> Body) : Node;
    internal sealed record BlockNode(List<Node> Body) : Node;
    internal sealed record NextNode : Node;
    internal sealed record BreakNode : Node;
    internal sealed record ContinueNode : Node;
    internal sealed record DeleteNode(string Array, Expr Index) : Node;

    internal abstract record Expr;
    internal sealed record NumberExpr(double Value) : Expr;
    internal sealed record StringExpr(string Value) : Expr;
    internal sealed record RegexExpr(string Pattern) : Expr;
    internal sealed record FieldExpr(Expr Index) : Expr;
    internal sealed record VariableExpr(string Name) : Expr;
    internal sealed record IndexExpr(string Name, Expr Index) : Expr;
    internal sealed record AssignExpr(Expr Target, string Operator, Expr Value) : Expr;
    internal sealed record IncrementExpr(Expr Target, bool Increase, bool Prefix) : Expr;
    internal sealed record UnaryExpr(string Operator, Expr Operand) : Expr;
    internal sealed record BinaryExpr(Expr Left, string Operator, Expr Right) : Expr;
    internal sealed record TernaryExpr(Expr Condition, Expr Then, Expr Else) : Expr;
    internal sealed record CallExpr(string Name, List<Expr> Arguments) : Expr;
    internal sealed record InExpr(Expr Index, string Array) : Expr;
    internal sealed record GroupExpr(Expr Inner) : Expr;

    private sealed class AwkNextException : Exception;
    private sealed class AwkBreakException : Exception;
    private sealed class AwkContinueException : Exception;
}
