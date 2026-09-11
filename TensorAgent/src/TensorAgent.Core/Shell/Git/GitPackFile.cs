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
using System.IO.Compression;

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// One <c>.pack</c> / <c>.idx</c> pair, read-only.
///
/// <para>
/// <b>Why packs are implemented at all.</b> The alternative was tempting: this shell's
/// repositories are local scratch, created by <c>git init</c> here, so every object it
/// writes is loose and a loose-only reader would pass its own tests. It would then fail
/// on the first repository that mattered. Git runs <c>gc</c> on its own after ~6700
/// loose objects or 50 packs, so a workspace that lives long enough packs itself without
/// anyone asking; a directory copied or restored from anywhere else is packed already;
/// and <c>git log</c> against such a repository would report an empty history rather
/// than an error, which is the exact failure mode this whole implementation is meant to
/// avoid. Reading packs is about 300 lines. Not reading them is a silent wrong answer.
/// </para>
/// <para>
/// <b>What is implemented.</b> Reading: index v1 and v2, both delta encodings
/// (<c>OFS_DELTA</c> and <c>REF_DELTA</c>), and the copy/insert delta instruction
/// stream. <b>Not</b> implemented: writing packs (this implementation only ever writes
/// loose objects, which real git reads happily), the <c>multi-pack-index</c> (unnecessary
/// — every <c>.idx</c> in the directory is opened directly, which is what the midx is an
/// optimisation over), and reverse indexes. Objects are located by binary search over the
/// idx's sorted name table, so a lookup costs a seek and an inflate rather than a scan.
/// </para>
/// </summary>
internal sealed class GitPackFile : IDisposable
{
    /// <summary>The v2 index signature, <c>\377tOc</c>. A v1 index has no magic at all — it starts straight into the fanout.</summary>
    private static readonly byte[] IndexV2Magic = { 0xFF, 0x74, 0x4F, 0x63 };

    private const int FanOutEntries = 256;

    /// <summary>
    /// How deep a delta chain may go before it is called corrupt. Git's own packer caps
    /// chains at 50 by default (<c>pack.depth</c>); 200 leaves room for a repository
    /// packed by something more aggressive without letting a cyclic pack loop forever.
    /// </summary>
    private const int MaxDeltaDepth = 200;

    private readonly string _packPath;
    private readonly byte[] _idx;
    private readonly int _count;
    private readonly int _namesOffset;
    private readonly int _offsetsOffset;
    private readonly int _largeOffsetsOffset;
    private readonly bool _version2;
    private readonly Dictionary<long, GitObjectData> _cache = new();
    private FileStream? _pack;

    private GitPackFile(string packPath, byte[] idx, bool version2, int count, int namesOffset, int offsetsOffset, int largeOffsetsOffset)
    {
        _packPath = packPath;
        _idx = idx;
        _version2 = version2;
        _count = count;
        _namesOffset = namesOffset;
        _offsetsOffset = offsetsOffset;
        _largeOffsetsOffset = largeOffsetsOffset;
    }

    /// <summary>
    /// Opens a pack given its already-confinement-checked <c>.idx</c> and <c>.pack</c>
    /// paths. Returns null when the idx is a version this reader does not know, so that
    /// one unreadable pack does not take the whole repository down with it — the caller
    /// reports the object as missing, which is honest, rather than reporting an empty
    /// history, which is not.
    /// </summary>
    public static GitPackFile? Open(string idxPath, string packPath)
    {
        byte[] idx = File.ReadAllBytes(idxPath);
        if (idx.Length < (FanOutEntries * 4) + (GitObjectId.RawLength * 2))
            return null;

        bool version2 = idx.AsSpan(0, 4).SequenceEqual(IndexV2Magic);
        if (version2)
        {
            uint version = BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(4, 4));
            if (version != 2)
                return null;                       // v3+ has never shipped; do not guess at it

            int fanOut = 8;
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(fanOut + ((FanOutEntries - 1) * 4), 4));
            int names = fanOut + (FanOutEntries * 4);
            int crcs = names + (count * GitObjectId.RawLength);
            int offsets = crcs + (count * 4);
            int largeOffsets = offsets + (count * 4);
            if (largeOffsets > idx.Length)
                return null;
            return new GitPackFile(packPath, idx, true, count, names, offsets, largeOffsets);
        }
        else
        {
            // v1: fanout, then count records of (4-byte offset, 20-byte name) interleaved.
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan((FanOutEntries - 1) * 4, 4));
            int table = FanOutEntries * 4;
            if (table + (count * (4 + GitObjectId.RawLength)) > idx.Length)
                return null;
            return new GitPackFile(packPath, idx, false, count, table, table, 0);
        }
    }

    /// <summary>The name at slot <paramref name="i"/> of the sorted table, as raw bytes.</summary>
    private ReadOnlySpan<byte> NameAt(int i)
        => _version2
            ? _idx.AsSpan(_namesOffset + (i * GitObjectId.RawLength), GitObjectId.RawLength)
            : _idx.AsSpan(_namesOffset + (i * (4 + GitObjectId.RawLength)) + 4, GitObjectId.RawLength);

    private long OffsetAt(int i)
    {
        if (!_version2)
            return BinaryPrimitives.ReadUInt32BigEndian(_idx.AsSpan(_offsetsOffset + (i * (4 + GitObjectId.RawLength)), 4));

        uint packed = BinaryPrimitives.ReadUInt32BigEndian(_idx.AsSpan(_offsetsOffset + (i * 4), 4));
        if ((packed & 0x80000000u) == 0)
            return packed;

        // The high bit means "this is an index into the 8-byte table", which is how a
        // pack larger than 2 GiB addresses its objects.
        int large = (int)(packed & 0x7FFFFFFFu);
        int at = _largeOffsetsOffset + (large * 8);
        if (at + 8 > _idx.Length)
            throw new GitFormatException("pack index: large offset out of range");
        return BinaryPrimitives.ReadInt64BigEndian(_idx.AsSpan(at, 8));
    }

    /// <summary>The half-open slot range whose names begin with the byte <paramref name="first"/>.</summary>
    private (int Start, int End) FanOutRange(byte first)
    {
        int fanOut = _version2 ? 8 : 0;
        int start = first == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32BigEndian(_idx.AsSpan(fanOut + ((first - 1) * 4), 4));
        int end = (int)BinaryPrimitives.ReadUInt32BigEndian(_idx.AsSpan(fanOut + (first * 4), 4));
        return (start, Math.Min(end, _count));
    }

    /// <summary>Binary search for a full object name; -1 when the pack does not hold it.</summary>
    private int Find(ReadOnlySpan<byte> raw)
    {
        (int lo, int hi) = FanOutRange(raw[0]);
        hi--;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int cmp = NameAt(mid).SequenceCompareTo(raw);
            if (cmp == 0)
                return mid;
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return -1;
    }

    public bool Contains(string id) => GitObjectId.IsFullId(id) && Find(GitObjectId.FromHex(id)) >= 0;

    /// <summary>Every object name in this pack starting with <paramref name="prefix"/> (lowercase hex, possibly odd-length).</summary>
    public void CollectPrefix(string prefix, ICollection<string> into, int limit)
    {
        if (prefix.Length == 0)
            return;
        byte first = (byte)((Hex(prefix[0]) << 4) | (prefix.Length > 1 ? Hex(prefix[1]) : 0));
        // An odd-length prefix pins only the high nibble of the first byte, so the whole
        // 16-slot fanout run for that nibble has to be scanned.
        byte lastFirst = prefix.Length > 1 ? first : (byte)(first | 0x0F);
        for (int b = first; b <= lastFirst; b++)
        {
            (int start, int end) = FanOutRange((byte)b);
            for (int i = start; i < end && into.Count < limit; i++)
            {
                string id = GitObjectId.ToHex(NameAt(i));
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    into.Add(id);
            }
        }
    }

    private static int Hex(char c) => c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a') + 10;

    public bool TryRead(string id, out GitObjectData data)
    {
        data = default;
        if (!GitObjectId.IsFullId(id))
            return false;
        int slot = Find(GitObjectId.FromHex(id));
        if (slot < 0)
            return false;
        data = ReadAt(OffsetAt(slot), 0);
        return true;
    }

    private FileStream Pack => _pack ??= new FileStream(_packPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Inflates the object stored at <paramref name="offset"/>, resolving deltas against
    /// their bases. Bases are memoised: a delta chain of length <i>n</i> would otherwise
    /// re-inflate its base <i>n</i> times, which turns a <c>git log</c> over a packed
    /// history into a quadratic walk.
    /// </summary>
    private GitObjectData ReadAt(long offset, int depth)
    {
        if (depth > MaxDeltaDepth)
            throw new GitFormatException("pack: delta chain deeper than " + MaxDeltaDepth + " (corrupt or cyclic pack)");
        if (_cache.TryGetValue(offset, out GitObjectData cached))
            return cached;

        FileStream pack = Pack;
        pack.Seek(offset, SeekOrigin.Begin);

        // Object header: 3-bit type and a little-endian, 7-bits-per-byte size.
        int b = ReadByte(pack);
        int packType = (b >> 4) & 7;
        long size = b & 0x0F;
        int shift = 4;
        while ((b & 0x80) != 0)
        {
            b = ReadByte(pack);
            size |= (long)(b & 0x7F) << shift;
            shift += 7;
            if (shift > 60)
                throw new GitFormatException("pack: object size header is not terminated");
        }

        GitObjectData result;
        if (GitObjectTypes.TryFromPack(packType, out GitObjectType type))
        {
            result = new GitObjectData(type, Inflate(pack, pack.Position, size));
        }
        else if (packType == 6)
        {
            // OFS_DELTA: a *negative* distance back to the base, in a variable-length
            // encoding of its own that is not the size encoding above — each continuation
            // adds one to the accumulator so that no two encodings mean the same number.
            b = ReadByte(pack);
            long back = b & 0x7F;
            while ((b & 0x80) != 0)
            {
                b = ReadByte(pack);
                back = ((back + 1) << 7) | (uint)(b & 0x7F);
            }
            long baseOffset = offset - back;
            if (baseOffset < 0 || baseOffset >= pack.Length)
                throw new GitFormatException("pack: delta base offset out of range");
            byte[] delta = Inflate(pack, pack.Position, size);
            GitObjectData baseObject = ReadAt(baseOffset, depth + 1);
            result = new GitObjectData(baseObject.Type, ApplyDelta(baseObject.Payload, delta));
        }
        else if (packType == 7)
        {
            // REF_DELTA: the base is named outright. It is normally in this same pack, but
            // a "thin" pack (only ever seen mid-transfer, never at rest) may point outside;
            // that case is reported rather than guessed at.
            var raw = new byte[GitObjectId.RawLength];
            pack.ReadExactly(raw);
            string baseId = GitObjectId.ToHex(raw);
            byte[] delta = Inflate(pack, pack.Position, size);
            int slot = Find(raw);
            if (slot < 0)
                throw new GitFormatException("pack: delta base " + baseId + " is not in this pack (thin pack?)");
            GitObjectData baseObject = ReadAt(OffsetAt(slot), depth + 1);
            result = new GitObjectData(baseObject.Type, ApplyDelta(baseObject.Payload, delta));
        }
        else
        {
            throw new GitFormatException("pack: unknown object type " + packType);
        }

        // Only bases are worth keeping; a leaf is asked for once. Bound the cache so a
        // walk over a large pack cannot hold the whole thing in memory.
        if (_cache.Count > 512)
            _cache.Clear();
        _cache[offset] = result;
        return result;
    }

    private static int ReadByte(Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new GitFormatException("pack: unexpected end of file");
        return b;
    }

    /// <summary>
    /// Inflates exactly <paramref name="size"/> bytes starting at <paramref name="position"/>.
    /// The zlib stream's compressed length is not recorded anywhere in the pack — only the
    /// inflated size is — so the decompressor is allowed to read ahead past the object's
    /// end, and the caller must seek before every read rather than continuing sequentially.
    /// </summary>
    private static byte[] Inflate(FileStream pack, long position, long size)
    {
        if (size < 0 || size > int.MaxValue)
            throw new GitFormatException("pack: object size " + size + " is out of range");
        pack.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[size];
        using var z = new ZLibStream(pack, CompressionMode.Decompress, leaveOpen: true);
        z.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>
    /// Applies git's delta encoding: a source size, a target size, then a stream of
    /// instructions that either copy a run out of the base (high bit set, with a bitmask
    /// saying which of the four offset bytes and three size bytes are present) or insert
    /// the next <c>n</c> literal bytes. A size of zero in a copy means 0x10000, which is
    /// the one place the format overloads a value rather than widening a field.
    /// </summary>
    internal static byte[] ApplyDelta(byte[] source, byte[] delta)
    {
        int pos = 0;
        long sourceSize = ReadVarInt(delta, ref pos);
        long targetSize = ReadVarInt(delta, ref pos);
        if (sourceSize != source.Length)
            throw new GitFormatException("pack: delta base is " + source.Length + " bytes, delta expects " + sourceSize);
        if (targetSize > int.MaxValue)
            throw new GitFormatException("pack: delta result is too large");

        var target = new byte[targetSize];
        int written = 0;
        while (pos < delta.Length)
        {
            int cmd = delta[pos++];
            if ((cmd & 0x80) != 0)
            {
                long offset = 0;
                long size = 0;
                if ((cmd & 0x01) != 0) offset |= (long)delta[pos++];
                if ((cmd & 0x02) != 0) offset |= (long)delta[pos++] << 8;
                if ((cmd & 0x04) != 0) offset |= (long)delta[pos++] << 16;
                if ((cmd & 0x08) != 0) offset |= (long)delta[pos++] << 24;
                if ((cmd & 0x10) != 0) size |= (long)delta[pos++];
                if ((cmd & 0x20) != 0) size |= (long)delta[pos++] << 8;
                if ((cmd & 0x40) != 0) size |= (long)delta[pos++] << 16;
                if (size == 0)
                    size = 0x10000;
                if (offset + size > source.Length || written + size > target.Length)
                    throw new GitFormatException("pack: delta copy runs past the end of the object");
                Array.Copy(source, (int)offset, target, written, (int)size);
                written += (int)size;
            }
            else if (cmd != 0)
            {
                if (pos + cmd > delta.Length || written + cmd > target.Length)
                    throw new GitFormatException("pack: delta insert runs past the end of the object");
                Array.Copy(delta, pos, target, written, cmd);
                pos += cmd;
                written += cmd;
            }
            else
            {
                // Instruction 0 is reserved and has never been emitted by any git.
                throw new GitFormatException("pack: reserved delta instruction 0");
            }
        }

        if (written != target.Length)
            throw new GitFormatException("pack: delta produced " + written + " bytes, expected " + target.Length);
        return target;
    }

    /// <summary>The delta header's size encoding: 7 bits per byte, little end first.</summary>
    private static long ReadVarInt(byte[] data, ref int pos)
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= data.Length)
                throw new GitFormatException("pack: truncated delta header");
            int b = data[pos++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 60)
                throw new GitFormatException("pack: delta size header is not terminated");
        }
    }

    public void Dispose()
    {
        _pack?.Dispose();
        _pack = null;
        _cache.Clear();
    }
}
