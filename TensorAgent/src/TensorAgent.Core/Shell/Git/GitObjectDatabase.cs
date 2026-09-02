// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.IO.Compression;
using System.Text;

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// <c>.git/objects</c>: loose objects, and every pack in <c>objects/pack</c>.
///
/// <para>
/// <b>Reads</b> consult loose storage first and then the packs, which is git's own order
/// and is the one that stays correct while a repository is being repacked. <b>Writes</b>
/// are always loose. That asymmetry is deliberate: a loose object is a zlib stream in a
/// file whose name is its hash — about thirty lines — whereas writing a pack means an
/// idx, a checksum trailer, and delta selection, for no benefit at all, because real git
/// reads loose objects natively and <c>git gc</c> will pack them if anyone cares.
/// </para>
/// <para>
/// Not supported: <c>objects/info/alternates</c> (a borrowed object store, which only
/// appears under <c>clone --shared</c> and worktrees created by <c>git worktree</c>) —
/// an object living only in an alternate is reported missing by name rather than
/// silently skipped, so the failure names the object instead of producing an empty
/// history.
/// </para>
/// </summary>
internal sealed class GitObjectDatabase : IDisposable
{
    /// <summary>
    /// Loose objects are compressed with zlib at git's default level. The level is not
    /// part of the identity — the hash covers the *inflated* bytes — so any level
    /// produces a file real git accepts; this one matches git's default so that object
    /// files are the same size git would have written.
    /// </summary>
    private const CompressionLevel LooseCompression = CompressionLevel.Optimal;

    private readonly GitFileGate _gate;
    private readonly string _objectsDir;
    private List<GitPackFile>? _packs;

    public GitObjectDatabase(GitFileGate gate, string objectsDir)
    {
        _gate = gate;
        _objectsDir = objectsDir;
    }

    private IReadOnlyList<GitPackFile> Packs
    {
        get
        {
            if (_packs != null)
                return _packs;
            _packs = new List<GitPackFile>();
            string packDir = Path.Combine(_objectsDir, "pack");
            foreach (string idxPath in _gate.EnumerateFiles(packDir, "*.idx").OrderBy(p => p, StringComparer.Ordinal))
            {
                string packPath = Path.ChangeExtension(idxPath, ".pack");
                if (!_gate.FileExists(packPath))
                    continue;
                GitPackFile? pack;
                try
                {
                    pack = GitPackFile.Open(_gate.Read(idxPath), _gate.Read(packPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitFormatException)
                {
                    pack = null;
                }

                if (pack != null)
                    _packs.Add(pack);
            }

            return _packs;
        }
    }

    private string LoosePath(string id) => Path.Combine(_objectsDir, id[..2], id[2..]);

    public bool Exists(string id)
    {
        if (!GitObjectId.IsFullId(id))
            return false;
        if (_gate.FileExists(LoosePath(id)))
            return true;
        foreach (GitPackFile pack in Packs)
        {
            if (pack.Contains(id))
                return true;
        }

        return false;
    }

    public bool TryRead(string id, out GitObjectData data)
    {
        data = default;
        if (!GitObjectId.IsFullId(id))
            return false;
        id = id.ToLowerInvariant();

        string loose = LoosePath(id);
        if (_gate.FileExists(loose))
        {
            data = ReadLoose(_gate.ReadAllBytes(loose), id);
            return true;
        }

        foreach (GitPackFile pack in Packs)
        {
            if (pack.TryRead(id, out data))
                return true;
        }

        return false;
    }

    /// <summary>Reads, or fails the way git does when an object name resolves to nothing on disk.</summary>
    public GitObjectData Read(string id)
    {
        if (!TryRead(id, out GitObjectData data))
            throw new GitFatalException("unable to read " + id);
        return data;
    }

    public GitObjectData Read(string id, GitObjectType expected)
    {
        GitObjectData data = Read(id);
        if (data.Type != expected)
        {
            throw new GitFatalException(
                "object " + id + " is a " + GitObjectTypes.Name(data.Type) + ", not a " + GitObjectTypes.Name(expected));
        }

        return data;
    }

    /// <summary>
    /// A loose object is <c>zlib(type SP length NUL content)</c>. The declared length is
    /// checked against what actually inflated: a truncated object file otherwise reads as
    /// a shorter blob, which would then be committed as if it were the real content.
    /// </summary>
    private static GitObjectData ReadLoose(byte[] compressed, string id)
    {
        byte[] raw;
        try
        {
            using var input = new MemoryStream(compressed);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new GitFormatException("loose object " + id + " is corrupt: " + ex.Message);
        }

        int nul = Array.IndexOf(raw, (byte)0);
        if (nul <= 0)
            throw new GitFormatException("loose object " + id + " has no header");
        string header = Encoding.ASCII.GetString(raw, 0, nul);
        int space = header.IndexOf(' ');
        if (space <= 0
            || !GitObjectTypes.TryParse(header[..space], out GitObjectType type)
            || !long.TryParse(header[(space + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long length))
        {
            throw new GitFormatException("loose object " + id + " has a malformed header: " + header);
        }

        var payload = new byte[raw.Length - nul - 1];
        Array.Copy(raw, nul + 1, payload, 0, payload.Length);
        if (payload.Length != length)
            throw new GitFormatException("loose object " + id + " declares " + length + " bytes but holds " + payload.Length);
        return new GitObjectData(type, payload);
    }

    /// <summary>
    /// Hashes and stores an object, returning its name. Writing an object that already
    /// exists is a no-op — the content is identical by definition of the hash, and git
    /// relies on this so that re-adding an unchanged file costs nothing.
    /// </summary>
    public string Write(GitObjectType type, byte[] payload)
    {
        string id = GitObjectId.Compute(type, payload);
        string path = LoosePath(id);
        if (_gate.FileExists(path) || ExistsInPack(id))
            return id;

        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, LooseCompression, leaveOpen: true))
        {
            byte[] header = GitObjectId.Header(type, payload.Length);
            z.Write(header, 0, header.Length);
            z.Write(payload, 0, payload.Length);
        }

        _gate.CreateDirectory(Path.Combine(_objectsDir, id[..2]));
        _gate.WriteAllBytesAtomic(path, buffer.ToArray());
        return id;
    }

    private bool ExistsInPack(string id)
    {
        foreach (GitPackFile pack in Packs)
        {
            if (pack.Contains(id))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every object whose name starts with <paramref name="prefix"/>, so that an
    /// abbreviated id can be rejected as ambiguous rather than resolved to whichever
    /// object happened to be found first. Capped, because "is it unique" only needs two.
    /// </summary>
    public List<string> ResolvePrefix(string prefix, int limit = 8)
    {
        var found = new List<string>();
        prefix = prefix.ToLowerInvariant();
        if (prefix.Length < 2 || !GitObjectId.IsHex(prefix))
            return found;

        string bucket = Path.Combine(_objectsDir, prefix[..2]);
        foreach (string file in _gate.EnumerateFiles(bucket, "*"))
        {
            string id = prefix[..2] + Path.GetFileName(file);
            if (id.Length == GitObjectId.HexLength && id.StartsWith(prefix, StringComparison.Ordinal) && !found.Contains(id))
                found.Add(id);
            if (found.Count >= limit)
                return found;
        }

        foreach (GitPackFile pack in Packs)
        {
            var fromPack = new List<string>();
            pack.CollectPrefix(prefix, fromPack, limit);
            foreach (string id in fromPack)
            {
                if (!found.Contains(id))
                    found.Add(id);
                if (found.Count >= limit)
                    return found;
            }
        }

        return found;
    }

    public void Dispose()
    {
        if (_packs == null)
            return;
        foreach (GitPackFile pack in _packs)
            pack.Dispose();
        _packs = null;
    }
}
