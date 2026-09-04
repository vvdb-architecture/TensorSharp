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
using TensorAgent.Core.Python;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Tests;

/// <summary>
/// Marks a test that needs a real CPython to say anything.
///
/// <para>
/// The build machine has none — the interpreter this component loads is staged
/// into an iOS app bundle — so these are skipped with the command that enables
/// them rather than passing vacuously. A test that returns early when the thing
/// it tests is absent is a test that reports success for an untested feature,
/// which is worse than no test.
/// </para>
/// </summary>
public sealed class LivePythonFactAttribute : FactAttribute
{
    /// <summary>The environment variable that points at a staged runtime or a CPython prefix.</summary>
    public const string RootVariable = "TENSORAGENT_PYTHON_ROOT";

    public LivePythonFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootVariable)))
        {
            Skip = $"needs an embedded CPython: set {RootVariable}=<TensorAgent/python-runtime/simulator "
                + "or a CPython prefix with lib/python3.13 and libpython3.13.dylib> and re-run";
        }
    }
}

/// <summary>
/// Marks a test that needs the runtime <c>prepare-python.sh</c> STAGES, rather than
/// any CPython 3.13.
///
/// <para>
/// The distinction is not pedantry, and getting it wrong costs a red suite that says
/// nothing about the product: a machine that points
/// <c>TENSORAGENT_PYTHON_ROOT</c> at a Homebrew prefix satisfies
/// <see cref="LivePythonFactAttribute"/> and then fails four tests whose whole subject
/// is what the STAGING put there — reportlab, openpyxl, pypdf, Pillow, defusedxml and
/// the certificate bundle. That is the machine not being ready, and it should say so
/// in the skip rather than in an assertion about a PDF.
/// </para>
/// </summary>
public sealed class LiveStagedPythonFactAttribute : FactAttribute
{
    /// <summary>What the staged runtime carries and a bare interpreter does not.</summary>
    private static readonly string[] Staged = ["reportlab", "openpyxl", "pypdf", "PIL", "defusedxml", "certifi"];

    public LiveStagedPythonFactAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            Skip = $"needs the staged runtime: set {LivePythonFactAttribute.RootVariable}="
                + "<TensorAgent/python-runtime/simulator or another prefix produced by "
                + "TensorAgent/scripts/prepare-python.sh> and re-run";
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 6,
            IgnoreInaccessible = true,
        };
        string[] missing = Staged.Where(name =>
        {
            try { return !Directory.EnumerateDirectories(root, name, options).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }).ToArray();

        if (missing.Length > 0)
        {
            Skip = $"{root} is a CPython, not a runtime staged by prepare-python.sh: "
                + string.Join(", ", missing) + " are not under it, and these tests are about "
                + "exactly what the staging puts there";
        }
    }
}

/// <summary>
/// What can be tested about the embedded interpreter on a machine that does not
/// have one: the sandbox it generates, the decisions it makes before Python is
/// involved, the honesty of its unavailability, and the whole of the installer
/// except the two lines that talk to PyPI.
/// </summary>
[Collection(LivePythonCollection.Name)]
public sealed class EmbeddedPythonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-py-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _temp;
    private readonly string _readable;
    private readonly string _packages;
    private readonly string _outside;

    public EmbeddedPythonTests()
    {
        _work = Path.Combine(_root, "work");
        _temp = Path.Combine(_root, "tmp");
        _readable = Path.Combine(_root, "skill");
        _packages = Path.Combine(_root, "packages");
        _outside = Path.Combine(_root, "outside");
        foreach (string directory in new[] { _work, _temp, _readable, _packages, _outside })
            Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private ExecutionPolicy Policy(bool network = false, bool scripts = true, bool packages = true) => new(
        AllowScripts: scripts,
        AllowNetwork: network,
        WorkRoot: _work,
        ReadableRoots: [_readable],
        TempRoot: _temp)
    {
        PackageRoot = packages ? _packages : null,
        DefaultTimeout = TimeSpan.FromSeconds(20),
    };

    private InterpreterContext Context(ExecutionPolicy? policy = null) =>
        new(_work, new Dictionary<string, string> { ["HOME"] = _work }, policy ?? Policy());

    private static string Real(string path) => ConfinedPaths.RealPath(path);

    // =====================================================================================
    // the sandbox source, per policy shape
    // =====================================================================================

    [Fact]
    public void TheSandboxMirrorsTheWritableAndReadableRoots()
    {
        string source = PythonBootstrap.CreateSandboxSource(Policy());

        // Writable is exactly the work and temp roots, as real paths: the hook
        // compares against realpath(), so the literals have to be real too.
        Assert.Contains($"        '{Real(_work)}',", source);
        Assert.Contains($"        '{Real(_temp)}',", source);
        Assert.Contains($"        '{Real(_readable)}',", source);
        Assert.Contains($"        '{Real(_packages)}',", source);

        // The read-only roots must not appear in the writable list. Take the
        // slice between the two keywords and look there.
        string writable = Between(source, "writable=[", "]");
        Assert.Contains(Real(_work), writable);
        Assert.Contains(Real(_temp), writable);
        Assert.DoesNotContain(Real(_readable), writable);
        Assert.DoesNotContain(Real(_packages), writable);
    }

    [Fact]
    public void TheSandboxCarriesTheNetworkSwitchBothWays()
    {
        Assert.Contains("network=False,", PythonBootstrap.CreateSandboxSource(Policy(network: false)));
        Assert.Contains("network=True,", PythonBootstrap.CreateSandboxSource(Policy(network: true)));
    }

    [Fact]
    public void TheSandboxHasNoPackageRootWhenTheSessionHasNone()
    {
        string source = PythonBootstrap.CreateSandboxSource(Policy(packages: false));
        Assert.DoesNotContain(Real(_packages), source);
        Assert.Contains(Real(_readable), source);
    }

    [Fact]
    public void TheSandboxAddsTheRuntimesOwnTreesToTheReadableRoots()
    {
        string stdlib = Path.Combine(_root, "runtime", "lib", "python3.13");
        Directory.CreateDirectory(stdlib);
        string source = PythonBootstrap.CreateSandboxSource(Policy(), [stdlib]);

        // Without this the interpreter cannot import its own standard library.
        Assert.Contains(Real(stdlib), Between(source, "readable=[", "]"));
        Assert.DoesNotContain(Real(stdlib), Between(source, "writable=[", "]"));
    }

    [Fact]
    public void TheSandboxIsInstalledAsAnAuditHookAndResolvesSymlinks()
    {
        string source = PythonBootstrap.CreateSandboxSource(Policy());
        Assert.Contains("sys.addaudithook(_hook)", source);
        // A prefix test on the literal path would be defeated by a symlink; the
        // hook has to resolve first, exactly as ConfinedPaths does.
        Assert.Contains("os.path.realpath(text)", source);
        // Fails closed: a run that never got a policy can touch nothing.
        Assert.Contains("'writable': (), 'readable': (), 'network': False", source);
    }

    [Fact]
    public void TheSandboxRefusesChildProcessesUnderEveryPolicy()
    {
        foreach (ExecutionPolicy policy in new[] { Policy(network: false), Policy(network: true) })
        {
            string source = PythonBootstrap.CreateSandboxSource(policy);
            foreach (string denied in new[] { "'os.system'", "'os.exec'", "'os.fork'", "'os.posix_spawn'", "'os.spawn'", "'subprocess.Popen'" })
                Assert.Contains(denied, source);
            Assert.Contains(ExecutionPolicy.ProcessesUnavailableMessage, source);
            Assert.Contains("iOS does not let an app start another program", source);
        }
    }

    [Fact]
    public void TheSandboxRefusesNativeLibraryLoadsUnderEveryPolicy()
    {
        foreach (ExecutionPolicy policy in new[] { Policy(network: false), Policy(network: true) })
        {
            string source = PythonBootstrap.CreateSandboxSource(policy);
            // Symbol lookup and the call gate stay refused outright: a handle that
            // somehow appears still cannot be used to reach native code.
            Assert.Contains("'ctypes.dlsym'", source);
            Assert.Contains("'ctypes.call_function'", source);
            Assert.Contains("step around every other check", source);
        }
    }

    [Fact]
    public void ALibraryLoadIsAllowedOnlyFromTheAppsOwnBundle()
    {
        // A blanket refusal of dlopen does not stop an attacker on this platform: it
        // stops `import numpy`. Every compiled extension module is a signed framework
        // that the import machinery loads by dlopen, so the rule has to be about which
        // library, and the bundle root is what decides.
        string? previous = PythonBootstrap.BundleRoot;
        try
        {
            PythonBootstrap.BundleRoot = Path.Combine(_root, "bundle");
            Directory.CreateDirectory(PythonBootstrap.BundleRoot);
            string source = PythonBootstrap.CreateSandboxSource(Policy());

            Assert.Contains("def _check_dlopen", source);
            Assert.Contains("bundle=", source);
            Assert.Contains(PythonBootstrap.Literal(PythonBootstrap.BundleRoot), source);
            // The decision is made on the resolved path, so a symlink laid inside the
            // bundle cannot point at a library outside it.
            Assert.Contains("os.path.realpath", source);
            Assert.Contains("text.startswith(bundle + os.sep)", source);
        }
        finally
        {
            PythonBootstrap.BundleRoot = previous;
        }
    }

    [Fact]
    public void AHandleToTheMainProgramIsAllowedBecauseItLoadsNothing()
    {
        // dlopen(NULL) returns a handle to the image already running. numpy and
        // Pillow both ask for one while probing during import, and refusing it makes
        // them unimportable while preventing nothing: no new code enters the process.
        string source = PythonBootstrap.CreateSandboxSource(Policy());
        Assert.Contains("if name is None or name == '':", source);
        // What keeps that safe is the pair that is still refused outright.
        Assert.Contains("'ctypes.dlsym'", source);
        Assert.Contains("'ctypes.call_function'", source);
    }

    [Fact]
    public void WithNoBundleEveryLibraryLoadStaysRefused()
    {
        string? previous = PythonBootstrap.BundleRoot;
        try
        {
            PythonBootstrap.BundleRoot = null;
            string source = PythonBootstrap.CreateSandboxSource(Policy());
            Assert.Contains("bundle=''", source);
            // With no bundle the prefix test can never pass, so every named library is
            // refused and the message says why rather than leaving the reader guessing.
            Assert.Contains("if bundle and (text == bundle or text.startswith(bundle + os.sep)):", source);
            Assert.Contains("This host named no bundle", source);
        }
        finally
        {
            PythonBootstrap.BundleRoot = previous;
        }
    }

    [Fact]
    public void TheSandboxUsesTheSharedWordingWhenTheNetworkIsOff()
    {
        string source = PythonBootstrap.CreateSandboxSource(Policy(network: false));
        Assert.Contains(ExecutionPolicy.NetworkDisabledMessage, source);
        // The families that reach a socket one way or another.
        foreach (string family in new[] { "'socket.'", "'urllib.'", "'http.client.'" })
            Assert.Contains(family, source);
    }

    [Fact]
    public void TheSandboxChecksTheCallsThatOpenNothing()
    {
        // open() is not the only way to touch a file; these are audited too and
        // a sandbox that only watched open() would be trivially walked around.
        string source = PythonBootstrap.CreateSandboxSource(Policy());
        foreach (string audited in new[] { "'os.mkdir'", "'os.remove'", "'os.rename'", "'os.symlink'", "'os.listdir'", "'os.chdir'" })
            Assert.Contains(audited, source);
    }

    [Fact]
    public void PathLiteralsSurviveQuotesAndBackslashes()
    {
        Assert.Equal(@"'a\\b'", PythonBootstrap.Literal(@"a\b"));
        Assert.Equal(@"'it\'s'", PythonBootstrap.Literal("it's"));
        Assert.Equal(@"'a\nb'", PythonBootstrap.Literal("a\nb"));
    }

    // =====================================================================================
    // the path decision the host makes before Python is involved
    // =====================================================================================

    [Fact]
    public void APathUnderTheWorkRootIsReadableAndWritable()
    {
        var confined = new ConfinedPaths(Policy());
        File.WriteAllText(Path.Combine(_work, "a.txt"), "x");
        Assert.True(PythonBootstrap.IsPathAllowed(confined, "a.txt", _work, forWrite: false));
        Assert.True(PythonBootstrap.IsPathAllowed(confined, "a.txt", _work, forWrite: true));
        Assert.True(PythonBootstrap.IsPathAllowed(confined, "new.txt", _work, forWrite: true));
    }

    [Fact]
    public void AReadOnlyRootIsReadableButNotWritable()
    {
        var confined = new ConfinedPaths(Policy());
        string file = Path.Combine(_readable, "skill.py");
        File.WriteAllText(file, "x");
        Assert.True(PythonBootstrap.IsPathAllowed(confined, file, _work, forWrite: false));
        Assert.False(PythonBootstrap.IsPathAllowed(confined, file, _work, forWrite: true));
    }

    [Fact]
    public void APathOutsideEveryRootIsRefused()
    {
        var confined = new ConfinedPaths(Policy());
        string file = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(file, "x");
        Assert.False(PythonBootstrap.IsPathAllowed(confined, file, _work, forWrite: false));
        Assert.False(PythonBootstrap.IsPathAllowed(confined, "/etc/hosts", _work, forWrite: false));
    }

    [Fact]
    public void ASymlinkOutOfTheSandboxIsRefused()
    {
        // The reason the check resolves before it compares: this path is
        // lexically inside the work root and physically is not.
        string target = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(target, "x");
        File.CreateSymbolicLink(Path.Combine(_work, "link.txt"), target);

        var confined = new ConfinedPaths(Policy());
        Assert.False(PythonBootstrap.IsPathAllowed(confined, "link.txt", _work, forWrite: false));
        Assert.False(PythonBootstrap.IsPathAllowed(confined, "link.txt", _work, forWrite: true));
    }

    [Fact]
    public void ADirectorySymlinkOutOfTheSandboxIsRefused()
    {
        Directory.CreateSymbolicLink(Path.Combine(_work, "escape"), _outside);
        var confined = new ConfinedPaths(Policy());
        Assert.False(PythonBootstrap.IsPathAllowed(confined, "escape/new.txt", _work, forWrite: true));
    }

    [Fact]
    public void TheRefusalNamesTheRootsTheRunActuallyHas()
    {
        var confined = new ConfinedPaths(Policy());
        string message = PythonBootstrap.DeniedMessage("/etc/hosts", forWrite: false, confined.ReadableRoots);
        Assert.Contains("/etc/hosts: Permission denied", message);
        Assert.Contains(Real(_work), message);
        Assert.Contains("read under", message);
        Assert.Contains("write under", PythonBootstrap.DeniedMessage("/etc/x", true, confined.WritableRoots));
        Assert.Contains("may not touch the file system", PythonBootstrap.DeniedMessage("/etc/x", true, []));
    }

    // =====================================================================================
    // honesty about not being there
    // =====================================================================================

    [Fact]
    public void AMissingRuntimeRootIsReportedByName()
    {
        string missing = Path.Combine(_root, "no-such-runtime");
        var python = new EmbeddedPython(missing);

        Assert.False(python.IsAvailable);
        Assert.NotNull(python.UnavailableReason);
        Assert.Contains(missing, python.UnavailableReason);
        Assert.Contains("does not exist", python.UnavailableReason);
        Assert.Equal(string.Empty, python.Version);
    }

    [Fact]
    public void ARootWithoutAStandardLibraryNamesWhatItLooksFor()
    {
        // The directory is there; what makes a Python runtime is not.
        string empty = Path.Combine(_root, "empty-runtime");
        Directory.CreateDirectory(empty);
        var python = new EmbeddedPython(empty);

        Assert.False(python.IsAvailable);
        Assert.NotNull(python.UnavailableReason);
        Assert.Contains(empty, python.UnavailableReason);
        Assert.Contains("tensoragent-python.json", python.UnavailableReason);
        Assert.Contains("lib/python3.13", python.UnavailableReason);
    }

    [Fact]
    public void AManifestPointingAtNothingIsReportedRatherThanGuessedAround()
    {
        string root = Path.Combine(_root, "broken-runtime");
        Directory.CreateDirectory(Path.Combine(root, "python"));
        File.WriteAllText(
            Path.Combine(root, "python", "tensoragent-python.json"),
            """{ "version": "3.13", "stdlib": "python/lib/python3.13", "packages": "python/app_packages", "layout": 1 }""");

        var python = new EmbeddedPython(root);
        Assert.False(python.IsAvailable);
        Assert.Contains("is not there", python.UnavailableReason!);
        Assert.Contains("python3.13", python.UnavailableReason!);
    }

    [Fact]
    public async Task RunningWithoutAnInterpreterFailsWithTheReasonAndCommandNotFound()
    {
        var python = new EmbeddedPython(Path.Combine(_root, "no-such-runtime"));
        ExecutionResult result = await python.RunCodeAsync("print(1)", [], Context(), CancellationToken.None);

        Assert.Equal(ExecutionResult.CommandNotFoundExitCode, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("does not exist", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task RunningIsRefusedBeforeAnythingElseWhenScriptsAreOff()
    {
        // Checked ahead of availability on purpose: "scripts are off" is the
        // true answer even on a host that does have an interpreter.
        var python = new EmbeddedPython(Path.Combine(_root, "no-such-runtime"));
        ExecutionResult result = await python.RunCodeAsync("print(1)", [], Context(Policy(scripts: false)), CancellationToken.None);

        Assert.Equal(126, result.ExitCode);
        Assert.Contains(ExecutionPolicy.ScriptsDisabledMessage, result.Stderr);
    }

    [Fact]
    public async Task ASyntaxCheckWithoutAnInterpreterSaysSoRatherThanPassing()
    {
        var python = new EmbeddedPython(Path.Combine(_root, "no-such-runtime"));
        string script = Path.Combine(_work, "s.py");
        File.WriteAllText(script, "print(");

        SyntaxCheckResult result = await python.CheckSyntaxAsync(script, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(script, result.Message!);
        Assert.Contains("does not exist", result.Message!);
    }

    /// <summary>
    /// Every caller of an interpreter that is still starting must be made to wait for
    /// the answer, not handed "there is none".
    ///
    /// <para>
    /// This is the bug behind "the model refuses to use the shell tool". The
    /// initialization flag was published BEFORE the work rather than after it, so the
    /// fast path outside the lock was a window into a half-started interpreter: a second
    /// caller arriving while <c>Py_Initialize</c> was running saw "already tried", found
    /// no interpreter and no reason, and reported unavailable. The app opens that window
    /// on every launch — the page fetches <c>/api/agent/engine</c> as it loads, which
    /// asks for the version and starts CPython, while the startup self-test runs
    /// <c>python3</c> on its own thread — and it was observed doing exactly that in the
    /// simulator: four self-test checks saying "no Python interpreter is embedded in this
    /// build" from a build whose engine line, moments later, read "python 3.13.14". A
    /// model told that on the first command of a session stops reaching for the shell.
    /// </para>
    /// <para>
    /// The invariant asserted here is the one that was broken and holds whether or not a
    /// real runtime is present: unavailable is never silent. Every observation either
    /// finds an interpreter or says why there is none.
    /// </para>
    /// </summary>
    [Fact]
    public void AskingFromEveryThreadAtOnceNeverProducesASilentUnavailable()
    {
        var python = new EmbeddedPython(Path.Combine(_root, "no-such-runtime"));
        var answers = new System.Collections.Concurrent.ConcurrentBag<(bool Available, string? Reason)>();

        using var start = new Barrier(32);
        Parallel.For(0, 32, _ =>
        {
            start.SignalAndWait();
            answers.Add((python.IsAvailable, python.UnavailableReason));
        });

        Assert.Equal(32, answers.Count);
        foreach ((bool available, string? reason) in answers)
        {
            Assert.False(available);
            Assert.False(string.IsNullOrWhiteSpace(reason),
                "a caller was told there is no interpreter and given no reason, which is the "
                + "shape of a half-initialized runtime being reported as a missing one");
        }
    }

    /// <summary>
    /// The same race with a real interpreter, where the window is hundreds of
    /// milliseconds wide instead of microseconds: every thread must see the one that
    /// started.
    /// </summary>
    [LivePythonFact]
    public void AllOfThemSeeTheInterpreterThatOneOfThemStarted()
    {
        var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
        var seen = new System.Collections.Concurrent.ConcurrentBag<bool>();

        using var start = new Barrier(16);
        Parallel.For(0, 16, _ =>
        {
            start.SignalAndWait();
            seen.Add(python.IsAvailable);
        });

        Assert.Equal(16, seen.Count);
        Assert.DoesNotContain(false, seen);
        Assert.True(python.IsAvailable, python.UnavailableReason);
    }

    // =====================================================================================
    // the installer
    // =====================================================================================

    [Fact]
    public void TheInstallerCannotInstallWithoutNetworkPermission()
    {
        var installer = new WheelInstaller(Policy(network: false));
        Assert.False(installer.CanInstall);
        Assert.Equal(ExecutionPolicy.NetworkDisabledMessage, installer.UnavailableReason);
    }

    [Fact]
    public void TheInstallerCannotInstallWhenTheIndexIsNotAnAllowedHost()
    {
        var installer = new WheelInstaller(Policy(network: true) with { NetworkHosts = ["example.com"] });
        Assert.False(installer.CanInstall);
        Assert.Contains("pypi.org", installer.UnavailableReason!);
    }

    [Fact]
    public void TheInstallerCanInstallWhenTheNetworkIsOn()
    {
        var installer = new WheelInstaller(Policy(network: true));
        Assert.True(installer.CanInstall);
        Assert.Null(installer.UnavailableReason);
    }

    [Fact]
    public async Task TheInstallerRefusesToInstallWithoutNetworkPermission()
    {
        var installer = new WheelInstaller(Policy(network: false));
        var request = new InstallRequest("python", ["rich"], _packages, Policy(network: false));

        ExecutionResult result = await installer.InstallAsync(request, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(ExecutionPolicy.NetworkDisabledMessage, result.Stderr);
        Assert.Empty(Directory.GetFileSystemEntries(_packages));
    }

    [Fact]
    public async Task TheInstallerRefusesALanguageItDoesNotInstall()
    {
        var installer = new WheelInstaller(Policy(network: true));
        var request = new InstallRequest("node", ["left-pad"], _packages, Policy(network: true));

        ExecutionResult result = await installer.InstallAsync(request, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("only handles Python packages", result.Stderr);
    }

    [Fact]
    public async Task TheInstallerRefusesAHostThePolicyDoesNotAllow()
    {
        ExecutionPolicy policy = Policy(network: true) with { NetworkHosts = ["example.com"] };
        var installer = new WheelInstaller(policy);
        var request = new InstallRequest("python", ["rich"], _packages, policy);

        ExecutionResult result = await installer.InstallAsync(request, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("pypi.org", result.Stderr);
    }

    [Fact]
    public void TheInstallerPicksThePurePythonWheelAndCarriesItsDigest()
    {
        Assert.True(WheelInstaller.TrySelectWheel(IndexJson(
            IndexFile("rich-13.9.4.tar.gz", "sdist", "aaa"),
            IndexFile("rich-13.9.4-py3-none-any.whl", "bdist_wheel", "bbb")), out PyPiFile? wheel, out string? reason));

        Assert.Null(reason);
        Assert.NotNull(wheel);
        Assert.Equal("rich-13.9.4-py3-none-any.whl", wheel.FileName);
        Assert.Equal("bbb", wheel.Sha256);
    }

    [Fact]
    public void TheInstallerRefusesAWheelThatWouldNeedACompilerAndSaysWhy()
    {
        Assert.False(WheelInstaller.TrySelectWheel(IndexJson(
            IndexFile("numpy-2.5.2.tar.gz", "sdist", "aaa"),
            IndexFile("numpy-2.5.2-cp313-cp313-macosx_11_0_arm64.whl", "bdist_wheel", "bbb"),
            IndexFile("numpy-2.5.2-cp313-cp313-manylinux_2_17_x86_64.whl", "bdist_wheel", "ccc")), out PyPiFile? wheel, out string? reason));

        Assert.Null(wheel);
        Assert.Contains("compiled code", reason!);
        Assert.Contains("py3-none-any", reason!);
        Assert.Contains("signed into the app at build time", reason!);
    }

    [Fact]
    public void TheInstallerRefusesASourceOnlyPackageAndSaysACompilerIsMissing()
    {
        Assert.False(WheelInstaller.TrySelectWheel(
            IndexJson(IndexFile("pyyaml-6.0.tar.gz", "sdist", "aaa")), out PyPiFile? wheel, out string? reason));

        Assert.Null(wheel);
        Assert.Contains("only a source distribution", reason!);
        Assert.Contains("compiler", reason!);
    }

    [Fact]
    public void TheInstallerSkipsAYankedWheel()
    {
        Assert.True(WheelInstaller.TrySelectWheel(IndexJson(
            IndexFile("rich-1.0-py3-none-any.whl", "bdist_wheel", "aaa", yanked: true),
            IndexFile("rich-1.1-py3-none-any.whl", "bdist_wheel", "bbb")), out PyPiFile? wheel, out _));
        Assert.Equal("rich-1.1-py3-none-any.whl", wheel!.FileName);
    }

    [Fact]
    public void TheInstallerRefusesAWheelPublishedWithoutADigest()
    {
        Assert.False(WheelInstaller.TrySelectWheel(
            IndexJson(IndexFile("rich-1.0-py3-none-any.whl", "bdist_wheel", "")), out _, out string? reason));
        Assert.Contains("without a sha256", reason!);
    }

    [Theory]
    [InlineData("rich-13.9.4-py3-none-any.whl", true)]
    [InlineData("six-1.16.0-py2.py3-none-any.whl", true)]
    [InlineData("numpy-2.5.2-cp313-cp313-macosx_11_0_arm64.whl", false)]
    [InlineData("pillow-10.4.0-cp313-cp313-ios_13_0_arm64_iphoneos.whl", false)]
    [InlineData("pyyaml-6.0.tar.gz", false)]
    public void PureWheelsAreTheOnesWithNoAbiAndNoPlatform(string fileName, bool pure)
        => Assert.Equal(pure, WheelInstaller.IsPureWheel(fileName));

    [Theory]
    [InlineData("rich", "rich", null)]
    [InlineData("rich==13.9.4", "rich", "13.9.4")]
    [InlineData("et_xmlfile", "et_xmlfile", null)]
    public void PackageSpecsSplitIntoANameAndAnOptionalVersion(string spec, string name, string? version)
    {
        Assert.True(WheelInstaller.TrySplit(spec, out string parsed, out string? parsedVersion, out _));
        Assert.Equal(name, parsed);
        Assert.Equal(version, parsedVersion);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("rich; rm -rf /")]
    [InlineData("-rrequirements.txt")]
    [InlineData("rich==")]
    public void APackageSpecThatIsNotANameIsRefused(string spec)
    {
        Assert.False(WheelInstaller.TrySplit(spec, out _, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void AWheelMemberThatClimbsOutOfTheTargetIsRefused()
    {
        using var archive = Archive(("rich/__init__.py", "ok"), ("../escaped.py", "not ok"));
        Assert.False(WheelInstaller.TryExtract(archive, _packages, Confined(_packages), out int files, out string? refusal));

        Assert.Equal(0, files);
        Assert.Contains("../escaped.py", refusal!);
        Assert.Contains("points outside", refusal!);
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "escaped.py")));
    }

    [Fact]
    public void AWheelMemberWithAnAbsolutePathIsRefused()
    {
        using var archive = Archive(("/etc/cron.d/evil", "not ok"));
        Assert.False(WheelInstaller.TryExtract(archive, _packages, Confined(_packages), out _, out string? refusal));
        Assert.Contains("points outside", refusal!);
    }

    [Fact]
    public void AWheelMemberThatWouldLandThroughASymlinkIsRefused()
    {
        // The archive name is innocent; a symlink already in the target is what
        // moves it. Only re-resolving each member catches this.
        Directory.CreateSymbolicLink(Path.Combine(_packages, "rich"), _outside);
        using var archive = Archive(("rich/__init__.py", "not ok"));

        Assert.False(WheelInstaller.TryExtract(archive, _packages, Confined(_packages), out _, out string? refusal));
        Assert.Contains("outside", refusal!);
        Assert.False(System.IO.File.Exists(Path.Combine(_outside, "__init__.py")));
    }

    [Fact]
    public void APureWheelUnpacksIntoTheTargetDirectory()
    {
        using var archive = Archive(
            ("rich/__init__.py", "VERSION = '13.9.4'"),
            ("rich/console.py", "class Console: pass"),
            ("rich-13.9.4.dist-info/METADATA", "Name: rich"));

        Assert.True(WheelInstaller.TryExtract(archive, _packages, Confined(_packages), out int files, out string? refusal));
        Assert.Null(refusal);
        Assert.Equal(3, files);
        Assert.Equal("VERSION = '13.9.4'", System.IO.File.ReadAllText(Path.Combine(_packages, "rich", "__init__.py")));
        // pip list reads the dist-info directories, so the layout has to be this one.
        Assert.True(Directory.Exists(Path.Combine(_packages, "rich-13.9.4.dist-info")));
    }

    // =====================================================================================
    // with a real interpreter
    // =====================================================================================

    [LivePythonFact]
    public async Task ALiveInterpreterPrintsAndStreamsEachLine()
    {
        EmbeddedPython python = Live();
        var lines = new List<string>();
        InterpreterContext context = Context() with { OnStdoutLine = lines.Add };

        ExecutionResult result = await python.RunCodeAsync("print('hello'); print('world')", [], context, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\nworld\n", result.Stdout);
        Assert.Equal(["hello", "world"], lines);
    }

    [LivePythonFact]
    public void ALiveInterpreterReportsItsVersion()
    {
        EmbeddedPython python = Live();
        Assert.True(python.IsAvailable, python.UnavailableReason);
        Assert.StartsWith("3.", python.Version);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterSetsArgvAndMainTheWayTheRealOneDoes()
    {
        EmbeddedPython python = Live();
        ExecutionResult result = await python.RunCodeAsync(
            "import sys; print(sys.argv, __name__)", ["a", "b"], Context(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("['-c', 'a', 'b'] __main__\n", result.Stdout);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterRunsAScriptWithItsOwnDirectoryOnThePath()
    {
        EmbeddedPython python = Live();
        System.IO.File.WriteAllText(Path.Combine(_work, "helper.py"), "VALUE = 41\n");
        string script = Path.Combine(_work, "main.py");
        System.IO.File.WriteAllText(script, "import helper, sys\nprint(helper.VALUE + 1, __name__, sys.argv[0])\n");

        ExecutionResult result = await python.RunScriptAsync(script, ["main.py"], Context(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42 __main__ main.py\n", result.Stdout);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterHonoursSystemExit()
    {
        EmbeddedPython python = Live();
        Assert.Equal(3, (await python.RunCodeAsync("raise SystemExit(3)", [], Context(), CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await python.RunCodeAsync("raise SystemExit(0)", [], Context(), CancellationToken.None)).ExitCode);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterPrintsARealTracebackAndFails()
    {
        EmbeddedPython python = Live();
        ExecutionResult result = await python.RunCodeAsync(
            "def f():\n    raise ValueError('boom')\nf()", [], Context(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Traceback (most recent call last):", result.Stderr);
        Assert.Contains("ValueError: boom", result.Stderr);
        // The bootstrap's own frame must not be in it.
        Assert.DoesNotContain("_run", result.Stderr);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterRefusesToWriteOutsideTheSandbox()
    {
        EmbeddedPython python = Live();
        string escape = Path.Combine(_outside, "bad.txt").Replace("\\", "\\\\");
        ExecutionResult result = await python.RunCodeAsync(
            $"open('{escape}', 'w').write('x')", [], Context(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("PermissionError", result.Stderr);
        Assert.Contains("Permission denied", result.Stderr);
        Assert.False(System.IO.File.Exists(Path.Combine(_outside, "bad.txt")));
    }

    [LivePythonFact]
    public async Task ALiveInterpreterRefusesTheNetworkWithTheSharedWording()
    {
        EmbeddedPython python = Live();
        ExecutionResult result = await python.RunCodeAsync(
            "import socket; socket.socket()", [], Context(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(ExecutionPolicy.NetworkDisabledMessage, result.Stderr);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterRefusesToStartAProcess()
    {
        EmbeddedPython python = Live();
        ExecutionResult result = await python.RunCodeAsync(
            "import os; os.system('echo hi')", [], Context(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(ExecutionPolicy.ProcessesUnavailableMessage, result.Stderr);
    }

    [LivePythonFact]
    public async Task ALiveInterpreterStopsARunThatOutlivesItsDeadline()
    {
        EmbeddedPython python = Live();
        InterpreterContext context = Context() with { Timeout = TimeSpan.FromSeconds(2) };

        ExecutionResult result = await python.RunCodeAsync("while True:\n    pass\n", [], context, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(ExecutionResult.TimeoutExitCode, result.ExitCode);
    }

    [LivePythonFact]
    public async Task ALiveSyntaxCheckReportsPathAndLine()
    {
        EmbeddedPython python = Live();
        string script = Path.Combine(_work, "bad.py");
        System.IO.File.WriteAllText(script, "x = 1\ndef (:\n");

        SyntaxCheckResult result = await python.CheckSyntaxAsync(script, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith($"{script}:2:", result.Message);
    }

    [LivePythonFact]
    public async Task ALiveSyntaxCheckPassesGoodSource()
    {
        EmbeddedPython python = Live();
        string script = Path.Combine(_work, "good.py");
        System.IO.File.WriteAllText(script, "def f(x):\n    return x + 1\n");

        Assert.True((await python.CheckSyntaxAsync(script, CancellationToken.None)).Ok);
    }

    // =====================================================================================
    // helpers
    // =====================================================================================

    private static EmbeddedPython Live()
        => new(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));

    private ConfinedPaths Confined(string target) => new(Policy() with { WorkRoot = target });

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is not in the generated source");
        from += start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to >= 0, $"'{end}' does not close '{start}'");
        return text[from..to];
    }

    private static ZipArchive Archive(params (string Name, string Content)[] members)
    {
        var buffer = new MemoryStream();
        using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in members)
            {
                ZipArchiveEntry entry = writing.CreateEntry(name);
                using Stream stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static string IndexFile(string name, string type, string sha256, bool yanked = false)
        => $$"""
            {
              "filename": "{{name}}",
              "url": "https://files.pythonhosted.org/packages/{{name}}",
              "packagetype": "{{type}}",
              "digests": { "sha256": "{{sha256}}" },
              "yanked": {{(yanked ? "true" : "false")}}
            }
            """;

    private static string IndexJson(params string[] files)
        => $$"""{ "info": { "name": "x" }, "urls": [ {{string.Join(",", files)}} ] }""";
}
