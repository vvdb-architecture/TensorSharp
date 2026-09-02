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

namespace TensorAgent.Core.Shell;

/// <summary>
/// Shell arithmetic: <c>$(( ... ))</c>, <c>(( ... ))</c> and <c>for (( ... ))</c>.
/// C precedence over 64-bit integers, with the shell's own rules: a bare name is a
/// variable, an unset one is zero, assignment writes the variable back.
/// </summary>
internal sealed class ShellArithmetic
{
    private readonly string _s;
    private int _p;
    private readonly Func<string, string> _get;
    private readonly Action<string, string> _set;

    private ShellArithmetic(string expression, Func<string, string> get, Action<string, string> set)
    {
        _s = expression;
        _get = get;
        _set = set;
    }

    public static long Evaluate(string expression, Func<string, string> get, Action<string, string> set)
    {
        var a = new ShellArithmetic(expression, get, set);
        a.SkipSpaces();
        if (a._p >= a._s.Length)
            return 0;
        long value = a.ParseComma();
        a.SkipSpaces();
        if (a._p < a._s.Length)
            throw new ShellArithmeticException($"syntax error in expression (error token is \"{a._s.Substring(a._p)}\")");
        return value;
    }

    private char Cur => _p < _s.Length ? _s[_p] : '\0';
    private char At(int o) => _p + o < _s.Length ? _s[_p + o] : '\0';

    private void SkipSpaces()
    {
        while (_p < _s.Length && char.IsWhiteSpace(_s[_p]))
            _p++;
    }

    private bool Accept(string op)
    {
        SkipSpaces();
        if (string.CompareOrdinal(_s, _p, op, 0, op.Length) == 0)
        {
            // do not take "<" when "<=" or "<<" follows, etc.
            char next = At(op.Length);
            if (op.Length == 1 && op[0] is '<' or '>' or '=' or '!' or '&' or '|' or '+' or '-' or '*' or '/' or '%' or '^' && (next == '=' || (op[0] is '<' or '>' or '&' or '|' or '+' or '-' or '*' && next == op[0])))
                return false;
            if (op.Length == 2 && op[1] == op[0] && op[0] is '<' or '>' && next == '=')
                return false;
            _p += op.Length;
            return true;
        }
        return false;
    }

    private long ParseComma()
    {
        long v = ParseAssignment();
        while (Accept(","))
            v = ParseAssignment();
        return v;
    }

    private long ParseAssignment()
    {
        SkipSpaces();
        int save = _p;
        if (IsNameStart(Cur))
        {
            string name = ReadName();
            SkipSpaces();
            string[] ops = { "=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "**=" };
            foreach (string op in ops)
            {
                if (string.CompareOrdinal(_s, _p, op, 0, op.Length) == 0 && !(op == "=" && At(1) == '='))
                {
                    _p += op.Length;
                    long rhs = ParseAssignment();
                    long current = Value(name);
                    long result = op switch
                    {
                        "=" => rhs,
                        "+=" => current + rhs,
                        "-=" => current - rhs,
                        "*=" => current * rhs,
                        "/=" => Div(current, rhs),
                        "%=" => Mod(current, rhs),
                        "<<=" => current << (int)rhs,
                        ">>=" => current >> (int)rhs,
                        "&=" => current & rhs,
                        "|=" => current | rhs,
                        "^=" => current ^ rhs,
                        _ => Pow(current, rhs),
                    };
                    _set(name, result.ToString(CultureInfo.InvariantCulture));
                    return result;
                }
            }
            _p = save;
        }
        return ParseTernary();
    }

    private long ParseTernary()
    {
        long cond = ParseOr();
        if (Accept("?"))
        {
            long a = ParseAssignment();
            SkipSpaces();
            if (!Accept(":"))
                throw new ShellArithmeticException("expected ':' in conditional expression");
            long b = ParseAssignment();
            return cond != 0 ? a : b;
        }
        return cond;
    }

    private long ParseOr()
    {
        long v = ParseAnd();
        while (Accept("||"))
        {
            long r = ParseAnd();
            v = (v != 0 || r != 0) ? 1 : 0;
        }
        return v;
    }

    private long ParseAnd()
    {
        long v = ParseBitOr();
        while (Accept("&&"))
        {
            long r = ParseBitOr();
            v = (v != 0 && r != 0) ? 1 : 0;
        }
        return v;
    }

    private long ParseBitOr()
    {
        long v = ParseBitXor();
        while (Accept("|"))
            v |= ParseBitXor();
        return v;
    }

    private long ParseBitXor()
    {
        long v = ParseBitAnd();
        while (Accept("^"))
            v ^= ParseBitAnd();
        return v;
    }

    private long ParseBitAnd()
    {
        long v = ParseEquality();
        while (Accept("&"))
            v &= ParseEquality();
        return v;
    }

    private long ParseEquality()
    {
        long v = ParseRelational();
        while (true)
        {
            if (Accept("=="))
                v = v == ParseRelational() ? 1 : 0;
            else if (Accept("!="))
                v = v != ParseRelational() ? 1 : 0;
            else
                return v;
        }
    }

    private long ParseRelational()
    {
        long v = ParseShift();
        while (true)
        {
            if (Accept("<="))
                v = v <= ParseShift() ? 1 : 0;
            else if (Accept(">="))
                v = v >= ParseShift() ? 1 : 0;
            else if (Accept("<"))
                v = v < ParseShift() ? 1 : 0;
            else if (Accept(">"))
                v = v > ParseShift() ? 1 : 0;
            else
                return v;
        }
    }

    private long ParseShift()
    {
        long v = ParseAdditive();
        while (true)
        {
            if (Accept("<<"))
                v <<= (int)ParseAdditive();
            else if (Accept(">>"))
                v >>= (int)ParseAdditive();
            else
                return v;
        }
    }

    private long ParseAdditive()
    {
        long v = ParseMultiplicative();
        while (true)
        {
            if (Accept("+"))
                v += ParseMultiplicative();
            else if (Accept("-"))
                v -= ParseMultiplicative();
            else
                return v;
        }
    }

    private long ParseMultiplicative()
    {
        long v = ParsePower();
        while (true)
        {
            if (Accept("*"))
                v *= ParsePower();
            else if (Accept("/"))
                v = Div(v, ParsePower());
            else if (Accept("%"))
                v = Mod(v, ParsePower());
            else
                return v;
        }
    }

    private long ParsePower()
    {
        long v = ParseUnary();
        if (Accept("**"))
            return Pow(v, ParsePower());
        return v;
    }

    private long ParseUnary()
    {
        SkipSpaces();
        if (Accept("!"))
            return ParseUnary() == 0 ? 1 : 0;
        if (Accept("~"))
            return ~ParseUnary();
        if (Accept("++"))
        {
            string name = ExpectName();
            long v = Value(name) + 1;
            _set(name, v.ToString(CultureInfo.InvariantCulture));
            return v;
        }
        if (Accept("--"))
        {
            string name = ExpectName();
            long v = Value(name) - 1;
            _set(name, v.ToString(CultureInfo.InvariantCulture));
            return v;
        }
        if (Accept("-"))
            return -ParseUnary();
        if (Accept("+"))
            return ParseUnary();
        return ParsePostfix();
    }

    private long ParsePostfix()
    {
        SkipSpaces();
        if (Cur == '(')
        {
            _p++;
            long v = ParseComma();
            SkipSpaces();
            if (Cur != ')')
                throw new ShellArithmeticException("missing ')'");
            _p++;
            return v;
        }
        if (Cur == '$')
        {
            _p++;
            if (Cur == '{')
            {
                int close = _s.IndexOf('}', _p);
                if (close < 0)
                    throw new ShellArithmeticException("missing '}'");
                string n = _s.Substring(_p + 1, close - _p - 1);
                _p = close + 1;
                return Value(n);
            }
            return Value(ExpectName());
        }
        if (char.IsAsciiDigit(Cur))
            return ReadNumber();
        if (IsNameStart(Cur))
        {
            string name = ReadName();
            SkipSpaces();
            if (Cur == '[')
            {
                int close = _s.IndexOf(']', _p);
                if (close < 0)
                    throw new ShellArithmeticException("missing ']'");
                string index = _s.Substring(_p + 1, close - _p - 1);
                _p = close + 1;
                long i = Evaluate(index, _get, _set);
                name = name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
            }
            if (Cur == '+' && At(1) == '+')
            {
                _p += 2;
                long v = Value(name);
                _set(name, (v + 1).ToString(CultureInfo.InvariantCulture));
                return v;
            }
            if (Cur == '-' && At(1) == '-')
            {
                _p += 2;
                long v = Value(name);
                _set(name, (v - 1).ToString(CultureInfo.InvariantCulture));
                return v;
            }
            return Value(name);
        }
        throw new ShellArithmeticException(_p >= _s.Length
            ? "syntax error: operand expected"
            : $"syntax error: operand expected (error token is \"{_s.Substring(_p)}\")");
    }

    private long ReadNumber()
    {
        int start = _p;
        if (Cur == '0' && (At(1) == 'x' || At(1) == 'X'))
        {
            _p += 2;
            while (char.IsAsciiHexDigit(Cur))
                _p++;
            return long.Parse(_s.AsSpan(start + 2, _p - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        while (char.IsAsciiDigit(Cur))
            _p++;
        if (Cur == '#')
        {
            int b = int.Parse(_s.AsSpan(start, _p - start), CultureInfo.InvariantCulture);
            _p++;
            int digitsStart = _p;
            while (char.IsAsciiLetterOrDigit(Cur))
                _p++;
            long v = 0;
            foreach (char d in _s.AsSpan(digitsStart, _p - digitsStart))
            {
                int digit = char.IsAsciiDigit(d) ? d - '0' : char.ToLowerInvariant(d) - 'a' + 10;
                if (digit >= b)
                    throw new ShellArithmeticException($"value too great for base (error token is \"{d}\")");
                v = v * b + digit;
            }
            return v;
        }
        string text = _s.Substring(start, _p - start);
        if (text.Length > 1 && text[0] == '0' && text.All(ch => ch >= '0' && ch <= '7'))
            return Convert.ToInt64(text, 8);
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private string ReadName()
    {
        int start = _p;
        while (_p < _s.Length && (char.IsAsciiLetterOrDigit(_s[_p]) || _s[_p] == '_'))
            _p++;
        return _s.Substring(start, _p - start);
    }

    private string ExpectName()
    {
        SkipSpaces();
        if (!IsNameStart(Cur))
            throw new ShellArithmeticException("syntax error: variable name expected");
        return ReadName();
    }

    private long Value(string name)
    {
        string text = _get(name).Trim();
        if (text.Length == 0)
            return 0;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
            return v;
        // A value that is itself an expression (x="y+1") evaluates recursively, as in bash.
        try
        {
            return Evaluate(text, _get, _set);
        }
        catch (ShellArithmeticException)
        {
            return 0;
        }
    }

    private static long Div(long a, long b)
    {
        if (b == 0)
            throw new ShellArithmeticException("division by 0");
        return a / b;
    }

    private static long Mod(long a, long b)
    {
        if (b == 0)
            throw new ShellArithmeticException("division by 0");
        return a % b;
    }

    private static long Pow(long a, long b)
    {
        if (b < 0)
            return 0;
        long r = 1;
        for (long i = 0; i < b; i++)
            r *= a;
        return r;
    }
}

internal sealed class ShellArithmeticException : Exception
{
    public ShellArithmeticException(string message) : base(message) { }
}
