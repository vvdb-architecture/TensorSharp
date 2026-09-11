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

namespace TensorAgent.Core.Shell;

/// <summary>The script could not be parsed. The message is what <c>sh</c> would print, minus the line number prefix the caller adds.</summary>
public sealed class ShellSyntaxException : Exception
{
    public ShellSyntaxException(string message) : base(message) { }
}

// ---------------------------------------------------------------------------------
// AST. Deliberately plain: the interpreter walks it directly.
// ---------------------------------------------------------------------------------

internal sealed class CommandList
{
    public List<AndOrList> Items { get; } = new();
}

internal enum AndOrOp { And, Or }

internal sealed class AndOrList
{
    public List<Pipeline> Pipelines { get; } = new();
    /// <summary>Ops[i] joins Pipelines[i] and Pipelines[i + 1].</summary>
    public List<AndOrOp> Ops { get; } = new();
}

internal sealed class Pipeline
{
    public bool Negate { get; set; }
    public List<Command> Commands { get; } = new();
}

internal abstract class Command
{
    public List<Redirect> Redirects { get; } = new();
}

internal sealed class SimpleCommand : Command
{
    public List<Assignment> Assignments { get; } = new();
    public List<Word> Words { get; } = new();
}

internal sealed class Assignment
{
    public required string Name { get; init; }
    public Word? Value { get; init; }
    /// <summary>Set for <c>name=(a b c)</c>.</summary>
    public List<Word>? ArrayValue { get; init; }
    public bool Append { get; init; }
    /// <summary>Set for <c>name[i]=value</c>; the raw subscript text.</summary>
    public string? Subscript { get; init; }
}

internal sealed class SubshellCommand : Command
{
    public required CommandList Body { get; init; }
}

internal sealed class GroupCommand : Command
{
    public required CommandList Body { get; init; }
}

internal sealed class IfCommand : Command
{
    public List<(CommandList Condition, CommandList Body)> Branches { get; } = new();
    public CommandList? Else { get; set; }
}

internal sealed class ForCommand : Command
{
    public required string Variable { get; init; }
    /// <summary>Null means "$@".</summary>
    public List<Word>? Words { get; init; }
    public required CommandList Body { get; init; }
}

internal sealed class ArithForCommand : Command
{
    public required string Init { get; init; }
    public required string Condition { get; init; }
    public required string Step { get; init; }
    public required CommandList Body { get; init; }
}

internal sealed class WhileCommand : Command
{
    public bool Until { get; init; }
    public required CommandList Condition { get; init; }
    public required CommandList Body { get; init; }
}

internal sealed class CaseCommand : Command
{
    public required Word Subject { get; init; }
    public List<(List<Word> Patterns, CommandList Body)> Items { get; } = new();
}

internal sealed class FunctionCommand : Command
{
    public required string Name { get; init; }
    public required Command Body { get; init; }
}

/// <summary><c>[[ ... ]]</c>: the words between the brackets, operators included.</summary>
internal sealed class ConditionalCommand : Command
{
    public List<Word> Words { get; } = new();
}

/// <summary><c>(( expr ))</c>.</summary>
internal sealed class ArithmeticCommand : Command
{
    public required string Expression { get; init; }
}

internal sealed class Word
{
    public List<WordPart> Parts { get; } = new();

    /// <summary>The text as written, for <c>set -x</c> and error messages.</summary>
    public string Source { get; set; } = string.Empty;

    public static Word Literal(string text, bool quoted = true)
    {
        var w = new Word { Source = text };
        w.Parts.Add(new LiteralPart { Text = text, Quoted = quoted });
        return w;
    }

    public bool IsLiteral(out string text)
    {
        if (Parts.Count == 0)
        {
            text = string.Empty;
            return true;
        }
        var sb = new StringBuilder();
        foreach (WordPart part in Parts)
        {
            if (part is LiteralPart lit)
                sb.Append(lit.Text);
            else
            {
                text = string.Empty;
                return false;
            }
        }
        text = sb.ToString();
        return true;
    }
}

internal abstract class WordPart
{
    /// <summary>True inside quotes: no field splitting, no globbing of what this produces.</summary>
    public bool Quoted { get; set; }
}

internal sealed class LiteralPart : WordPart
{
    public required string Text { get; init; }
}

internal sealed class TildePart : WordPart
{
    public string User { get; init; } = string.Empty;
}

internal sealed class ParamPart : WordPart
{
    public required string Name { get; init; }
    /// <summary>"" / ":-" / "-" / ":=" / "=" / ":?" / "?" / ":+" / "+" / "#" / "##" / "%" / "%%" / "/" / "//" / ":" / "#len" / "!" / "^" / "^^" / "," / ",,"</summary>
    public string Op { get; init; } = string.Empty;
    public Word? Arg { get; init; }
    public Word? Arg2 { get; init; }
    /// <summary>Raw subscript text for arrays: "@", "*" or an index expression.</summary>
    public string? Subscript { get; init; }
    public bool InDoubleQuotes { get; init; }
}

internal sealed class CommandSubstPart : WordPart
{
    public required CommandList Body { get; init; }
}

internal sealed class ArithPart : WordPart
{
    public required string Expression { get; init; }
}

internal enum RedirectKind
{
    Output,      // >
    Append,      // >>
    Clobber,     // >|
    Input,       // <
    DupOutput,   // >&n
    DupInput,    // <&n
    OutputBoth,  // &> or >&file
    AppendBoth,  // &>>
    HereDoc,     // << / <<-
    HereString,  // <<<
    Close,       // >&- / <&-
}

internal sealed class Redirect
{
    public int Fd { get; set; }
    public RedirectKind Kind { get; set; }
    public Word? Target { get; set; }
    public int DupFd { get; set; } = -1;
    /// <summary>For here-documents: the body, already parsed for expansion (or literal when the delimiter was quoted).</summary>
    public Word? HereDocBody { get; set; }
    internal string? PendingDelimiter { get; set; }
    internal bool PendingStripTabs { get; set; }
    internal bool PendingQuoted { get; set; }
}

// ---------------------------------------------------------------------------------
// Parser. A hand-written recursive descent over the raw text; the lexer is folded in
// because a shell's tokens depend on context (a `}` is a keyword only where a
// command may start, `<<` reads lines that come after the current one).
// ---------------------------------------------------------------------------------

internal sealed class ShellParser
{
    private readonly string _s;
    private int _p;
    private readonly List<Redirect> _pendingHereDocs = new();
    private int _depth;

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi", "for", "in", "do", "done", "while", "until", "case", "esac", "function", "{", "}", "!", "[[", "]]",
    };

    private ShellParser(string source)
    {
        _s = source.Replace("\r\n", "\n");
    }

    public static CommandList Parse(string source)
    {
        var parser = new ShellParser(source ?? string.Empty);
        CommandList list = parser.ParseList();
        parser.SkipNewlines();
        if (!parser.AtEnd)
            throw new ShellSyntaxException($"syntax error near unexpected token '{parser.NextTokenForMessage()}'");
        return list;
    }

    /// <summary>Parse a here-document body or a `$(...)` text in expansion-only mode.</summary>
    internal static Word ParseHereDocBody(string body)
    {
        var parser = new ShellParser(body);
        var word = new Word { Source = body };
        parser.ParseParts(word.Parts, WordMode.HereDoc, '\0');
        return word;
    }

    // --- character-level helpers ---------------------------------------------------

    private bool AtEnd => _p >= _s.Length;
    private char Cur => _p < _s.Length ? _s[_p] : '\0';
    private char At(int offset) => _p + offset < _s.Length ? _s[_p + offset] : '\0';

    private string NextTokenForMessage()
    {
        int end = _p;
        while (end < _s.Length && !char.IsWhiteSpace(_s[end]) && end - _p < 16)
            end++;
        return end == _p ? (AtEnd ? "end of file" : Cur.ToString()) : _s.Substring(_p, end - _p);
    }

    private void SkipBlanks()
    {
        while (!AtEnd)
        {
            char c = Cur;
            if (c == ' ' || c == '\t')
                _p++;
            else if (c == '\\' && At(1) == '\n')
                _p += 2;
            else if (c == '#')
            {
                while (!AtEnd && Cur != '\n')
                    _p++;
            }
            else
                break;
        }
    }

    private void SkipNewlines()
    {
        while (true)
        {
            SkipBlanks();
            if (Cur == '\n')
            {
                _p++;
                ReadPendingHereDocs();
            }
            else
                break;
        }
    }

    private static bool IsWordTerminator(char c)
        => c is ' ' or '\t' or '\n' or ';' or '&' or '|' or '(' or ')' or '<' or '>' or '\0';

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>The raw text of the next word if it is a plain unquoted token, else null.</summary>
    private string? PeekPlainWord()
    {
        int i = _p;
        while (i < _s.Length && !IsWordTerminator(_s[i]))
        {
            char c = _s[i];
            if (c is '\'' or '"' or '\\' or '$' or '`')
                return null;
            i++;
        }
        return i == _p ? null : _s.Substring(_p, i - _p);
    }

    private bool PeekKeyword(string keyword)
    {
        if (keyword == "{" || keyword == "}" || keyword == "!" || keyword == "[[" || keyword == "]]")
        {
            if (string.CompareOrdinal(_s, _p, keyword, 0, keyword.Length) != 0)
                return false;
            return IsWordTerminator(At(keyword.Length));
        }
        return PeekPlainWord() == keyword;
    }

    private bool IsListTerminator()
    {
        if (AtEnd)
            return true;
        char c = Cur;
        if (c == ')')
            return true;
        if (c == ';' && At(1) == ';')
            return true;
        if (c == '}' && IsWordTerminator(At(1)))
            return true;
        string? word = PeekPlainWord();
        return word is "then" or "do" or "done" or "fi" or "elif" or "else" or "esac";
    }

    private void ExpectKeyword(string keyword)
    {
        SkipNewlines();
        if (!PeekKeyword(keyword))
            throw new ShellSyntaxException($"syntax error: expected '{keyword}' near '{NextTokenForMessage()}'");
        _p += keyword.Length;
    }

    private void ExpectChar(char c, string what)
    {
        SkipBlanks();
        if (Cur != c)
            throw new ShellSyntaxException($"syntax error: expected '{what}' near '{NextTokenForMessage()}'");
        _p++;
    }

    // --- lists -----------------------------------------------------------------------

    private CommandList ParseList()
    {
        if (++_depth > 200)
            throw new ShellSyntaxException("the script is nested too deeply");
        try
        {
            var list = new CommandList();
            while (true)
            {
                SkipNewlines();
                if (IsListTerminator())
                    break;

                list.Items.Add(ParseAndOr());

                SkipBlanks();
                if (Cur == ';')
                {
                    if (At(1) == ';')
                        break;
                    _p++;
                    continue;
                }
                if (Cur == '\n')
                {
                    _p++;
                    ReadPendingHereDocs();
                    continue;
                }
                if (Cur == '&')
                {
                    if (At(1) == '&')
                        throw new ShellSyntaxException("syntax error near unexpected token '&&'");
                    throw new ShellSyntaxException("background jobs ('&') are not supported by this host; run the command in the foreground");
                }
                if (IsListTerminator())
                    break;
                throw new ShellSyntaxException($"syntax error near unexpected token '{NextTokenForMessage()}'");
            }
            return list;
        }
        finally
        {
            _depth--;
        }
    }

    private AndOrList ParseAndOr()
    {
        var list = new AndOrList();
        list.Pipelines.Add(ParsePipeline());
        while (true)
        {
            SkipBlanks();
            if (Cur == '&' && At(1) == '&')
            {
                _p += 2;
                list.Ops.Add(AndOrOp.And);
            }
            else if (Cur == '|' && At(1) == '|')
            {
                _p += 2;
                list.Ops.Add(AndOrOp.Or);
            }
            else
                break;
            SkipNewlines();
            list.Pipelines.Add(ParsePipeline());
        }
        return list;
    }

    private Pipeline ParsePipeline()
    {
        var pipeline = new Pipeline();
        SkipBlanks();
        if (PeekKeyword("!"))
        {
            pipeline.Negate = true;
            _p++;
            SkipBlanks();
        }
        pipeline.Commands.Add(ParseCommand());
        while (true)
        {
            SkipBlanks();
            if (Cur == '|' && At(1) != '|')
            {
                _p++;
                if (Cur == '&')
                    _p++; // |& : stderr joins the pipe; the interpreter treats it as 2>&1 |
                SkipNewlines();
                pipeline.Commands.Add(ParseCommand());
            }
            else
                break;
        }
        return pipeline;
    }

    // --- commands ---------------------------------------------------------------------

    private Command ParseCommand()
    {
        SkipBlanks();
        if (AtEnd)
            throw new ShellSyntaxException("syntax error: unexpected end of file");

        Command command;
        if (Cur == '(' && At(1) == '(')
            command = ParseArithmeticCommand();
        else if (Cur == '(')
        {
            _p++;
            CommandList body = ParseList();
            SkipNewlines();
            ExpectChar(')', ")");
            command = new SubshellCommand { Body = body };
        }
        else if (PeekKeyword("{"))
        {
            _p++;
            CommandList body = ParseList();
            ExpectKeyword("}");
            command = new GroupCommand { Body = body };
        }
        else if (PeekKeyword("if"))
            command = ParseIf();
        else if (PeekKeyword("for"))
            command = ParseFor();
        else if (PeekKeyword("while") || PeekKeyword("until"))
            command = ParseWhile();
        else if (PeekKeyword("case"))
            command = ParseCase();
        else if (PeekKeyword("function"))
        {
            _p += "function".Length;
            SkipBlanks();
            string? name = PeekPlainWord();
            if (name == null)
                throw new ShellSyntaxException("syntax error: 'function' needs a name");
            _p += name.Length;
            SkipBlanks();
            if (Cur == '(' && At(1) == ')')
                _p += 2;
            SkipNewlines();
            command = new FunctionCommand { Name = name, Body = ParseCommand() };
            return command;
        }
        else if (PeekKeyword("[["))
            command = ParseConditional();
        else
        {
            string? word = PeekPlainWord();
            if (word != null && ReservedWords.Contains(word) && word != "in")
                throw new ShellSyntaxException($"syntax error near unexpected token '{word}'");
            return ParseSimpleCommand();
        }

        ParseTrailingRedirects(command.Redirects);
        return command;
    }

    private void ParseTrailingRedirects(List<Redirect> redirects)
    {
        while (true)
        {
            SkipBlanks();
            if (!TryParseRedirect(redirects))
                break;
        }
    }

    private Command ParseArithmeticCommand()
    {
        _p += 2;
        string expr = ReadBalancedUntilDoubleParen();
        return new ArithmeticCommand { Expression = expr };
    }

    /// <summary>Reads up to the matching "))", leaving the position after it.</summary>
    private string ReadBalancedUntilDoubleParen()
    {
        int depth = 0;
        int start = _p;
        while (!AtEnd)
        {
            char c = Cur;
            if (c == '(')
                depth++;
            else if (c == ')')
            {
                if (depth == 0 && At(1) == ')')
                {
                    string text = _s.Substring(start, _p - start);
                    _p += 2;
                    return text;
                }
                if (depth > 0)
                    depth--;
            }
            _p++;
        }
        throw new ShellSyntaxException("syntax error: missing '))'");
    }

    private Command ParseIf()
    {
        _p += 2;
        var cmd = new IfCommand();
        CommandList condition = ParseList();
        ExpectKeyword("then");
        CommandList body = ParseList();
        cmd.Branches.Add((condition, body));
        while (true)
        {
            SkipNewlines();
            if (PeekKeyword("elif"))
            {
                _p += 4;
                CommandList c = ParseList();
                ExpectKeyword("then");
                CommandList b = ParseList();
                cmd.Branches.Add((c, b));
            }
            else if (PeekKeyword("else"))
            {
                _p += 4;
                cmd.Else = ParseList();
            }
            else
                break;
        }
        ExpectKeyword("fi");
        return cmd;
    }

    private Command ParseFor()
    {
        _p += 3;
        SkipBlanks();
        if (Cur == '(' && At(1) == '(')
        {
            _p += 2;
            string header = ReadBalancedUntilDoubleParen();
            string[] parts = header.Split(';');
            if (parts.Length != 3)
                throw new ShellSyntaxException("syntax error: for (( init; condition; step ))");
            SkipBlanks();
            if (Cur == ';')
                _p++;
            ExpectKeyword("do");
            CommandList arithBody = ParseList();
            ExpectKeyword("done");
            return new ArithForCommand { Init = parts[0].Trim(), Condition = parts[1].Trim(), Step = parts[2].Trim(), Body = arithBody };
        }

        string? variable = PeekPlainWord();
        if (variable == null || !IsNameStart(variable[0]))
            throw new ShellSyntaxException("syntax error: 'for' needs a variable name");
        _p += variable.Length;
        SkipBlanks();

        List<Word>? words = null;
        if (Cur == '\n')
        {
            SkipNewlines();
        }
        if (PeekKeyword("in"))
        {
            _p += 2;
            words = new List<Word>();
            while (true)
            {
                SkipBlanks();
                if (AtEnd || Cur == ';' || Cur == '\n')
                    break;
                words.AddRange(ParseWordVariants());
            }
        }
        SkipBlanks();
        if (Cur == ';')
            _p++;
        ExpectKeyword("do");
        CommandList body = ParseList();
        ExpectKeyword("done");
        return new ForCommand { Variable = variable, Words = words, Body = body };
    }

    private Command ParseWhile()
    {
        bool until = PeekKeyword("until");
        _p += 5;
        CommandList condition = ParseList();
        ExpectKeyword("do");
        CommandList body = ParseList();
        ExpectKeyword("done");
        return new WhileCommand { Until = until, Condition = condition, Body = body };
    }

    private Command ParseCase()
    {
        _p += 4;
        SkipBlanks();
        Word subject = ParseWord(WordMode.Normal);
        ExpectKeyword("in");
        var cmd = new CaseCommand { Subject = subject };
        while (true)
        {
            SkipNewlines();
            if (PeekKeyword("esac"))
            {
                _p += 4;
                break;
            }
            if (AtEnd)
                throw new ShellSyntaxException("syntax error: expected 'esac'");
            SkipBlanks();
            if (Cur == '(')
                _p++;
            var patterns = new List<Word>();
            while (true)
            {
                SkipBlanks();
                patterns.Add(ParseWord(WordMode.Normal));
                SkipBlanks();
                if (Cur == '|')
                {
                    _p++;
                    continue;
                }
                break;
            }
            ExpectChar(')', ")");
            CommandList body = ParseList();
            SkipNewlines();
            if (Cur == ';' && At(1) == ';')
            {
                _p += 2;
                if (Cur == '&')
                    _p++;
            }
            cmd.Items.Add((patterns, body));
        }
        return cmd;
    }

    private Command ParseConditional()
    {
        _p += 2;
        var cmd = new ConditionalCommand();
        while (true)
        {
            SkipBlanks();
            if (Cur == '\n')
            {
                _p++;
                continue;
            }
            if (AtEnd)
                throw new ShellSyntaxException("syntax error: expected ']]'");
            if (PeekKeyword("]]"))
            {
                _p += 2;
                break;
            }
            if (Cur == '&' && At(1) == '&')
            {
                _p += 2;
                cmd.Words.Add(Word.Literal("&&", quoted: false));
                continue;
            }
            if (Cur == '|' && At(1) == '|')
            {
                _p += 2;
                cmd.Words.Add(Word.Literal("||", quoted: false));
                continue;
            }
            if (Cur == '(' || Cur == ')')
            {
                cmd.Words.Add(Word.Literal(Cur.ToString(), quoted: false));
                _p++;
                continue;
            }
            if (Cur == '<' || Cur == '>')
            {
                cmd.Words.Add(Word.Literal(Cur.ToString(), quoted: false));
                _p++;
                continue;
            }
            cmd.Words.Add(ParseWord(WordMode.Conditional));
        }
        return cmd;
    }

    private Command ParseSimpleCommand()
    {
        var cmd = new SimpleCommand();
        bool assignmentsAllowed = true;
        while (true)
        {
            SkipBlanks();
            if (AtEnd)
                break;
            char c = Cur;
            if (c is '\n' or ';' or '|' or ')')
                break;
            if (c == '&')
                break;
            if (c == '(')
            {
                if (cmd.Words.Count == 0 && cmd.Assignments.Count == 0)
                    throw new ShellSyntaxException("syntax error near unexpected token '('");
                break;
            }

            if (TryParseRedirect(cmd.Redirects))
                continue;

            if (assignmentsAllowed && TryParseAssignment(out Assignment? assignment))
            {
                cmd.Assignments.Add(assignment!);
                continue;
            }

            // NAME () { ... }
            if (cmd.Words.Count == 0 && cmd.Assignments.Count == 0)
            {
                string? name = PeekPlainWord();
                if (name != null && IsNameStart(name[0]) && name.All(IsNameChar))
                {
                    int save = _p;
                    _p += name.Length;
                    SkipBlanks();
                    if (Cur == '(' && At(1) == ')')
                    {
                        _p += 2;
                        SkipNewlines();
                        return new FunctionCommand { Name = name, Body = ParseCommand() };
                    }
                    _p = save;
                }
            }

            cmd.Words.AddRange(ParseWordVariants());
            assignmentsAllowed = false;
        }

        if (cmd.Words.Count == 0 && cmd.Assignments.Count == 0 && cmd.Redirects.Count == 0)
            throw new ShellSyntaxException($"syntax error near unexpected token '{NextTokenForMessage()}'");
        return cmd;
    }

    private bool TryParseAssignment(out Assignment? assignment)
    {
        assignment = null;
        int i = _p;
        if (i >= _s.Length || !IsNameStart(_s[i]))
            return false;
        while (i < _s.Length && IsNameChar(_s[i]))
            i++;
        string name = _s.Substring(_p, i - _p);

        string? subscript = null;
        if (i < _s.Length && _s[i] == '[')
        {
            int close = _s.IndexOf(']', i);
            if (close < 0)
                return false;
            subscript = _s.Substring(i + 1, close - i - 1);
            i = close + 1;
        }

        bool append = false;
        if (i < _s.Length && _s[i] == '+' && i + 1 < _s.Length && _s[i + 1] == '=')
        {
            append = true;
            i += 2;
        }
        else if (i < _s.Length && _s[i] == '=')
            i++;
        else
            return false;

        _p = i;
        if (Cur == '(' && subscript == null)
        {
            _p++;
            var items = new List<Word>();
            while (true)
            {
                SkipNewlines();
                if (Cur == ')')
                {
                    _p++;
                    break;
                }
                if (AtEnd)
                    throw new ShellSyntaxException("syntax error: expected ')' to close the array");
                items.AddRange(ParseWordVariants());
            }
            assignment = new Assignment { Name = name, ArrayValue = items, Append = append };
            return true;
        }

        Word value = ParseWord(WordMode.Assignment);
        assignment = new Assignment { Name = name, Value = value, Append = append, Subscript = subscript };
        return true;
    }

    // --- redirections -------------------------------------------------------------------

    private bool TryParseRedirect(List<Redirect> redirects)
    {
        int save = _p;
        int fd = -1;
        int i = _p;
        while (i < _s.Length && char.IsAsciiDigit(_s[i]))
            i++;
        if (i > _p && i < _s.Length && (_s[i] == '<' || _s[i] == '>'))
        {
            fd = int.Parse(_s.AsSpan(_p, i - _p));
            _p = i;
        }

        var redirect = new Redirect();
        char c = Cur;
        if (c == '&' && At(1) == '>')
        {
            if (fd >= 0)
            {
                _p = save;
                return false;
            }
            if (At(2) == '>')
            {
                redirect.Kind = RedirectKind.AppendBoth;
                _p += 3;
            }
            else
            {
                redirect.Kind = RedirectKind.OutputBoth;
                _p += 2;
            }
            redirect.Fd = 1;
        }
        else if (c == '>')
        {
            redirect.Fd = fd < 0 ? 1 : fd;
            if (At(1) == '>')
            {
                redirect.Kind = RedirectKind.Append;
                _p += 2;
            }
            else if (At(1) == '|')
            {
                redirect.Kind = RedirectKind.Clobber;
                _p += 2;
            }
            else if (At(1) == '&')
            {
                _p += 2;
                SkipBlanks();
                if (char.IsAsciiDigit(Cur))
                {
                    int j = _p;
                    while (j < _s.Length && char.IsAsciiDigit(_s[j]))
                        j++;
                    if (IsWordTerminator(At(j - _p)))
                    {
                        redirect.Kind = RedirectKind.DupOutput;
                        redirect.DupFd = int.Parse(_s.AsSpan(_p, j - _p));
                        _p = j;
                        redirects.Add(redirect);
                        return true;
                    }
                }
                if (Cur == '-')
                {
                    _p++;
                    redirect.Kind = RedirectKind.Close;
                    redirects.Add(redirect);
                    return true;
                }
                // >&file is &>file
                redirect.Kind = RedirectKind.OutputBoth;
            }
            else
            {
                redirect.Kind = RedirectKind.Output;
                _p++;
            }
        }
        else if (c == '<')
        {
            redirect.Fd = fd < 0 ? 0 : fd;
            if (At(1) == '<')
            {
                if (At(2) == '<')
                {
                    redirect.Kind = RedirectKind.HereString;
                    _p += 3;
                }
                else
                {
                    bool strip = At(2) == '-';
                    _p += strip ? 3 : 2;
                    SkipBlanks();
                    (string delimiter, bool quoted) = ReadHereDocDelimiter();
                    redirect.Kind = RedirectKind.HereDoc;
                    redirect.PendingDelimiter = delimiter;
                    redirect.PendingQuoted = quoted;
                    redirect.PendingStripTabs = strip;
                    _pendingHereDocs.Add(redirect);
                    redirects.Add(redirect);
                    return true;
                }
            }
            else if (At(1) == '&')
            {
                _p += 2;
                SkipBlanks();
                if (Cur == '-')
                {
                    _p++;
                    redirect.Kind = RedirectKind.Close;
                    redirects.Add(redirect);
                    return true;
                }
                int j = _p;
                while (j < _s.Length && char.IsAsciiDigit(_s[j]))
                    j++;
                if (j == _p)
                    throw new ShellSyntaxException("syntax error: '<&' needs a file descriptor number");
                redirect.Kind = RedirectKind.DupInput;
                redirect.DupFd = int.Parse(_s.AsSpan(_p, j - _p));
                _p = j;
                redirects.Add(redirect);
                return true;
            }
            else
            {
                redirect.Kind = RedirectKind.Input;
                _p++;
            }
        }
        else
        {
            _p = save;
            return false;
        }

        SkipBlanks();
        if (AtEnd || IsWordTerminator(Cur))
            throw new ShellSyntaxException("syntax error near unexpected token 'newline'");
        redirect.Target = ParseWord(WordMode.Normal);
        redirects.Add(redirect);
        return true;
    }

    private (string Delimiter, bool Quoted) ReadHereDocDelimiter()
    {
        var sb = new StringBuilder();
        bool quoted = false;
        while (!AtEnd && !IsWordTerminator(Cur))
        {
            char c = Cur;
            if (c == '\'')
            {
                quoted = true;
                int close = _s.IndexOf('\'', _p + 1);
                if (close < 0)
                    throw new ShellSyntaxException("syntax error: unterminated quote in here-document delimiter");
                sb.Append(_s, _p + 1, close - _p - 1);
                _p = close + 1;
            }
            else if (c == '"')
            {
                quoted = true;
                int close = _s.IndexOf('"', _p + 1);
                if (close < 0)
                    throw new ShellSyntaxException("syntax error: unterminated quote in here-document delimiter");
                sb.Append(_s, _p + 1, close - _p - 1);
                _p = close + 1;
            }
            else if (c == '\\')
            {
                quoted = true;
                if (At(1) != '\0')
                    sb.Append(At(1));
                _p += 2;
            }
            else
            {
                sb.Append(c);
                _p++;
            }
        }
        if (sb.Length == 0)
            throw new ShellSyntaxException("syntax error: '<<' needs a delimiter");
        return (sb.ToString(), quoted);
    }

    private void ReadPendingHereDocs()
    {
        if (_pendingHereDocs.Count == 0)
            return;
        List<Redirect> pending = new(_pendingHereDocs);
        _pendingHereDocs.Clear();
        foreach (Redirect redirect in pending)
        {
            var body = new StringBuilder();
            string delimiter = redirect.PendingDelimiter!;
            while (!AtEnd)
            {
                int eol = _s.IndexOf('\n', _p);
                string line = eol < 0 ? _s.Substring(_p) : _s.Substring(_p, eol - _p);
                _p = eol < 0 ? _s.Length : eol + 1;
                string compare = redirect.PendingStripTabs ? line.TrimStart('\t') : line;
                if (compare == delimiter)
                    break;
                body.Append(compare).Append('\n');
            }
            redirect.HereDocBody = redirect.PendingQuoted
                ? Word.Literal(body.ToString())
                : ParseHereDocBody(body.ToString());
        }
    }

    // --- words --------------------------------------------------------------------------

    internal enum WordMode
    {
        Normal,
        Assignment,   // like Normal but a leading ~ after ':' also expands (ignored here) and no brace expansion
        Conditional,  // inside [[ ]]: no glob later, same lexing
        DoubleQuote,
        HereDoc,
        Brace,        // inside ${...}: '}' terminates, whitespace does not
    }

    /// <summary>A word, with brace expansion applied: <c>a{b,c}</c> becomes two words.</summary>
    private List<Word> ParseWordVariants()
    {
        int start = _p;
        Word first = ParseWord(WordMode.Normal);
        string raw = _s.Substring(start, _p - start);
        if (raw.IndexOf('{') < 0)
            return new List<Word> { first };

        List<string> variants = BraceExpansion.Expand(raw);
        if (variants.Count == 1 && variants[0] == raw)
            return new List<Word> { first };

        var words = new List<Word>(variants.Count);
        foreach (string variant in variants)
        {
            var sub = new ShellParser(variant);
            Word w = sub.ParseWord(WordMode.Normal);
            if (!sub.AtEnd)
                w = first; // something exotic; keep the unexpanded word rather than guessing
            words.Add(w);
        }
        return words;
    }

    private Word ParseWord(WordMode mode)
    {
        int start = _p;
        var word = new Word();
        ParseParts(word.Parts, mode, '\0');
        word.Source = _s.Substring(start, _p - start);
        if (word.Parts.Count == 0 && mode != WordMode.Assignment)
            throw new ShellSyntaxException($"syntax error near unexpected token '{NextTokenForMessage()}'");
        return word;
    }

    private void ParseParts(List<WordPart> parts, WordMode mode, char terminator)
    {
        var literal = new StringBuilder();
        bool quotedLiteral = mode is WordMode.DoubleQuote or WordMode.HereDoc;

        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new LiteralPart { Text = literal.ToString(), Quoted = quotedLiteral });
                literal.Clear();
            }
        }

        while (!AtEnd)
        {
            char c = Cur;
            if (mode == WordMode.DoubleQuote)
            {
                if (c == '"')
                    break;
            }
            else if (mode == WordMode.Brace)
            {
                if (c == '}' || c == terminator)
                    break;
            }
            else if (mode == WordMode.HereDoc)
            {
                // nothing terminates but the end of the text
            }
            else if (IsWordTerminator(c))
                break;

            switch (c)
            {
                case '\'' when mode is not (WordMode.DoubleQuote or WordMode.HereDoc):
                {
                    Flush();
                    int close = _s.IndexOf('\'', _p + 1);
                    if (close < 0)
                        throw new ShellSyntaxException("unexpected EOF while looking for matching `''");
                    parts.Add(new LiteralPart { Text = _s.Substring(_p + 1, close - _p - 1), Quoted = true });
                    _p = close + 1;
                    break;
                }
                case '"' when mode is not (WordMode.DoubleQuote or WordMode.HereDoc):
                {
                    Flush();
                    _p++;
                    int before = parts.Count;
                    ParseParts(parts, WordMode.DoubleQuote, '"');
                    if (Cur != '"')
                        throw new ShellSyntaxException("unexpected EOF while looking for matching `\"'");
                    _p++;
                    if (parts.Count == before)
                        parts.Add(new LiteralPart { Text = string.Empty, Quoted = true });
                    break;
                }
                case '\\':
                {
                    char next = At(1);
                    if (next == '\0')
                    {
                        literal.Append('\\');
                        _p++;
                        break;
                    }
                    if (next == '\n')
                    {
                        _p += 2;
                        break;
                    }
                    if (mode is WordMode.DoubleQuote or WordMode.HereDoc)
                    {
                        bool special = next is '$' or '`' or '\\' || (mode == WordMode.DoubleQuote && next == '"');
                        if (special)
                        {
                            literal.Append(next);
                            _p += 2;
                        }
                        else
                        {
                            literal.Append('\\');
                            _p++;
                        }
                        break;
                    }
                    Flush();
                    parts.Add(new LiteralPart { Text = next.ToString(), Quoted = true });
                    _p += 2;
                    break;
                }
                case '$':
                    Flush();
                    ParseDollar(parts, mode is WordMode.DoubleQuote or WordMode.HereDoc);
                    break;
                case '`':
                    Flush();
                    ParseBackticks(parts, mode is WordMode.DoubleQuote or WordMode.HereDoc);
                    break;
                case '~' when parts.Count == 0 && literal.Length == 0 && mode is WordMode.Normal or WordMode.Assignment or WordMode.Conditional:
                {
                    int j = _p + 1;
                    while (j < _s.Length && (char.IsAsciiLetterOrDigit(_s[j]) || _s[j] is '_' or '-' or '.'))
                        j++;
                    if (j >= _s.Length || _s[j] == '/' || IsWordTerminator(_s[j]) || (mode == WordMode.Assignment && _s[j] == ':'))
                    {
                        parts.Add(new TildePart { User = _s.Substring(_p + 1, j - _p - 1) });
                        _p = j;
                    }
                    else
                    {
                        literal.Append('~');
                        _p++;
                    }
                    break;
                }
                default:
                    literal.Append(c);
                    _p++;
                    break;
            }
        }
        Flush();
    }

    private void ParseDollar(List<WordPart> parts, bool quoted)
    {
        _p++; // $
        char c = Cur;
        if (c == '(' && At(1) == '(')
        {
            // $(( ... )) — but "$((" could also be "$( (subshell) )"; bash resolves the
            // same way we do: try arithmetic first.
            int save = _p;
            _p += 2;
            try
            {
                string expr = ReadBalancedUntilDoubleParen();
                parts.Add(new ArithPart { Expression = expr, Quoted = quoted });
                return;
            }
            catch (ShellSyntaxException)
            {
                _p = save;
            }
        }
        if (c == '(')
        {
            _p++;
            CommandList body = ParseList();
            SkipNewlines();
            if (Cur != ')')
                throw new ShellSyntaxException("unexpected EOF while looking for matching `)'");
            _p++;
            parts.Add(new CommandSubstPart { Body = body, Quoted = quoted });
            return;
        }
        if (c == '{')
        {
            _p++;
            parts.Add(ParseBracedParam(quoted));
            if (Cur != '}')
                throw new ShellSyntaxException("bad substitution: missing '}'");
            _p++;
            return;
        }
        if (IsNameStart(c))
        {
            int j = _p;
            while (j < _s.Length && IsNameChar(_s[j]))
                j++;
            parts.Add(new ParamPart { Name = _s.Substring(_p, j - _p), Quoted = quoted, InDoubleQuotes = quoted });
            _p = j;
            return;
        }
        if (char.IsAsciiDigit(c) || c is '@' or '*' or '#' or '?' or '$' or '!' or '-')
        {
            parts.Add(new ParamPart { Name = c.ToString(), Quoted = quoted, InDoubleQuotes = quoted });
            _p++;
            return;
        }
        parts.Add(new LiteralPart { Text = "$", Quoted = quoted });
    }

    private ParamPart ParseBracedParam(bool quoted)
    {
        string op = string.Empty;
        if (Cur == '#' && At(1) != '}' && At(1) != '\0')
        {
            // ${#name} / ${#name[@]} / ${#@}
            _p++;
            string n = ReadParamName();
            string? sub = ReadSubscript();
            return new ParamPart { Name = n, Op = "#len", Subscript = sub, Quoted = quoted, InDoubleQuotes = quoted };
        }
        if (Cur == '!' && At(1) != '}')
        {
            _p++;
            string n = ReadParamName();
            return new ParamPart { Name = n, Op = "!", Quoted = quoted, InDoubleQuotes = quoted };
        }

        string name = ReadParamName();
        string? subscript = ReadSubscript();
        if (Cur == '}')
            return new ParamPart { Name = name, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };

        char c = Cur;
        if (c == ':')
        {
            char n = At(1);
            if (n is '-' or '=' or '?' or '+')
            {
                op = ":" + n;
                _p += 2;
                Word arg = ParseBraceArg('}');
                return new ParamPart { Name = name, Op = op, Arg = arg, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };
            }
            // substring ${name:offset[:length]}
            _p++;
            Word offset = ParseBraceArg(':');
            Word? length = null;
            if (Cur == ':')
            {
                _p++;
                length = ParseBraceArg('}');
            }
            return new ParamPart { Name = name, Op = ":", Arg = offset, Arg2 = length, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };
        }
        if (c is '-' or '=' or '?' or '+')
        {
            op = c.ToString();
            _p++;
            Word arg = ParseBraceArg('}');
            return new ParamPart { Name = name, Op = op, Arg = arg, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };
        }
        if (c is '#' or '%' or '^' or ',')
        {
            op = c.ToString();
            _p++;
            if (Cur == c)
            {
                op += c;
                _p++;
            }
            Word arg = ParseBraceArg('}');
            return new ParamPart { Name = name, Op = op, Arg = arg, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };
        }
        if (c == '/')
        {
            op = "/";
            _p++;
            if (Cur == '/')
            {
                op = "//";
                _p++;
            }
            Word pattern = ParseBraceArg('/');
            Word? replacement = null;
            if (Cur == '/')
            {
                _p++;
                replacement = ParseBraceArg('}');
            }
            return new ParamPart { Name = name, Op = op, Arg = pattern, Arg2 = replacement, Subscript = subscript, Quoted = quoted, InDoubleQuotes = quoted };
        }
        throw new ShellSyntaxException($"bad substitution: '${{{name}{c}'");
    }

    private string ReadParamName()
    {
        char c = Cur;
        if (IsNameStart(c))
        {
            int j = _p;
            while (j < _s.Length && IsNameChar(_s[j]))
                j++;
            string name = _s.Substring(_p, j - _p);
            _p = j;
            return name;
        }
        if (char.IsAsciiDigit(c))
        {
            int j = _p;
            while (j < _s.Length && char.IsAsciiDigit(_s[j]))
                j++;
            string name = _s.Substring(_p, j - _p);
            _p = j;
            return name;
        }
        if (c is '@' or '*' or '#' or '?' or '$' or '!' or '-')
        {
            _p++;
            return c.ToString();
        }
        throw new ShellSyntaxException("bad substitution");
    }

    private string? ReadSubscript()
    {
        if (Cur != '[')
            return null;
        int close = _s.IndexOf(']', _p);
        if (close < 0)
            throw new ShellSyntaxException("bad substitution: missing ']'");
        string sub = _s.Substring(_p + 1, close - _p - 1);
        _p = close + 1;
        return sub;
    }

    private Word ParseBraceArg(char terminator)
    {
        var word = new Word();
        int start = _p;
        ParseParts(word.Parts, WordMode.Brace, terminator);
        word.Source = _s.Substring(start, _p - start);
        return word;
    }

    private void ParseBackticks(List<WordPart> parts, bool quoted)
    {
        _p++; // `
        var inner = new StringBuilder();
        while (!AtEnd)
        {
            char c = Cur;
            if (c == '`')
            {
                _p++;
                CommandList body = Parse(inner.ToString());
                parts.Add(new CommandSubstPart { Body = body, Quoted = quoted });
                return;
            }
            if (c == '\\' && At(1) is '`' or '\\' or '$')
            {
                inner.Append(At(1));
                _p += 2;
                continue;
            }
            inner.Append(c);
            _p++;
        }
        throw new ShellSyntaxException("unexpected EOF while looking for matching '`'");
    }
}

/// <summary>
/// Bash brace expansion on raw word text: <c>a{b,c}d</c> → <c>abd acd</c>,
/// <c>{1..3}</c> → <c>1 2 3</c>. Runs before every other expansion, on text, which
/// is why it lives outside the parser proper. Quoted regions and <c>${</c> are left
/// alone.
/// </summary>
internal static class BraceExpansion
{
    public static List<string> Expand(string text)
    {
        var results = new List<string>();
        ExpandInto(text, results, 0);
        return results;
    }

    private static void ExpandInto(string text, List<string> results, int depth)
    {
        if (depth > 16 || results.Count > 4096)
        {
            results.Add(text);
            return;
        }

        int open = FindOpen(text, out int close, out List<int> commas);
        if (open < 0)
        {
            results.Add(text);
            return;
        }

        string prefix = text.Substring(0, open);
        string suffix = text.Substring(close + 1);
        string inner = text.Substring(open + 1, close - open - 1);

        var alternatives = new List<string>();
        if (commas.Count == 0)
        {
            if (!TryRange(inner, alternatives))
            {
                // A lone {x} is literal in bash.
                foreach (string rest in Expand(suffix))
                    results.Add(prefix + "{" + inner + "}" + rest);
                return;
            }
        }
        else
        {
            int last = open + 1;
            foreach (int comma in commas)
            {
                alternatives.Add(text.Substring(last, comma - last));
                last = comma + 1;
            }
            alternatives.Add(text.Substring(last, close - last));
        }

        foreach (string alternative in alternatives)
        {
            foreach (string expandedAlternative in Expand(alternative))
            {
                ExpandInto(prefix + expandedAlternative + suffix, results, depth + 1);
            }
        }
    }

    private static bool TryRange(string inner, List<string> alternatives)
    {
        int dots = inner.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0)
            return false;
        string a = inner.Substring(0, dots);
        string b = inner.Substring(dots + 2);
        int step = 1;
        int stepDots = b.IndexOf("..", StringComparison.Ordinal);
        if (stepDots > 0)
        {
            if (!int.TryParse(b.AsSpan(stepDots + 2), out step) || step == 0)
                return false;
            b = b.Substring(0, stepDots);
            step = Math.Abs(step);
        }
        if (int.TryParse(a, out int from) && int.TryParse(b, out int to))
        {
            int width = (a.StartsWith('0') || b.StartsWith('0')) && (a.Length > 1 || b.Length > 1) ? Math.Max(a.Length, b.Length) : 0;
            if (Math.Abs((long)to - from) / step > 10000)
                return false;
            if (from <= to)
                for (int i = from; i <= to; i += step)
                    alternatives.Add(i.ToString().PadLeft(width, '0'));
            else
                for (int i = from; i >= to; i -= step)
                    alternatives.Add(i.ToString().PadLeft(width, '0'));
            return true;
        }
        if (a.Length == 1 && b.Length == 1 && char.IsAsciiLetter(a[0]) && char.IsAsciiLetter(b[0]))
        {
            if (a[0] <= b[0])
                for (char c = a[0]; c <= b[0]; c = (char)(c + step))
                    alternatives.Add(c.ToString());
            else
                for (char c = a[0]; c >= b[0]; c = (char)(c - step))
                    alternatives.Add(c.ToString());
            return true;
        }
        return false;
    }

    /// <summary>The first unquoted `{` that has a matching `}`; commas at its top level.</summary>
    private static int FindOpen(string text, out int close, out List<int> commas)
    {
        close = -1;
        commas = new List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '\'')
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0)
                    return -1;
                i = end;
                continue;
            }
            if (c == '"')
            {
                int end = FindClosingDoubleQuote(text, i + 1);
                if (end < 0)
                    return -1;
                i = end;
                continue;
            }
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int end = MatchBrace(text, i + 1, null);
                if (end < 0)
                    return -1;
                i = end;
                continue;
            }
            if (c == '{')
            {
                var found = new List<int>();
                int end = MatchBrace(text, i, found);
                if (end < 0)
                    return -1;
                close = end;
                commas = found;
                return i;
            }
        }
        return -1;
    }

    private static int FindClosingDoubleQuote(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '"')
                return i;
        }
        return -1;
    }

    private static int MatchBrace(string text, int open, List<int>? commas)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '\'')
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0)
                    return -1;
                i = end;
                continue;
            }
            if (c == '"')
            {
                int end = FindClosingDoubleQuote(text, i + 1);
                if (end < 0)
                    return -1;
                i = end;
                continue;
            }
            if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
            else if (c == ',' && depth == 1)
                commas?.Add(i);
        }
        return -1;
    }
}
