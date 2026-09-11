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
using static TensorAgent.Core.Shell.AwkInterpreter;

namespace TensorAgent.Core.Shell;

/// <summary>
/// Reads an awk program into the syntax tree <see cref="AwkInterpreter"/> walks.
///
/// <para>
/// Two things make awk's grammar awkward and both are handled here rather than in the
/// interpreter: string concatenation is written by juxtaposition, so <c>$1 $2</c> is a
/// binary operator with no symbol; and <c>/re/</c> is a regular expression in a value
/// position but a division everywhere else, which is decided by what the previous
/// token was.
/// </para>
/// </summary>
internal sealed class AwkParser
{
    private readonly List<Token> _tokens;
    private int _i;

    public AwkParser(string program)
    {
        _tokens = Tokenize(program);
    }

    public void ParseProgram(List<Node> begin, List<Node> end, List<Rule> rules)
    {
        SkipTerminators();
        while (!AtEnd)
        {
            if (Match(TokenKind.Word, "BEGIN"))
            {
                begin.AddRange(ParseBlock());
            }
            else if (Match(TokenKind.Word, "END"))
            {
                end.AddRange(ParseBlock());
            }
            else if (Check(TokenKind.LeftBrace))
            {
                rules.Add(new Rule(null, ParseBlock()));
            }
            else
            {
                Expr pattern = ParseExpression();
                List<Node> body = Check(TokenKind.LeftBrace)
                    ? ParseBlock()
                    : new List<Node> { new PrintNode(new List<Expr>()) };
                rules.Add(new Rule(pattern, body));
            }
            SkipTerminators();
        }
    }

    // ---- statements --------------------------------------------------------------------

    private List<Node> ParseBlock()
    {
        Expect(TokenKind.LeftBrace, "{");
        var body = new List<Node>();
        SkipTerminators();
        while (!Check(TokenKind.RightBrace))
        {
            if (AtEnd)
                throw new ShellUsageException("awk: missing '}'", 2);
            body.Add(ParseStatement());
            SkipTerminators();
        }
        Expect(TokenKind.RightBrace, "}");
        return body;
    }

    private Node ParseStatement()
    {
        if (Check(TokenKind.LeftBrace))
            return new BlockNode(ParseBlock());

        if (Check(TokenKind.Word))
        {
            string word = Current.Text;
            switch (word)
            {
                case "print":
                {
                    _i++;
                    var args = new List<Expr>();
                    while (!AtStatementEnd && !Check(TokenKind.Gt))
                    {
                        args.Add(ParseTernary(noGreater: true));
                        if (!Match(TokenKind.Comma))
                            break;
                        SkipNewlines();
                    }
                    if (Check(TokenKind.Gt))
                        throw new ShellUsageException("awk: redirecting print is not available on this host", 2);
                    return new PrintNode(args);
                }
                case "printf":
                {
                    _i++;
                    var args = new List<Expr>();
                    while (!AtStatementEnd && !Check(TokenKind.Gt))
                    {
                        args.Add(ParseTernary(noGreater: true));
                        if (!Match(TokenKind.Comma))
                            break;
                        SkipNewlines();
                    }
                    if (args.Count == 0)
                        throw new ShellUsageException("awk: printf needs a format", 2);
                    return new PrintfNode(args);
                }
                case "if":
                {
                    _i++;
                    Expect(TokenKind.LeftParen, "(");
                    Expr condition = ParseExpression();
                    Expect(TokenKind.RightParen, ")");
                    SkipTerminators();
                    List<Node> then = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                    var otherwise = new List<Node>();
                    int save = _i;
                    SkipTerminators();
                    if (Match(TokenKind.Word, "else"))
                    {
                        SkipTerminators();
                        otherwise = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                    }
                    else
                    {
                        _i = save;
                    }
                    return new IfNode(condition, then, otherwise);
                }
                case "while":
                {
                    _i++;
                    Expect(TokenKind.LeftParen, "(");
                    Expr condition = ParseExpression();
                    Expect(TokenKind.RightParen, ")");
                    SkipTerminators();
                    List<Node> body = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                    return new WhileNode(condition, body);
                }
                case "do":
                {
                    _i++;
                    SkipTerminators();
                    List<Node> body = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                    SkipTerminators();
                    if (!Match(TokenKind.Word, "while"))
                        throw new ShellUsageException("awk: expected 'while' after 'do'", 2);
                    Expect(TokenKind.LeftParen, "(");
                    Expr condition = ParseExpression();
                    Expect(TokenKind.RightParen, ")");
                    // do/while runs the body once, then loops.
                    var repeated = new List<Node>(body);
                    return new BlockNode(new List<Node> { new BlockNode(body), new WhileNode(condition, repeated) });
                }
                case "for":
                {
                    _i++;
                    Expect(TokenKind.LeftParen, "(");
                    // for (k in array)
                    if (Check(TokenKind.Word) && Peek(1).Kind == TokenKind.Word && Peek(1).Text == "in")
                    {
                        string variable = Current.Text;
                        _i += 2;
                        string array = Current.Text;
                        _i++;
                        Expect(TokenKind.RightParen, ")");
                        SkipTerminators();
                        List<Node> body = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                        return new ForInNode(variable, array, body);
                    }
                    Expr? initial = Check(TokenKind.Semicolon) ? null : ParseExpression();
                    Expect(TokenKind.Semicolon, ";");
                    Expr? condition = Check(TokenKind.Semicolon) ? null : ParseExpression();
                    Expect(TokenKind.Semicolon, ";");
                    Expr? step = Check(TokenKind.RightParen) ? null : ParseExpression();
                    Expect(TokenKind.RightParen, ")");
                    SkipTerminators();
                    List<Node> loopBody = Check(TokenKind.LeftBrace) ? ParseBlock() : new List<Node> { ParseStatement() };
                    return new ForNode(initial, condition, step, loopBody);
                }
                case "next": _i++; return new NextNode();
                case "break": _i++; return new BreakNode();
                case "continue": _i++; return new ContinueNode();
                case "exit": _i++; if (!AtStatementEnd) ParseExpression(); return new NextNode();
                case "delete":
                {
                    _i++;
                    string array = Current.Text;
                    _i++;
                    Expect(TokenKind.LeftBracket, "[");
                    Expr index = ParseExpression();
                    Expect(TokenKind.RightBracket, "]");
                    return new DeleteNode(array, index);
                }
            }
        }
        return new ExpressionNode(ParseExpression());
    }

    private bool AtStatementEnd => AtEnd || Check(TokenKind.Semicolon) || Check(TokenKind.Newline) || Check(TokenKind.RightBrace);

    // ---- expressions -------------------------------------------------------------------

    private Expr ParseExpression()
    {
        Expr first = ParseTernary(noGreater: false);
        if (!Check(TokenKind.Comma))
            return first;
        // A comma list only appears where the caller collects several values; the
        // statement parser handles print/printf, so here it is simply the last value.
        while (Match(TokenKind.Comma))
            first = ParseTernary(noGreater: false);
        return first;
    }

    private Expr ParseTernary(bool noGreater)
    {
        Expr condition = ParseAssignment(noGreater);
        if (!Match(TokenKind.Question))
            return condition;
        Expr then = ParseTernary(noGreater);
        Expect(TokenKind.Colon, ":");
        Expr otherwise = ParseTernary(noGreater);
        return new TernaryExpr(condition, then, otherwise);
    }

    private Expr ParseAssignment(bool noGreater)
    {
        Expr left = ParseOr(noGreater);
        if (Check(TokenKind.Assign))
        {
            string op = Current.Text;
            _i++;
            Expr value = ParseAssignment(noGreater);
            return new AssignExpr(left, op, value);
        }
        return left;
    }

    private Expr ParseOr(bool noGreater)
    {
        Expr left = ParseAnd(noGreater);
        while (Match(TokenKind.Or))
        {
            SkipNewlines();
            left = new BinaryExpr(left, "||", ParseAnd(noGreater));
        }
        return left;
    }

    private Expr ParseAnd(bool noGreater)
    {
        Expr left = ParseIn(noGreater);
        while (Match(TokenKind.And))
        {
            SkipNewlines();
            left = new BinaryExpr(left, "&&", ParseIn(noGreater));
        }
        return left;
    }

    private Expr ParseIn(bool noGreater)
    {
        Expr left = ParseMatch(noGreater);
        while (Check(TokenKind.Word) && Current.Text == "in")
        {
            _i++;
            string array = Current.Text;
            _i++;
            left = new InExpr(left, array);
        }
        return left;
    }

    private Expr ParseMatch(bool noGreater)
    {
        Expr left = ParseComparison(noGreater);
        while (Check(TokenKind.Match))
        {
            string op = Current.Text;
            _i++;
            left = new BinaryExpr(left, op, ParseComparison(noGreater));
        }
        return left;
    }

    private Expr ParseComparison(bool noGreater)
    {
        Expr left = ParseConcatenation(noGreater);
        while (Check(TokenKind.Compare) || (!noGreater && Check(TokenKind.Gt)))
        {
            string op = Current.Text;
            _i++;
            left = new BinaryExpr(left, op, ParseConcatenation(noGreater));
        }
        return left;
    }

    private Expr ParseConcatenation(bool noGreater)
    {
        Expr left = ParseAdditive(noGreater);
        while (StartsValue())
            left = new BinaryExpr(left, " ", ParseAdditive(noGreater));
        return left;
    }

    /// <summary>True when the next token could begin another value, which is what makes
    /// juxtaposition a concatenation.</summary>
    private bool StartsValue()
    {
        if (AtEnd) return false;
        return Current.Kind switch
        {
            TokenKind.Number or TokenKind.String or TokenKind.Dollar or TokenKind.LeftParen => true,
            TokenKind.Regex => true,
            TokenKind.Word => Current.Text is not ("in" or "else"),
            TokenKind.Not => true,
            _ => false,
        };
    }

    private Expr ParseAdditive(bool noGreater)
    {
        Expr left = ParseMultiplicative(noGreater);
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            string op = Current.Text;
            _i++;
            left = new BinaryExpr(left, op, ParseMultiplicative(noGreater));
        }
        return left;
    }

    private Expr ParseMultiplicative(bool noGreater)
    {
        Expr left = ParsePower(noGreater);
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            string op = Current.Text;
            _i++;
            left = new BinaryExpr(left, op, ParsePower(noGreater));
        }
        return left;
    }

    private Expr ParsePower(bool noGreater)
    {
        Expr left = ParseUnary(noGreater);
        if (Check(TokenKind.Caret))
        {
            _i++;
            return new BinaryExpr(left, "^", ParsePower(noGreater));   // right-associative
        }
        return left;
    }

    private Expr ParseUnary(bool noGreater)
    {
        if (Check(TokenKind.Not)) { _i++; return new UnaryExpr("!", ParseUnary(noGreater)); }
        if (Check(TokenKind.Minus)) { _i++; return new UnaryExpr("-", ParseUnary(noGreater)); }
        if (Check(TokenKind.Plus)) { _i++; return new UnaryExpr("+", ParseUnary(noGreater)); }
        if (Check(TokenKind.Increment))
        {
            bool increase = Current.Text == "++";
            _i++;
            Expr target = ParseUnary(noGreater);
            return new IncrementExpr(target, increase, Prefix: true);
        }
        return ParsePostfix(noGreater);
    }

    private Expr ParsePostfix(bool noGreater)
    {
        Expr value = ParsePrimary(noGreater);
        while (Check(TokenKind.Increment))
        {
            bool increase = Current.Text == "++";
            _i++;
            value = new IncrementExpr(value, increase, Prefix: false);
        }
        return value;
    }

    private Expr ParsePrimary(bool noGreater)
    {
        if (AtEnd)
            throw new ShellUsageException("awk: unexpected end of program", 2);
        Token token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _i++;
                return new NumberExpr(double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
            case TokenKind.String:
                _i++;
                return new StringExpr(token.Text);
            case TokenKind.Regex:
                _i++;
                return new RegexExpr(PosixRegex.Translate(token.Text, extended: true));
            case TokenKind.Dollar:
            {
                _i++;
                Expr index = ParsePrimary(noGreater);
                return new FieldExpr(index);
            }
            case TokenKind.LeftParen:
            {
                _i++;
                Expr inner = ParseExpression();
                Expect(TokenKind.RightParen, ")");
                return new GroupExpr(inner);
            }
            case TokenKind.Word:
            {
                _i++;
                string name = token.Text;
                if (Check(TokenKind.LeftParen) && !Current.SpaceBefore)
                {
                    _i++;
                    var args = new List<Expr>();
                    if (!Check(TokenKind.RightParen))
                    {
                        args.Add(ParseTernary(noGreater: false));
                        while (Match(TokenKind.Comma))
                            args.Add(ParseTernary(noGreater: false));
                    }
                    Expect(TokenKind.RightParen, ")");
                    return new CallExpr(name, args);
                }
                if (Check(TokenKind.LeftBracket))
                {
                    _i++;
                    Expr index = ParseExpression();
                    Expect(TokenKind.RightBracket, "]");
                    return new IndexExpr(name, index);
                }
                return new VariableExpr(name);
            }
            default:
                throw new ShellUsageException($"awk: syntax error at '{token.Text}'", 2);
        }
    }

    // ---- tokens -------------------------------------------------------------------------

    private enum TokenKind
    {
        Word, Number, String, Regex, Dollar, LeftParen, RightParen, LeftBrace, RightBrace,
        LeftBracket, RightBracket, Comma, Semicolon, Newline, Assign, Compare, Match, And, Or,
        Not, Plus, Minus, Star, Slash, Percent, Caret, Question, Colon, Increment, Gt,
    }

    private readonly record struct Token(TokenKind Kind, string Text, bool SpaceBefore);

    private bool AtEnd => _i >= _tokens.Count;
    private Token Current => _tokens[_i];
    private Token Peek(int ahead) => _i + ahead < _tokens.Count ? _tokens[_i + ahead] : new Token(TokenKind.Newline, string.Empty, false);
    private bool Check(TokenKind kind) => !AtEnd && Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind)) return false;
        _i++;
        return true;
    }

    private bool Match(TokenKind kind, string text)
    {
        if (!Check(kind) || Current.Text != text) return false;
        _i++;
        return true;
    }

    private void Expect(TokenKind kind, string what)
    {
        if (!Match(kind))
            throw new ShellUsageException($"awk: expected '{what}'", 2);
    }

    private void SkipTerminators()
    {
        while (!AtEnd && (Current.Kind == TokenKind.Newline || Current.Kind == TokenKind.Semicolon))
            _i++;
    }

    private void SkipNewlines()
    {
        while (!AtEnd && Current.Kind == TokenKind.Newline)
            _i++;
    }

    private static List<Token> Tokenize(string program)
    {
        var tokens = new List<Token>();
        bool space = false;
        for (int i = 0; i < program.Length; i++)
        {
            char c = program[i];
            if (c is ' ' or '\t' or '\r') { space = true; continue; }
            if (c == '\\' && i + 1 < program.Length && program[i + 1] == '\n') { i++; continue; }
            if (c == '#')
            {
                while (i < program.Length && program[i] != '\n') i++;
                continue;
            }
            if (c == '\n')
            {
                tokens.Add(new Token(TokenKind.Newline, "\n", space));
                space = false;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < program.Length && char.IsDigit(program[i + 1])))
            {
                int start = i;
                while (i < program.Length && (char.IsDigit(program[i]) || program[i] == '.' || program[i] is 'e' or 'E'
                       || ((program[i] == '+' || program[i] == '-') && i > start && (program[i - 1] is 'e' or 'E'))))
                    i++;
                tokens.Add(new Token(TokenKind.Number, program[start..i], space));
                i--; space = false;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < program.Length && (char.IsLetterOrDigit(program[i]) || program[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Word, program[start..i], space));
                i--; space = false;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < program.Length && program[i] != '"')
                {
                    if (program[i] == '\\' && i + 1 < program.Length)
                    {
                        i++;
                        sb.Append(program[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', '/' => '/', _ => program[i] });
                    }
                    else
                    {
                        sb.Append(program[i]);
                    }
                    i++;
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), space));
                space = false;
                continue;
            }
            if (c == '/' && RegexAllowedAfter(tokens))
            {
                var sb = new StringBuilder();
                i++;
                while (i < program.Length && program[i] != '/')
                {
                    if (program[i] == '\\' && i + 1 < program.Length)
                    {
                        sb.Append(program[i]).Append(program[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(program[i++]);
                }
                tokens.Add(new Token(TokenKind.Regex, sb.ToString(), space));
                space = false;
                continue;
            }

            string two = i + 1 < program.Length ? program.Substring(i, 2) : string.Empty;
            switch (two)
            {
                case "==" or "!=" or "<=" or ">=":
                    tokens.Add(new Token(TokenKind.Compare, two, space)); i++; space = false; continue;
                case "&&":
                    tokens.Add(new Token(TokenKind.And, two, space)); i++; space = false; continue;
                case "||":
                    tokens.Add(new Token(TokenKind.Or, two, space)); i++; space = false; continue;
                case "++" or "--":
                    tokens.Add(new Token(TokenKind.Increment, two, space)); i++; space = false; continue;
                case "+=" or "-=" or "*=" or "/=" or "%=" or "^=":
                    tokens.Add(new Token(TokenKind.Assign, two, space)); i++; space = false; continue;
                case "!~":
                    tokens.Add(new Token(TokenKind.Match, two, space)); i++; space = false; continue;
            }

            TokenKind kind = c switch
            {
                '$' => TokenKind.Dollar,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                '{' => TokenKind.LeftBrace,
                '}' => TokenKind.RightBrace,
                '[' => TokenKind.LeftBracket,
                ']' => TokenKind.RightBracket,
                ',' => TokenKind.Comma,
                ';' => TokenKind.Semicolon,
                '=' => TokenKind.Assign,
                '<' => TokenKind.Compare,
                '>' => TokenKind.Gt,
                '~' => TokenKind.Match,
                '!' => TokenKind.Not,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '%' => TokenKind.Percent,
                '^' => TokenKind.Caret,
                '?' => TokenKind.Question,
                ':' => TokenKind.Colon,
                _ => throw new ShellUsageException($"awk: unexpected character '{c}'", 2),
            };
            tokens.Add(new Token(kind, c.ToString(), space));
            space = false;
        }
        return tokens;
    }

    /// <summary>`/` starts a regular expression only where a value may start; after a value
    /// it is division. This is the classic awk/JavaScript ambiguity.</summary>
    private static bool RegexAllowedAfter(List<Token> tokens)
    {
        if (tokens.Count == 0)
            return true;
        Token last = tokens[^1];
        return last.Kind switch
        {
            TokenKind.Number or TokenKind.String or TokenKind.RightParen or TokenKind.RightBracket => false,
            TokenKind.Word => last.Text is "in" or "print" or "printf" or "if" or "while" or "return" or "case",
            TokenKind.Increment => false,
            _ => true,
        };
    }
}
