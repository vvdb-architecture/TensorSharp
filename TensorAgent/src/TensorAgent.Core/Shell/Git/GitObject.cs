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

namespace TensorAgent.Core.Shell.Git;

/// <summary>The four things a git object can be. Git's on-disk names, spelled its way.</summary>
internal enum GitObjectType
{
    Commit = 1,
    Tree = 2,
    Blob = 3,
    Tag = 4,
}

/// <summary>An object as it exists once inflated: what it is, and its bytes.</summary>
/// <remarks>
/// The payload deliberately excludes the <c>"type length\0"</c> header. That header is
/// part of the identity (it is hashed) but not part of the content, and every consumer
/// here — tree parser, commit parser, <c>cat-file -p</c> — wants the content alone.
/// <see cref="GitObjectId.Compute"/> is the one place that puts the header back.
/// </remarks>
internal readonly record struct GitObjectData(GitObjectType Type, byte[] Payload);

/// <summary>Object type names, in both directions, including the pack encoding's numbers.</summary>
internal static class GitObjectTypes
{
    public static string Name(GitObjectType type) => type switch
    {
        GitObjectType.Commit => "commit",
        GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob",
        GitObjectType.Tag => "tag",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParse(string name, out GitObjectType type)
    {
        switch (name)
        {
            case "commit": type = GitObjectType.Commit; return true;
            case "tree": type = GitObjectType.Tree; return true;
            case "blob": type = GitObjectType.Blob; return true;
            case "tag": type = GitObjectType.Tag; return true;
            default: type = default; return false;
        }
    }

    /// <summary>
    /// The 3-bit type field of a packed object header. 5 is unused and 6/7 are the two
    /// delta encodings, which are not object types at all — the caller resolves those
    /// against a base before it ever asks for a type.
    /// </summary>
    public static bool TryFromPack(int packType, out GitObjectType type)
    {
        if (packType is >= 1 and <= 4)
        {
            type = (GitObjectType)packType;
            return true;
        }
        type = default;
        return false;
    }
}

/// <summary>
/// Object names: 40 lowercase hex characters, and the SHA-1 that produces them.
///
/// <para>
/// SHA-1 is not a security choice here, it is the file format. A git object's name is
/// defined as <c>SHA1("&lt;type&gt; &lt;length&gt;\0" + content)</c>, and any other digest
/// would produce a repository that the real <c>git</c> binary cannot read — which is
/// precisely the property this implementation is built to keep. Git's own SHA-256
/// repositories exist but are opt-in at <c>init</c> time and vanishingly rare; a
/// repository whose <c>extensions.objectFormat</c> says <c>sha256</c> is refused by
/// <see cref="GitRepository"/> rather than silently misread.
/// </para>
/// </summary>
internal static class GitObjectId
{
    public const int RawLength = 20;
    public const int HexLength = 40;

    /// <summary>The all-zero id, which git uses to mean "no object" in reflogs and diffs.</summary>
    public const string Zero = "0000000000000000000000000000000000000000";

    /// <summary>The header git hashes ahead of the content: <c>type SP length NUL</c>, in ASCII.</summary>
    public static byte[] Header(GitObjectType type, long length)
        => Encoding.ASCII.GetBytes(GitObjectTypes.Name(type) + " " + length.ToString(CultureInfo.InvariantCulture) + "\0");

    public static string Compute(GitObjectType type, ReadOnlySpan<byte> payload)
    {
        using var sha = SHA1.Create();
        byte[] header = Header(type, payload.Length);
        sha.TransformBlock(header, 0, header.Length, null, 0);
        byte[] body = payload.ToArray();
        sha.TransformFinalBlock(body, 0, body.Length);
        return ToHex(sha.Hash!);
    }

    public static string ToHex(ReadOnlySpan<byte> raw)
    {
        Span<char> chars = stackalloc char[raw.Length * 2];
        const string digits = "0123456789abcdef";
        for (int i = 0; i < raw.Length; i++)
        {
            chars[i * 2] = digits[raw[i] >> 4];
            chars[(i * 2) + 1] = digits[raw[i] & 0xF];
        }
        return new string(chars);
    }

    public static byte[] FromHex(string hex)
    {
        if (hex.Length != HexLength || !IsHex(hex))
            throw new ArgumentException("not an object name: " + hex, nameof(hex));
        var raw = new byte[RawLength];
        for (int i = 0; i < RawLength; i++)
            raw[i] = (byte)((Digit(hex[i * 2]) << 4) | Digit(hex[(i * 2) + 1]));
        return raw;
    }

    private static int Digit(char c) => c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a') + 10;

    public static bool IsHex(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (char c in text)
        {
            if (!(c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        }
        return true;
    }

    public static bool IsFullId(string text) => text.Length == HexLength && IsHex(text);

    /// <summary>Git's default abbreviation for display. Not for lookup — lookup accepts any unambiguous prefix.</summary>
    public static string Abbreviate(string id, int length = 7) => id.Length <= length ? id : id[..length];
}

/// <summary>
/// The file modes git records. Only these five exist: git stores a permission bit, not
/// a permission mask, so every regular file is either <c>100644</c> or <c>100755</c> and
/// nothing else. Values are the octal literals the tree format writes.
/// </summary>
internal static class GitFileMode
{
    public const int Tree = 0x4000;         // 040000
    public const int Regular = 0x81A4;      // 100644
    public const int Executable = 0x81ED;   // 100755
    public const int Symlink = 0xA000;      // 120000
    public const int GitLink = 0xE000;      // 160000, a submodule's recorded commit

    /// <summary>Octal, with the leading zero omitted for trees exactly as git writes it.</summary>
    public static string Format(int mode) => Convert.ToString(mode, 8);

    /// <summary>Git pads to six digits when it names a mode in prose (diff headers, status).</summary>
    public static string FormatPadded(int mode) => Convert.ToString(mode, 8).PadLeft(6, '0');

    public static bool IsTree(int mode) => mode == Tree;

    public static bool IsBlob(int mode) => mode is Regular or Executable or Symlink;
}

/// <summary>One entry of a tree object.</summary>
internal readonly record struct GitTreeEntry(int Mode, string Name, string Id)
{
    public bool IsTree => GitFileMode.IsTree(Mode);
}

/// <summary>
/// Tree objects: a flat, sorted list of <c>mode SP name NUL rawid</c> records.
///
/// <para>
/// The sort order is the one detail a re-implementation usually gets wrong, and getting
/// it wrong produces trees that hash differently from git's for identical content — so
/// <c>git fsck</c> complains and every id in the repository diverges. Git sorts by the
/// raw name bytes, but compares a subtree <em>as if its name ended in a slash</em>,
/// because that is where the name would sort if trees were spelled out as paths.
/// <see cref="Compare"/> is that rule.
/// </para>
/// </summary>
internal static class GitTree
{
    public static List<GitTreeEntry> Parse(ReadOnlySpan<byte> payload)
    {
        var entries = new List<GitTreeEntry>();
        int i = 0;
        while (i < payload.Length)
        {
            int space = payload[i..].IndexOf((byte)' ');
            if (space < 0)
                throw new GitFormatException("malformed tree: no mode terminator");
            space += i;
            int mode = 0;
            for (int m = i; m < space; m++)
            {
                if (payload[m] is < (byte)'0' or > (byte)'7')
                    throw new GitFormatException("malformed tree: mode is not octal");
                mode = (mode * 8) + (payload[m] - '0');
            }

            int nul = payload[(space + 1)..].IndexOf((byte)0);
            if (nul < 0)
                throw new GitFormatException("malformed tree: unterminated name");
            nul += space + 1;
            string name = Encoding.UTF8.GetString(payload[(space + 1)..nul]);

            if (nul + 1 + GitObjectId.RawLength > payload.Length)
                throw new GitFormatException("malformed tree: truncated object name");
            string id = GitObjectId.ToHex(payload.Slice(nul + 1, GitObjectId.RawLength));

            entries.Add(new GitTreeEntry(mode, name, id));
            i = nul + 1 + GitObjectId.RawLength;
        }
        return entries;
    }

    public static byte[] Serialize(IEnumerable<GitTreeEntry> entries)
    {
        List<GitTreeEntry> sorted = entries.ToList();
        sorted.Sort(Compare);
        using var ms = new MemoryStream();
        foreach (GitTreeEntry entry in sorted)
        {
            byte[] prefix = Encoding.UTF8.GetBytes(GitFileMode.Format(entry.Mode) + " " + entry.Name);
            ms.Write(prefix, 0, prefix.Length);
            ms.WriteByte(0);
            byte[] raw = GitObjectId.FromHex(entry.Id);
            ms.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Git's tree order: raw name bytes, with a subtree compared as <c>name/</c>.</summary>
    public static int Compare(GitTreeEntry a, GitTreeEntry b)
    {
        byte[] left = Encoding.UTF8.GetBytes(a.Name);
        byte[] right = Encoding.UTF8.GetBytes(b.Name);
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            if (left[i] != right[i])
                return left[i] - right[i];
        }
        byte leftNext = left.Length > n ? left[n] : (byte)(a.IsTree ? '/' : 0);
        byte rightNext = right.Length > n ? right[n] : (byte)(b.IsTree ? '/' : 0);
        return leftNext - rightNext;
    }
}

/// <summary>A name, an address, and a moment — the <c>author</c> / <c>committer</c> line.</summary>
internal readonly record struct GitSignature(string Name, string Email, DateTimeOffset When)
{
    /// <summary>Git's wire form: <c>Name &lt;email&gt; seconds ±hhmm</c>.</summary>
    public string Format()
    {
        TimeSpan offset = When.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan abs = offset.Duration();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} <{1}> {2} {3}{4:00}{5:00}",
            Name,
            Email,
            When.ToUnixTimeSeconds(),
            sign,
            abs.Hours,
            abs.Minutes);
    }

    /// <summary>How <c>git log</c> prints a date in its default format.</summary>
    public string FormatDate()
    {
        TimeSpan offset = When.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan abs = offset.Duration();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:ddd MMM} {1} {0:HH:mm:ss} {2} {3}{4:00}{5:00}",
            When,
            When.Day,
            When.Year,
            sign,
            abs.Hours,
            abs.Minutes);
    }

    /// <summary>
    /// Parses <c>Name &lt;email&gt; seconds ±hhmm</c>. The name may itself contain spaces and
    /// the timezone may be absent in objects written by old or broken tools, so the parse
    /// works backwards from the last <c>&gt;</c> rather than splitting on whitespace.
    /// </summary>
    public static GitSignature Parse(string line)
    {
        int close = line.LastIndexOf('>');
        int open = close < 0 ? -1 : line.LastIndexOf('<', close);
        if (open < 0)
            return new GitSignature(line.Trim(), string.Empty, DateTimeOffset.UnixEpoch);

        string name = line[..open].TrimEnd();
        string email = line[(open + 1)..close];
        string[] rest = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long seconds = rest.Length > 0 && long.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
        TimeSpan zone = TimeSpan.Zero;
        if (rest.Length > 1 && rest[1].Length == 5 && (rest[1][0] == '+' || rest[1][0] == '-')
            && int.TryParse(rest[1].AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh)
            && int.TryParse(rest[1].AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm))
        {
            zone = new TimeSpan(hh, mm, 0);
            if (rest[1][0] == '-')
                zone = -zone;
        }

        // A zone outside ±14:00 is not representable as a DateTimeOffset; treat it as UTC.
        if (zone > TimeSpan.FromHours(14) || zone < TimeSpan.FromHours(-14))
            zone = TimeSpan.Zero;
        if (seconds is < -62135596800 or > 253402300799)
            seconds = 0;
        return new GitSignature(name, email, DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(zone));
    }
}

/// <summary>A parsed commit object.</summary>
internal sealed class GitCommit
{
    public required string Id { get; init; }

    public required string Tree { get; init; }

    public required IReadOnlyList<string> Parents { get; init; }

    public required GitSignature Author { get; init; }

    public required GitSignature Committer { get; init; }

    public required string Message { get; init; }

    /// <summary>The first line, which is what <c>--oneline</c> and <c>[branch abc1234]</c> show.</summary>
    public string Subject
    {
        get
        {
            string trimmed = Message.TrimStart('\n');
            int nl = trimmed.IndexOf('\n');
            return (nl < 0 ? trimmed : trimmed[..nl]).TrimEnd();
        }
    }

    /// <summary>
    /// Commit objects are a header block, a blank line, then the message. Headers we do
    /// not model (<c>gpgsig</c>, <c>encoding</c>, <c>mergetag</c>) are skipped rather than
    /// rejected: they carry continuation lines that begin with a space, and a reader that
    /// choked on them could not read most real repositories.
    /// </summary>
    public static GitCommit Parse(string id, ReadOnlySpan<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        string tree = string.Empty;
        var parents = new List<string>();
        GitSignature author = default;
        GitSignature committer = default;

        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
                nl = text.Length;
            string line = text[pos..nl];
            pos = nl + 1;
            if (line.Length == 0)
                break;                                  // the blank line: the message follows
            if (line[0] == ' ')
                continue;                               // a continuation of a header we ignore
            int space = line.IndexOf(' ');
            if (space < 0)
                continue;
            string key = line[..space];
            string value = line[(space + 1)..];
            switch (key)
            {
                case "tree": tree = value; break;
                case "parent": parents.Add(value); break;
                case "author": author = GitSignature.Parse(value); break;
                case "committer": committer = GitSignature.Parse(value); break;
            }
        }

        if (tree.Length == 0)
            throw new GitFormatException("commit " + id + " has no tree");

        return new GitCommit
        {
            Id = id,
            Tree = tree,
            Parents = parents,
            Author = author,
            Committer = committer.Name is null ? author : committer,
            Message = pos < text.Length ? text[pos..] : string.Empty,
        };
    }

    public static byte[] Serialize(string tree, IReadOnlyList<string> parents, GitSignature author, GitSignature committer, string message)
    {
        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree).Append('\n');
        foreach (string parent in parents)
            sb.Append("parent ").Append(parent).Append('\n');
        sb.Append("author ").Append(author.Format()).Append('\n');
        sb.Append("committer ").Append(committer.Format()).Append('\n');
        sb.Append('\n');
        sb.Append(message);
        if (!message.EndsWith('\n'))
            sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>A parsed annotated-tag object. Only ever read: this implementation creates lightweight tags or none.</summary>
internal sealed class GitTag
{
    public required string Object { get; init; }

    public required GitObjectType TargetType { get; init; }

    public required string Name { get; init; }

    public required string Message { get; init; }

    public static GitTag Parse(ReadOnlySpan<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        string target = string.Empty;
        string name = string.Empty;
        GitObjectType type = GitObjectType.Commit;
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
                nl = text.Length;
            string line = text[pos..nl];
            pos = nl + 1;
            if (line.Length == 0)
                break;
            int space = line.IndexOf(' ');
            if (space < 0)
                continue;
            switch (line[..space])
            {
                case "object": target = line[(space + 1)..]; break;
                case "type": GitObjectTypes.TryParse(line[(space + 1)..], out type); break;
                case "tag": name = line[(space + 1)..]; break;
            }
        }

        if (target.Length == 0)
            throw new GitFormatException("malformed tag: no object");
        return new GitTag
        {
            Object = target,
            TargetType = type,
            Name = name,
            Message = pos < text.Length ? text[pos..] : string.Empty,
        };
    }
}

/// <summary>
/// The repository on disk is not what this code believes it to be.
///
/// <para>
/// Kept distinct from <see cref="GitFatalException"/> because the two mean different
/// things to a caller: a fatal is a refusal this implementation chose (unknown flag,
/// nothing to commit), a format exception is data that cannot be trusted. Both end up
/// on stderr as <c>fatal:</c>, which is also how git reports both.
/// </para>
/// </summary>
internal sealed class GitFormatException : Exception
{
    public GitFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>A refusal, carrying the exit status git itself would use.</summary>
/// <remarks>
/// Git's exit codes are a small vocabulary and the shell's caller reads them: 128 for
/// <c>fatal:</c>, 129 for a usage error out of parse-options, 1 for "ran fine, found
/// nothing" (an empty <c>commit</c>, a pathspec that matched nothing). Every refusal in
/// this implementation picks one of those deliberately, because a coding agent that
/// sees 0 will report success to a user.
/// </remarks>
internal sealed class GitFatalException : Exception
{
    public GitFatalException(string message, int code = 128, string prefix = "fatal")
        : base(message)
    {
        Code = code;
        Prefix = prefix;
    }

    /// <summary>The process exit status.</summary>
    public int Code { get; }

    /// <summary>The word before the colon on stderr: <c>fatal</c>, <c>error</c>, or <c>git</c>.</summary>
    public string Prefix { get; }
}
