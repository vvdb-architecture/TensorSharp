// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TensorAgent.Core.Shell.Git;

/// <summary>One row of <c>.git/index</c>: a path, the blob it stages, and the stat data git caches beside it.</summary>
internal sealed class GitIndexEntry
{
    /// <summary>Slash-separated and relative to the work tree, which is how git stores it on every platform.</summary>
    public required string Path { get; set; }

    public required string Id { get; set; }

    public required int Mode { get; set; }

    public long Size { get; set; }

    public uint MTimeSeconds { get; set; }

    public uint MTimeNanoseconds { get; set; }

    public uint CTimeSeconds { get; set; }

    public uint CTimeNanoseconds { get; set; }

    public uint Device { get; set; }

    public uint Inode { get; set; }

    public uint Uid { get; set; }

    public uint Gid { get; set; }

    /// <summary>0 for a normal entry; 1/2/3 are the base/ours/theirs rows of an unresolved merge.</summary>
    public int Stage { get; set; }

    public bool AssumeValid { get; set; }

    /// <summary>The v3 extended flags word, preserved so that <c>--skip-worktree</c> survives a round trip.</summary>
    public ushort ExtendedFlags { get; set; }

    public GitIndexEntry Clone() => (GitIndexEntry)MemberwiseClone();
}

/// <summary>
/// <c>.git/index</c> — the staging area — in its version 2 and 3 forms.
///
/// <para>
/// <b>Format.</b> <c>DIRC</c>, a version, an entry count, then fixed-size records sorted
/// by path bytes and NUL-padded to a multiple of eight, then optional extensions, then a
/// SHA-1 of everything before it. The trailer is verified on read: a truncated index
/// otherwise parses as a *shorter* index, which would look exactly like "the agent
/// unstaged some files" and get committed as such.
/// </para>
/// <para>
/// <b>Versions.</b> 2 is written always (3 only when an entry actually carries extended
/// flags, since v3 differs from v2 only by those two bytes). Version 4 is refused, not
/// approximated: v4 prefix-compresses path names against the previous entry, so a v2
/// reader pointed at a v4 index does not fail — it silently produces wrong paths.
/// </para>
/// <para>
/// <b>Extensions are dropped on write.</b> The cached tree (<c>TREE</c>) and untracked
/// cache (<c>UNTR</c>) are optimisations git rebuilds on demand, and an extension that
/// was correct before an edit would be stale after it. The one that cannot merely be
/// dropped is <c>link</c> (a split index), where the entries themselves live in another
/// file; that is refused on read for the same reason as v4.
/// </para>
/// <para>
/// <b>Stat data.</b> <c>dev</c>, <c>ino</c>, <c>uid</c> and <c>gid</c> are written as
/// zero because .NET exposes no portable way to read them and this shell must build for
/// iOS. Git tolerates it exactly as it tolerates a checkout on a filesystem without
/// inodes: the stat comparison reports "changed", git falls back to hashing the file,
/// the hash matches, and the file is reported clean. The cost is one extra read per file
/// in <c>git status</c> run by the real binary; the alternative — lying about stat data —
/// would report modified files as clean.
/// </para>
/// </summary>
internal sealed class GitIndex
{
    private static readonly byte[] Signature = { (byte)'D', (byte)'I', (byte)'R', (byte)'C' };

    private const int NameLengthMask = 0x0FFF;
    private const ushort FlagExtended = 0x4000;
    private const ushort FlagAssumeValid = 0x8000;

    private readonly List<GitIndexEntry> _entries = new();

    /// <summary>Entries in git's order: path bytes ascending, then stage.</summary>
    public IReadOnlyList<GitIndexEntry> Entries => _entries;

    public bool Dirty { get; set; }

    /// <summary>True when any entry is at a merge stage, which blocks <c>commit</c> the way git blocks it.</summary>
    public bool HasConflicts => _entries.Any(e => e.Stage != 0);

    public static GitIndex Empty() => new();

    public static GitIndex Read(GitFileGate gate, string path)
    {
        if (!gate.FileExists(path))
            return new GitIndex();
        return Parse(gate.ReadAllBytes(path));
    }

    internal static GitIndex Parse(byte[] data)
    {
        var index = new GitIndex();
        if (data.Length == 0)
            return index;
        if (data.Length < 12 + GitObjectId.RawLength || !data.AsSpan(0, 4).SequenceEqual(Signature))
            throw new GitFormatException("index file corrupt: bad signature");

        uint version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        if (version is not (2 or 3))
        {
            throw new GitFatalException(
                "index file is version " + version + "; this shell's built-in git reads only versions 2 and 3. "
                + "Run `git update-index --index-version 2` with a real git, or delete .git/index and re-add.");
        }

        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));

        // Verify the trailer before trusting any of it.
        int body = data.Length - GitObjectId.RawLength;
        using (var sha = SHA1.Create())
        {
            byte[] actual = sha.ComputeHash(data, 0, body);
            if (!actual.AsSpan().SequenceEqual(data.AsSpan(body, GitObjectId.RawLength)))
                throw new GitFormatException("index file corrupt: bad checksum");
        }

        int pos = 12;
        for (int i = 0; i < count; i++)
        {
            if (pos + 62 > body)
                throw new GitFormatException("index file corrupt: truncated entry " + i);
            int start = pos;
            var entry = new GitIndexEntry { Path = string.Empty, Id = string.Empty, Mode = 0 };
            entry.CTimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            entry.CTimeNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4, 4));
            entry.MTimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8, 4));
            entry.MTimeNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 12, 4));
            entry.Device = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 16, 4));
            entry.Inode = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 20, 4));
            entry.Mode = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 24, 4));
            entry.Uid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 28, 4));
            entry.Gid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 32, 4));
            entry.Size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 36, 4));
            entry.Id = GitObjectId.ToHex(data.AsSpan(pos + 40, GitObjectId.RawLength));
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 60, 2));
            entry.AssumeValid = (flags & FlagAssumeValid) != 0;
            entry.Stage = (flags >> 12) & 3;
            int nameLength = flags & NameLengthMask;
            pos += 62;

            if ((flags & FlagExtended) != 0)
            {
                if (version < 3)
                    throw new GitFormatException("index file corrupt: extended flag in a version 2 entry");
                entry.ExtendedFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
                pos += 2;
            }

            // 0xFFF is a sentinel for "longer than the field can say"; the name then runs
            // to the NUL, which is why the terminator is scanned for rather than assumed.
            int nul = Array.IndexOf(data, (byte)0, pos, body - pos);
            if (nul < 0)
                throw new GitFormatException("index file corrupt: unterminated path");
            int actualLength = nul - pos;
            if (nameLength != NameLengthMask && nameLength != actualLength)
                throw new GitFormatException("index file corrupt: path length mismatch");
            entry.Path = Encoding.UTF8.GetString(data, pos, actualLength);
            pos = nul + 1;

            // Records are padded with NULs to an 8-byte boundary measured from the record's start.
            int length = pos - start;
            pos += (8 - (length % 8)) % 8;
            index._entries.Add(entry);
        }

        // Extensions: 4-byte name, 4-byte length, payload. Optional ones have a lowercase
        // first letter and may be skipped; `link` may not, because it moves the entries.
        while (pos + 8 <= body)
        {
            string name = Encoding.ASCII.GetString(data, pos, 4);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4, 4));
            if (name == "link")
            {
                throw new GitFatalException(
                    "this repository uses a split index (core.splitIndex), which this shell's built-in git cannot read. "
                    + "Run `git update-index --no-split-index` with a real git first.");
            }

            pos += 8 + (int)length;
        }

        index._entries.Sort(CompareEntries);
        return index;
    }

    public byte[] Serialize()
    {
        _entries.Sort(CompareEntries);
        bool needsV3 = _entries.Any(e => e.ExtendedFlags != 0);
        using var ms = new MemoryStream();
        Span<byte> word = stackalloc byte[4];

        ms.Write(Signature, 0, 4);
        BinaryPrimitives.WriteUInt32BigEndian(word, needsV3 ? 3u : 2u);
        ms.Write(word);
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)_entries.Count);
        ms.Write(word);

        foreach (GitIndexEntry entry in _entries)
        {
            long start = ms.Position;
            WriteUInt32(ms, entry.CTimeSeconds);
            WriteUInt32(ms, entry.CTimeNanoseconds);
            WriteUInt32(ms, entry.MTimeSeconds);
            WriteUInt32(ms, entry.MTimeNanoseconds);
            WriteUInt32(ms, entry.Device);
            WriteUInt32(ms, entry.Inode);
            WriteUInt32(ms, (uint)entry.Mode);
            WriteUInt32(ms, entry.Uid);
            WriteUInt32(ms, entry.Gid);

            // The cached size is 32-bit in every index version; git truncates and relies on
            // the hash for files above 4 GiB. Match it rather than inventing a wider field.
            WriteUInt32(ms, (uint)Math.Min(entry.Size, uint.MaxValue));
            byte[] raw = GitObjectId.FromHex(entry.Id);
            ms.Write(raw, 0, raw.Length);

            byte[] name = Encoding.UTF8.GetBytes(entry.Path);
            var flags = (ushort)(Math.Min(name.Length, NameLengthMask) | (entry.Stage << 12));
            if (entry.AssumeValid)
                flags |= FlagAssumeValid;
            if (entry.ExtendedFlags != 0)
                flags |= FlagExtended;
            Span<byte> half = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(half, flags);
            ms.Write(half);
            if (entry.ExtendedFlags != 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(half, entry.ExtendedFlags);
                ms.Write(half);
            }

            ms.Write(name, 0, name.Length);
            ms.WriteByte(0);
            int length = (int)(ms.Position - start);
            for (int pad = (8 - (length % 8)) % 8; pad > 0; pad--)
                ms.WriteByte(0);
        }

        byte[] body = ms.ToArray();
        using var sha = SHA1.Create();
        byte[] checksum = sha.ComputeHash(body);
        var result = new byte[body.Length + checksum.Length];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        Buffer.BlockCopy(checksum, 0, result, body.Length, checksum.Length);
        return result;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, value);
        stream.Write(word);
    }

    /// <summary>Git's index order: raw path bytes ascending, then stage. Not the tree order — no trailing-slash rule here.</summary>
    private static int CompareEntries(GitIndexEntry a, GitIndexEntry b)
    {
        int cmp = string.CompareOrdinal(a.Path, b.Path);
        return cmp != 0 ? cmp : a.Stage.CompareTo(b.Stage);
    }

    public GitIndexEntry? Find(string path)
    {
        foreach (GitIndexEntry entry in _entries)
        {
            if (entry.Stage == 0 && string.Equals(entry.Path, path, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    /// <summary>Stages <paramref name="entry"/>, replacing any row for that path — including the stages of a conflict, which is how <c>git add</c> resolves one.</summary>
    public void Stage(GitIndexEntry entry)
    {
        _entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.Ordinal));
        entry.Stage = 0;
        _entries.Add(entry);
        Dirty = true;
    }

    public bool Remove(string path)
    {
        int removed = _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.Ordinal));
        if (removed > 0)
            Dirty = true;
        return removed > 0;
    }

    /// <summary>Removes everything under <paramref name="prefix"/> (a directory path, no trailing slash). Returns the paths dropped.</summary>
    public List<string> RemoveTree(string prefix)
    {
        string with = prefix + "/";
        List<string> removed = _entries
            .Where(e => e.Path.StartsWith(with, StringComparison.Ordinal))
            .Select(e => e.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (string path in removed)
            Remove(path);
        return removed;
    }

    public void Write(GitFileGate gate, string path) => gate.WriteAllBytesAtomic(path, Serialize());
}
