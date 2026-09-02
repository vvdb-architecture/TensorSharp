// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.Sandbox;

/// <summary>
/// What a run is allowed to do. One record, shared by the shell, the embedded
/// Python and the JavaScript engine, so that the three runtimes cannot disagree
/// about where a file may be written or whether the network exists.
///
/// <para>
/// On iOS there is no OS sandbox to lean on — no sandbox-exec, no bubblewrap, and
/// no child process to confine in the first place. The confinement therefore lives
/// in the runtimes themselves: every path the shell resolves goes through
/// <see cref="ConfinedPaths"/>, every Python file operation through an audit hook,
/// every JavaScript <c>fs</c> call through the same path rules. This record is the
/// single source of those rules.
/// </para>
/// </summary>
/// <param name="AllowScripts">Whether interpreters (Python, JavaScript, nested shells) may run at all.</param>
/// <param name="AllowNetwork">Whether anything may open a socket. Off means every attempt is refused with <see cref="NetworkDisabledMessage"/>.</param>
/// <param name="WorkRoot">The only tree a command may write into (besides <paramref name="TempRoot"/>); also the default working directory.</param>
/// <param name="ReadableRoots">Trees that may be read but not written — skill directories, staged inputs.</param>
/// <param name="TempRoot">A scratch tree that is writable like <paramref name="WorkRoot"/> and is what <c>$TMPDIR</c> points at.</param>
public sealed record ExecutionPolicy(
    bool AllowScripts,
    bool AllowNetwork,
    string WorkRoot,
    IReadOnlyList<string> ReadableRoots,
    string TempRoot)
{
    /// <summary>The wording every runtime uses when the network is off, so the model reads one sentence everywhere.</summary>
    public const string NetworkDisabledMessage = "network access is disabled by the user";

    /// <summary>The wording every runtime uses when scripts are off.</summary>
    public const string ScriptsDisabledMessage = "running scripts is disabled by the user";

    /// <summary>The wording for anything that would have needed a child process.</summary>
    public const string ProcessesUnavailableMessage = "this host cannot start programs";

    /// <summary>The AgentHost wording for a `pip install` typed by the model.</summary>
    public const string InstallsByHostMessage = "installs are performed by the host";

    /// <summary>Per-stream cap on captured output; the middle is dropped, both ends kept.</summary>
    public int MaxOutputBytes { get; init; } = 32 * 1024;

    /// <summary>Applied when a launch names no timeout of its own.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Hosts the runtimes may reach when <see cref="AllowNetwork"/> is on. Empty means
    /// any host. Matched by exact name or as a parent domain (<c>pypi.org</c> allows
    /// <c>files.pypi.org</c>).
    /// </summary>
    public IReadOnlyList<string> NetworkHosts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The session's package directory (what <c>pip --target</c> would fill). Readable
    /// by the interpreters and written only by the host's installer; null when the
    /// session has none.
    /// </summary>
    public string? PackageRoot { get; init; }

    /// <summary>
    /// Individual paths that may be written even though they sit outside
    /// <see cref="WorkRoot"/> and <see cref="TempRoot"/>.
    ///
    /// <para>
    /// The agent host names these one at a time rather than opening a tree: a skill's
    /// own output file, the session's state files. Keeping them as exact paths rather
    /// than as roots is the point — granting the parent directory would grant every
    /// sibling with it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> WritablePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// With <see cref="AllowNetwork"/> off, the one loopback port that is still
    /// reachable: the host's egress proxy, which an installer is pointed at so that a
    /// package download can be permitted without permitting the internet.
    /// </summary>
    public int? AllowLoopbackPort { get; init; }

    /// <summary>True when <paramref name="host"/> is reachable under this policy.</summary>
    public bool IsHostAllowed(string host)
    {
        if (!AllowNetwork || string.IsNullOrWhiteSpace(host))
            return false;
        if (NetworkHosts.Count == 0)
            return true;
        foreach (string allowed in NetworkHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Every root a runtime may read from: the writable ones plus the read-only ones.</summary>
    public IEnumerable<string> AllReadableRoots()
    {
        yield return WorkRoot;
        yield return TempRoot;
        foreach (string root in ReadableRoots)
            yield return root;
        if (!string.IsNullOrEmpty(PackageRoot))
            yield return PackageRoot;
    }
}
