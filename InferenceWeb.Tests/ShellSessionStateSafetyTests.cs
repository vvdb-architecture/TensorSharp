// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;

namespace InferenceWeb.Tests;

/// <summary>
/// The shell state directory is writable by generated code. These tests pin the host
/// reader to files, links and FIFOs an adversarial command can leave there.
/// </summary>
public sealed class ShellSessionStateSafetyTests : IDisposable
{
    private readonly string _base;
    private readonly SessionWorkspaceManager _manager;
    private readonly SessionWorkspace _workspace;

    public ShellSessionStateSafetyTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "ts-shell-state-" + Guid.NewGuid().ToString("N"));
        _manager = new SessionWorkspaceManager(Path.Combine(_base, "sessions"));
        _workspace = _manager.GetOrCreate("s");
    }

    public void Dispose()
    {
        try { _manager.Release("s"); } catch { /* best effort */ }
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OrdinaryStateStillRestoresTheCurrentDirectory()
    {
        if (!ShellProgram.TryResolve(null, out ShellProgram? shell, out _))
            return;

        string child = Path.Combine(_workspace.WorkDirectory, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(CwdFile, child);

        var session = new ShellSession(_workspace, shell!);

        Assert.Equal(child, session.CurrentDirectory);
    }

    [Fact]
    public void BomEncodedStateRemainsReadableForWindowsPowerShell()
    {
        string path = Path.Combine(_workspace.ShellStateDirectory, "bom-state");
        File.WriteAllText(path, "C:\\workspace", Encoding.Unicode);

        Assert.True(ShellSession.TryReadStateText(path, out string text));
        Assert.Equal("C:\\workspace", text);
    }

    [Fact]
    public void StateSymlinkIsNotFollowed()
    {
        string target = Path.Combine(_base, "outside-state");
        File.WriteAllText(target, _workspace.WorkDirectory);
        if (!TryCreateFileSymlink(CwdFile, target))
            return; // Windows needs Developer Mode or an elevated token to create one.

        Assert.False(ShellSession.TryReadStateText(CwdFile, out _));
        Assert.False(ShellSession.TryReadBoundedRegularTextUnderRoot(
            _workspace.ShellStateDirectory, CwdFile, 128 * 1024, out _));
    }

    [Fact]
    public void ParentDirectorySymlinkCannotRedirectABoundedWorkspaceRead()
    {
        string outside = Path.Combine(_base, "outside-parent");
        string nested = Path.Combine(outside, "source.py");
        string link = Path.Combine(_workspace.ShellStateDirectory, "linked-parent");
        Directory.CreateDirectory(outside);
        File.WriteAllText(nested, "SECRET_OUTSIDE_SOURCE\n");
        if (!TryCreateDirectorySymlink(link, outside))
            return;

        Assert.False(ShellSession.TryReadBoundedRegularTextUnderRoot(
            _workspace.ShellStateDirectory,
            Path.Combine(link, "source.py"),
            128 * 1024,
            out string text));
        Assert.Empty(text);
    }

    [Fact]
    public void PersistedDirectorySymlinkCannotLeaveTheWorkspace()
    {
        if (!ShellProgram.TryResolve(null, out ShellProgram? shell, out _))
            return;

        string outside = Path.Combine(_base, "outside-directory");
        string link = Path.Combine(_workspace.WorkDirectory, "outside-link");
        Directory.CreateDirectory(outside);
        if (!TryCreateDirectorySymlink(link, outside))
            return; // Windows needs Developer Mode or an elevated token to create one.

        File.WriteAllText(CwdFile, link);
        var session = new ShellSession(_workspace, shell!);

        Assert.Equal(_workspace.WorkDirectory, session.CurrentDirectory);
    }

    [Fact]
    public void InvalidPersistedPathFallsBackWithoutThrowing()
    {
        if (!ShellProgram.TryResolve(null, out ShellProgram? shell, out _))
            return;

        File.WriteAllBytes(CwdFile, new byte[] { (byte)'x', 0, (byte)'y' });
        var session = new ShellSession(_workspace, shell!);

        Assert.Equal(_workspace.WorkDirectory, session.CurrentDirectory);
    }

    [Fact]
    public void OversizedStateIsRejectedUnderTheReadCap()
    {
        string path = Path.Combine(_workspace.ShellStateDirectory, "oversized-state");
        File.WriteAllBytes(path, new byte[1024 * 1024]);

        Assert.False(ShellSession.TryReadStateText(path, out _));
    }

    [Fact]
    public void RootAnchoredReaderAcceptsTheExactCapAndRejectsOneByteMore()
    {
        const int cap = 128 * 1024;
        string path = Path.Combine(_workspace.ShellStateDirectory, "at-cap");
        File.WriteAllText(path, new string('x', cap));

        Assert.True(ShellSession.TryReadBoundedRegularTextUnderRoot(
            _workspace.ShellStateDirectory, path, cap, out string text));
        Assert.Equal(cap, text.Length);

        File.AppendAllText(path, "x");
        Assert.False(ShellSession.TryReadBoundedRegularTextUnderRoot(
            _workspace.ShellStateDirectory, path, cap, out _));
    }

    [Fact]
    public void FifoStateIsRejectedWithoutWaitingForInput()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = Path.Combine(_workspace.ShellStateDirectory, "fifo-state");
        Assert.Equal(0, MkFifo(path, Convert.ToUInt32("600", 8)));

        // Holding a FIFO open read/write lets even a regressed blocking reader open it.
        // The test thread can then close the only writer after a bounded wait, guaranteeing
        // that a failed assertion cannot strand the process in File.ReadAllText.
        int flags = 2 | (OperatingSystem.IsMacOS() ? 0x00000004 | 0x01000000
                                                   : 0x00000800 | 0x00080000);
        int fd = OpenUnix(path, flags);
        Assert.True(fd >= 0, $"open failed with errno {Marshal.GetLastWin32Error()}");

        using var keeper = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        AssertFifoRejectedWithoutBlocking(
            () => ShellSession.TryReadStateText(path, out _), keeper, "state reader");
        AssertFifoRejectedWithoutBlocking(
            () => ShellSession.TryReadBoundedRegularTextUnderRoot(
                _workspace.ShellStateDirectory, path, 128 * 1024, out _),
            keeper,
            "root-anchored reader");
    }

    // ---- the host-side state API an in-process backend persists through ----------

    [Fact]
    public void SaveThenLoad_RoundTripsOrdinaryVariables_AndDropsWhatTheHostOwns()
    {
        string child = Path.Combine(_workspace.WorkDirectory, "child");
        Directory.CreateDirectory(child);
        var session = new ShellSession(_workspace, ShellProgram.InProcess());

        session.Save(child, new Dictionary<string, string>
        {
            ["FOO"] = "bar baz",
            ["QUOTED"] = "it's \"x\" and $y and `z`",
            ["MULTI"] = "line one\nline two",
            ["EMPTY"] = string.Empty,
            // The wrapper's filter, applied on the way out: none of these may persist.
            ["PATH"] = "/evil/bin",
            ["HOME"] = "/somewhere/else",
            ["LD_PRELOAD"] = "/lib/evil.so",
            ["DYLD_INSERT_LIBRARIES"] = "/lib/evil.dylib",
            ["HTTPS_PROXY"] = "http://proxy:3128",
            ["TS_SECRET"] = "1",
            // Not an identifier: no shell could export it either.
            ["1BAD"] = "x",
        });

        ShellState state = session.Load();

        Assert.Equal(child, state.CurrentDirectory);
        Assert.Equal(child, session.CurrentDirectory);
        Assert.Equal("child", session.CurrentDirectoryLabel);
        Assert.Equal("bar baz", state.Environment["FOO"]);
        Assert.Equal("it's \"x\" and $y and `z`", state.Environment["QUOTED"]);
        Assert.Equal("line one\nline two", state.Environment["MULTI"]);
        Assert.Equal(string.Empty, state.Environment["EMPTY"]);
        foreach (string dropped in new[] { "PATH", "HOME", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES", "HTTPS_PROXY", "TS_SECRET", "1BAD" })
            Assert.False(state.Environment.ContainsKey(dropped), dropped + " must not persist");
        Assert.False(session.TakeEnvironmentWasReset());

        // What was written is what a POSIX shell would source, and nothing was left behind.
        string envFile = Path.Combine(_workspace.ShellStateDirectory, "env.sh");
        Assert.All(File.ReadAllText(envFile).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("export ", StringComparison.Ordinal)),
            line => Assert.Matches("^export [A-Za-z_][A-Za-z0-9_]*=", line));
        Assert.Empty(Directory.GetFiles(_workspace.ShellStateDirectory, "*.tmp"));
    }

    [Fact]
    public void Load_ReadsBothShapesExportPEmits_AndFiltersOnTheWayIn()
    {
        var session = new ShellSession(_workspace, ShellProgram.InProcess());
        File.WriteAllText(Path.Combine(_workspace.ShellStateDirectory, "env.sh"),
            // bash: double quotes, backslash escapes for " \ $ `, a literal newline inside.
            "declare -x A=\"x\\\"y\\\\z\\$q\"\n"
            + "declare -x TWO=\"first\nsecond\"\n"
            // dash: single quotes, '\'' for an embedded quote.
            + "export B='it'\\''s'\n"
            // A readonly export, and a value with no quoting at all.
            + "declare -rx C=plain\n"
            // Exported without a value: nothing to restore.
            + "declare -x NOVAL\n"
            // Written by hand into the file: still filtered.
            + "declare -x PATH=\"/evil\"\n"
            + "export LD_PRELOAD='/x.so'\n");

        ShellState state = session.Load();

        Assert.Equal("x\"y\\z$q", state.Environment["A"]);
        Assert.Equal("first\nsecond", state.Environment["TWO"]);
        Assert.Equal("it's", state.Environment["B"]);
        Assert.Equal("plain", state.Environment["C"]);
        Assert.False(state.Environment.ContainsKey("NOVAL"));
        Assert.False(state.Environment.ContainsKey("PATH"));
        Assert.False(state.Environment.ContainsKey("LD_PRELOAD"));
        Assert.False(session.TakeEnvironmentWasReset());
    }

    [Fact]
    public void Load_OfAFileThatDoesNotParse_ResetsTheEnvironment_AndSaysSoOnce()
    {
        var session = new ShellSession(_workspace, ShellProgram.InProcess());
        string envFile = Path.Combine(_workspace.ShellStateDirectory, "env.sh");
        File.WriteAllText(envFile, "export GOOD='kept'\nexport BROKEN=\"unterminated\n");

        ShellState state = session.Load();

        // The wrapper's own verdict: a file that will not source costs the whole
        // environment, never half of it.
        Assert.Empty(state.Environment);
        Assert.Equal(string.Empty, File.ReadAllText(envFile));
        Assert.True(session.TakeEnvironmentWasReset());
        Assert.False(session.TakeEnvironmentWasReset());
    }

    [Fact]
    public void MarkEnvironmentReset_IsReadBackExactlyOnce()
    {
        var session = new ShellSession(_workspace, ShellProgram.InProcess());

        Assert.False(session.TakeEnvironmentWasReset());
        Assert.True(session.MarkEnvironmentReset());
        Assert.True(session.TakeEnvironmentWasReset());
        Assert.False(session.TakeEnvironmentWasReset());
    }

    [Fact]
    public void Save_ReplacesAPlantedLink_InsteadOfWritingThroughIt()
    {
        string target = Path.Combine(_base, "outside-target");
        File.WriteAllText(target, "untouched");
        string envFile = Path.Combine(_workspace.ShellStateDirectory, "env.sh");
        if (!TryCreateFileSymlink(envFile, target))
            return; // Windows needs Developer Mode or an elevated token to create one.

        var session = new ShellSession(_workspace, ShellProgram.InProcess());
        session.Save(_workspace.WorkDirectory, new Dictionary<string, string> { ["X"] = "1" });

        Assert.Equal("untouched", File.ReadAllText(target));
        Assert.Null(new FileInfo(envFile).LinkTarget);
        Assert.Equal("1", session.Load().Environment["X"]);
    }

    [Fact]
    public void Save_WritesAPowerShellSessionsFile_InItsOwnShape()
    {
        var session = new ShellSession(_workspace, ShellProgram.InProcess("pwsh", ShellKind.PowerShell));
        session.Save(_workspace.WorkDirectory, new Dictionary<string, string>
        {
            ["FOO"] = "bar",
            ["MULTI"] = "a\nb",       // cannot live in a line-per-variable file
            ["Path"] = "C:\\evil",    // the PowerShell wrapper's own filter
        });

        ShellState state = session.Load();
        Assert.Equal("bar", state.Environment["FOO"]);
        Assert.False(state.Environment.ContainsKey("MULTI"));
        Assert.False(state.Environment.ContainsKey("Path"));
        Assert.Equal("FOO=bar\n", File.ReadAllText(Path.Combine(_workspace.ShellStateDirectory, "env.txt")));
    }

    [Fact]
    public void TheDarwinFStatFallback_AgreesWithTheRuntimeShim_AndItsLayoutIsPinned()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // The struct is Darwin's __DARWIN_STRUCT_STAT64: 144 bytes with st_size at 96.
        // A wrong field order would still compile and silently fail the length check.
        Assert.Equal(144, Marshal.SizeOf<ShellSession.DarwinStat64>());
        Assert.Equal(96, (int)Marshal.OffsetOf<ShellSession.DarwinStat64>(nameof(ShellSession.DarwinStat64.Size)));
        Assert.Equal(4, (int)Marshal.OffsetOf<ShellSession.DarwinStat64>(nameof(ShellSession.DarwinStat64.Mode)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ShellSession.DarwinStat64>(nameof(ShellSession.DarwinStat64.ATimeSec)));

        string path = Path.Combine(_workspace.ShellStateDirectory, "stat-probe");
        File.WriteAllText(path, new string('x', 1234));
        using SafeFileHandle handle = File.OpenHandle(path);

        Assert.True(ShellSession.TryFStatDarwin(handle, out ShellSession.UnixFileStatus status));
        Assert.Equal(1234, status.Size);
        Assert.Equal(0x8000, status.Mode & 0xF000);
        Assert.True(status.Ino != 0);
        Assert.True(status.MTime > 0);

        // And the reader still works through whichever path it took.
        Assert.True(ShellSession.TryReadStateText(path, out string text));
        Assert.Equal(1234, text.Length);
    }

    private static void AssertFifoRejectedWithoutBlocking(
        Func<bool> read, SafeFileHandle keeper, string readerName)
    {
        bool accepted = false;
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                accepted = read();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true };

        reader.Start();
        bool completedWithoutRescue = reader.Join(TimeSpan.FromSeconds(2));
        if (!completedWithoutRescue)
        {
            // Closing the FIFO's only writer makes a legacy blocking read see EOF. The
            // reader is a background thread as a final guard, so even a broken rescue
            // cannot keep the test process alive.
            keeper.Dispose();
            Assert.True(reader.Join(TimeSpan.FromSeconds(2)),
                "the blocking FIFO reader could not be rescued");
        }

        Assert.Null(failure);
        Assert.True(completedWithoutRescue, $"the {readerName} blocked on a FIFO");
        Assert.False(accepted);
    }

    [Fact]
    public async Task ConcurrentLeafReplacementReturnsOneCompleteVersion()
    {
        string path = Path.Combine(_workspace.ShellStateDirectory, "replaced-source");
        string first = new('a', 64 * 1024);
        string second = new('b', 64 * 1024);
        File.WriteAllText(path, first);

        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            int sequence = 0;
            while (!stop.IsCancellationRequested)
            {
                string replacement = path + "."
                    + (sequence++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(replacement, (sequence & 1) == 0 ? first : second);
                try
                {
                    File.Move(replacement, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(replacement); } catch { /* best effort */ }
                }
            }
        });

        try
        {
            for (int i = 0; i < 100; i++)
            {
                bool read = ShellSession.TryReadBoundedRegularTextUnderRoot(
                    _workspace.ShellStateDirectory, path, 128 * 1024, out string text);
                Assert.True(!read || text == first || text == second,
                    "a path replacement must never produce mixed or partial content");
            }
        }
        finally
        {
            stop.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConcurrentParentSwapNeverReadsThroughTheOutsideSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = _workspace.ShellStateDirectory;
        string outside = Path.Combine(_base, "swap-outside");
        string active = Path.Combine(root, "swap-parent");
        string parked = Path.Combine(root, "swap-parent-parked");
        string link = Path.Combine(root, "swap-parent-link");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "source.py"), "INSIDE\n");
        File.WriteAllText(Path.Combine(outside, "source.py"), "SECRET_OUTSIDE\n");
        if (!TryCreateDirectorySymlink(link, outside))
            return;

        using var stop = new CancellationTokenSource();
        Task toggler = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Rename(active, parked);
                Rename(link, active);
                Rename(active, link);
                Rename(parked, active);
                Thread.Yield();
            }
        });

        try
        {
            string source = Path.Combine(active, "source.py");
            for (int i = 0; i < 500; i++)
            {
                bool read = ShellSession.TryReadBoundedRegularTextUnderRoot(
                    root, source, 128 * 1024, out string text);
                Assert.True(!read || text == "INSIDE\n",
                    "a parent swap redirected a workspace read outside its root");
            }
        }
        finally
        {
            stop.Cancel();
            await toggler.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private string CwdFile => Path.Combine(_workspace.ShellStateDirectory, "cwd");

    private static bool TryCreateFileSymlink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static void Rename(string from, string to)
    {
        if (RenameUnix(from, to) != 0)
            throw new IOException($"rename failed with errno {Marshal.GetLastWin32Error()}");
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int RenameUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
}
