// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// The only door this git implementation has to the filesystem.
///
/// <para>
/// Every other builtin calls <c>exec.ResolveRead</c> / <c>exec.ResolveWrite</c> at each
/// of its two or three touch points, and that is fine when there are two or three. Git
/// has dozens — objects, packs, the index, refs, packed-refs, config, reflogs, HEAD, and
/// the whole working tree — spread over eight files, and "remember to resolve" would be
/// a rule that eventually gets forgotten in one of them. So the repository is handed
/// this object instead of a path, and there is no <c>File</c> call anywhere below it
/// that did not come through <see cref="Read"/> or <see cref="Write"/>.
/// </para>
/// <para>
/// The practical consequences are the ones that matter: <c>git init /etc/evil</c>,
/// <c>git -C /outside status</c>, and a repository whose <c>.git</c> is a symlink out of
/// the session all fail with <c>Permission denied</c>, because
/// <see cref="ConfinedPaths.Resolve"/> resolves links before it compares roots.
/// </para>
/// </summary>
internal sealed class GitFileGate
{
    private readonly ConfinedPaths _paths;
    private readonly string _cwd;

    public GitFileGate(ConfinedPaths paths, string cwd)
    {
        _paths = paths;
        _cwd = cwd;
    }

    /// <summary>The real path, or <see cref="ConfinementException"/> when reading it is not allowed.</summary>
    public string Read(string path) => _paths.Resolve(path, _cwd, PathAccess.Read);

    /// <summary>The real path, or <see cref="ConfinementException"/> when writing it is not allowed.</summary>
    public string Write(string path) => _paths.Resolve(path, _cwd, PathAccess.Write);

    /// <summary>Non-throwing <see cref="Read"/>, for the walk up the tree looking for <c>.git</c>.</summary>
    public bool TryRead(string path, out string resolved) => _paths.TryResolve(path, _cwd, PathAccess.Read, out resolved, out _);

    public bool CanRead(string path) => _paths.IsAllowed(ConfinedPaths.RealPath(path), PathAccess.Read);

    public bool CanWrite(string path) => _paths.IsAllowed(ConfinedPaths.RealPath(path), PathAccess.Write);

    /// <summary>
    /// Confines a path by its <em>parent</em>, leaving the final component unfollowed.
    ///
    /// <para>
    /// <see cref="Read"/> and <see cref="Write"/> resolve links all the way down, which is
    /// exactly right when the caller wants the file's bytes — it is what stops a link
    /// inside the work tree from reaching <c>/etc</c>. It is wrong for the one operation
    /// git performs on a symlink: recording it. Resolving first turns
    /// <c>git add link.txt</c> into "copy the target's content into a blob under the
    /// link's name", which loses the link and quietly inlines a file from outside the work
    /// tree.
    /// </para>
    /// <para>
    /// So this resolves the directory chain — every link on the way down is followed and
    /// the result must sit inside the policy's roots — and then re-appends the last
    /// component verbatim. Confinement still holds, because the containing directory was
    /// checked; the leaf is merely <c>lstat</c>-ed rather than opened, which is precisely
    /// what git does.
    /// </para>
    /// </summary>
    public string ResolveLeaf(string path, PathAccess access)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_cwd, path));
        string? parent = Path.GetDirectoryName(full);
        string name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(parent) || name.Length == 0)
            return access == PathAccess.Write ? Write(full) : Read(full);
        string realParent = access == PathAccess.Write ? Write(parent) : Read(parent);
        return Path.Combine(realParent, name);
    }

    /// <summary>Non-throwing <see cref="ResolveLeaf"/>.</summary>
    public bool TryResolveLeaf(string path, PathAccess access, out string resolved)
    {
        try
        {
            resolved = ResolveLeaf(path, access);
            return true;
        }
        catch (ConfinementException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    // ---- the small file operations the repository needs, each pre-resolved -------------

    public bool FileExists(string path) => TryRead(path, out string real) && File.Exists(real);

    public bool DirectoryExists(string path) => TryRead(path, out string real) && Directory.Exists(real);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(Read(path));

    public string ReadAllText(string path) => File.ReadAllText(Read(path));

    public string[] ReadAllLines(string path) => File.ReadAllLines(Read(path));

    public void CreateDirectory(string path) => Directory.CreateDirectory(Write(path));

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        if (!TryRead(directory, out string real) || !Directory.Exists(real))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(real, pattern);
    }

    public IEnumerable<string> EnumerateEntries(string directory)
    {
        if (!TryRead(directory, out string real) || !Directory.Exists(real))
            return Array.Empty<string>();
        return Directory.EnumerateFileSystemEntries(real);
    }

    public void Delete(string path)
    {
        string real = Write(path);
        if (File.Exists(real))
            File.Delete(real);
    }

    /// <summary>
    /// Writes through a sibling temporary file and a rename.
    ///
    /// <para>
    /// Git does this for every ref and for the index, and the reason applies here too: a
    /// run that is cancelled — which this shell does on a timeout, mid-builtin — must not
    /// leave a half-written <c>.git/index</c> behind. A rename within one directory is
    /// atomic, so a reader sees either the old file or the new one.
    /// </para>
    /// </summary>
    public void WriteAllBytesAtomic(string path, byte[] content)
    {
        string real = Write(path);
        string temp = real + ".tmp" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Write(temp);
        string? directory = Path.GetDirectoryName(real);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(Write(directory));
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, real, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* the write already failed; the temp file is best-effort */ }
            throw;
        }
    }

    public void WriteAllTextAtomic(string path, string content)
        => WriteAllBytesAtomic(path, ShellText.Utf8.GetBytes(content));

    public void AppendAllText(string path, string content)
    {
        string real = Write(path);
        string? directory = Path.GetDirectoryName(real);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(Write(directory));
        File.AppendAllText(real, content);
    }
}
