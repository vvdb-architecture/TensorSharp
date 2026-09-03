// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.Json;
using TensorAgent.Core.Python;
using TensorAgent.Core.Shell;
using TensorSharp.AgentHost.Skills;
using TensorAgent.Core.Sandbox;
using TensorSharp.AgentHost.CodeExec;
using System.Text.RegularExpressions;

namespace TensorAgent.Tests;

/// <summary>
/// Guards the one bundled skill that was written for this app rather than
/// adopted from upstream: <c>skills/documents</c>.
///
/// <para>
/// The published docx/pptx/xlsx/pdf skills cannot ship here — they need lxml,
/// which has no iOS wheel, and they shell out to LibreOffice, which iOS cannot
/// launch. `documents` replaces them with scripts that use only what
/// prepare-python.sh actually stages. That is the invariant these tests hold,
/// and it is one a future edit breaks by accident in two directions: a script
/// that starts importing something new, and a staging list that stops shipping
/// something a script already imports. Both are checked, against the real files,
/// so neither passes silently.
/// </para>
/// <para>
/// What is not checked here is whether the scripts produce documents Word or
/// PowerPoint will open. Nothing on a build machine or a phone can answer that;
/// see the "Limits" section of the skill's own SKILL.md.
/// </para>
/// </summary>
public sealed class DocumentSkillTests
{
    private static readonly string Repo = FindRepoRoot();
    private static readonly string Skill = Path.Combine(Repo, "TensorAgent", "skills", "documents");
    private static readonly string Scripts = Path.Combine(Skill, "scripts");

    /// <summary>
    /// Modules that exist but cannot work on this platform, mirroring
    /// <c>UNAVAILABLE</c> in scripts/verify-skills.py. Kept as a copy rather
    /// than a reference because a C# test cannot execute the Python; the copy is
    /// held in step by <see cref="TheForbiddenListMatchesTheSkillVerifier"/>.
    /// </summary>
    private static readonly string[] Forbidden =
    {
        "subprocess", "multiprocessing", "webbrowser", "playwright", "selenium",
        "pdf2image", "mcp", "anthropic", "tkinter", "ctypes",
    };

    /// <summary>
    /// Distribution name in prepare-python.sh to the module name a script
    /// imports. Only the ones that differ need an entry, but all are listed so a
    /// reader can see the whole staged surface in one place.
    /// </summary>
    private static readonly Dictionary<string, string> ModuleOfPackage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["numpy"] = "numpy",
        ["pillow"] = "PIL",
        ["pypdf"] = "pypdf",
        ["openpyxl"] = "openpyxl",
        ["et_xmlfile"] = "et_xmlfile",
        ["reportlab"] = "reportlab",
        ["imageio"] = "imageio",
        ["charset-normalizer"] = "charset_normalizer",
        ["defusedxml"] = "defusedxml",
        ["pyyaml"] = "yaml",
        // Not imported by any skill script: it is the CA bundle the interpreter points
        // OpenSSL at, so https:// works on a device that has no system trust store.
        ["certifi"] = "certifi",
    };

    /// <summary>
    /// The standard-library modules these scripts are allowed to reach for.
    /// An explicit list, not a heuristic: the failure this catches is a script
    /// importing a third-party package nobody staged, and any rule that guesses
    /// "unknown name means standard library" lets exactly that through.
    /// </summary>
    private static readonly string[] StandardLibrary =
    {
        "__future__", "argparse", "csv", "dataclasses", "datetime", "html", "io", "json",
        "math", "os", "pathlib", "posixpath", "re", "shutil", "statistics", "string",
        "sys", "textwrap", "typing", "unicodedata", "xml", "zipfile",
    };

    [Fact]
    public void TheSkillIsPresentAndDeclaresItself()
    {
        Assert.True(Directory.Exists(Skill), $"{Skill} is missing");
        string skillFile = Path.Combine(Skill, "SKILL.md");
        Assert.True(File.Exists(skillFile), $"{skillFile} is missing");

        string text = File.ReadAllText(skillFile);
        Assert.StartsWith("---\n", text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        string frontMatter = Front(text);
        Assert.Matches(@"(?m)^name:\s*documents\s*$", frontMatter);

        Match description = Regex.Match(frontMatter, @"(?m)^description:\s*(?<value>.+)$");
        Assert.True(description.Success, "SKILL.md has no description; the registry shows one per skill");
        Assert.True(description.Groups["value"].Value.Trim().Length > 80,
            "the description is what a model matches a request against; one line of it is not enough");
    }

    [Fact]
    public void EveryScriptRunsOnAPlatformWithNoProcesses()
    {
        var offenders = new List<string>();
        foreach (string path in PythonFiles())
        {
            foreach (string module in ImportsOf(path))
            {
                if (Forbidden.Contains(module, StringComparer.Ordinal))
                    offenders.Add($"{Path.GetFileName(path)} imports {module}");
            }
        }

        Assert.True(offenders.Count == 0,
            "iOS starts no processes and has no browser; these imports cannot work there: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryImportIsEitherStagedOrShippedWithTheSkill()
    {
        HashSet<string> staged = StagedModules();
        var siblings = PythonFiles().Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)!;
        var unresolved = new List<string>();

        foreach (string path in PythonFiles())
        {
            foreach (string module in ImportsOf(path))
            {
                if (StandardLibrary.Contains(module, StringComparer.Ordinal)) continue;
                if (siblings.Contains(module)) continue;
                if (staged.Contains(module)) continue;
                unresolved.Add($"{Path.GetFileName(path)} imports {module}");
            }
        }

        Assert.True(unresolved.Count == 0,
            "nothing installs packages on the phone, so every import has to be staged by "
            + "TensorAgent/scripts/prepare-python.sh, be a sibling of the script, or be in the "
            + "standard library. These are none of those: " + string.Join(", ", unresolved));
    }

    [Fact]
    public void TheStagingListStillShipsWhatTheScriptsImport()
    {
        // The other direction of the same invariant: dropping defusedxml or
        // reportlab from prepare-python.sh must fail here, not on a user's phone.
        HashSet<string> staged = StagedModules();
        foreach (string module in new[] { "reportlab", "pypdf", "openpyxl", "defusedxml", "PIL" })
        {
            Assert.True(staged.Contains(module),
                $"prepare-python.sh no longer stages {module}, which skills/documents imports");
        }
    }

    [Fact]
    public void TheForbiddenListMatchesTheSkillVerifier()
    {
        string verifier = File.ReadAllText(Path.Combine(Repo, "TensorAgent", "scripts", "verify-skills.py"));
        Match block = Regex.Match(verifier, @"UNAVAILABLE\s*=\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline);
        Assert.True(block.Success, "verify-skills.py no longer has an UNAVAILABLE map to compare against");

        var declared = Regex.Matches(block.Groups["body"].Value, "\"(?<name>[a-z0-9_]+)\"\\s*:")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.OrderBy(n => n, StringComparer.Ordinal),
            Forbidden.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryCommandLineInSkillMdNamesAScriptThatExists()
    {
        string text = File.ReadAllText(Path.Combine(Skill, "SKILL.md"));
        var documented = new HashSet<string>(StringComparer.Ordinal);

        var lines = Regex.Matches(text, @"(?m)^python3 scripts/(?<script>[A-Za-z0-9_]+\.py)(?<args>.*)$");
        Assert.True(lines.Count >= 6, $"SKILL.md documents only {lines.Count} command line(s); a model runs these");

        foreach (Match line in lines)
        {
            string script = line.Groups["script"].Value;
            string path = Path.Combine(Scripts, script);
            Assert.True(File.Exists(path), $"SKILL.md runs scripts/{script}, which does not exist");
            documented.Add(script);

            // A documented option the script does not accept fails at the point a
            // model copies the line, which is the worst possible moment.
            string source = File.ReadAllText(path);
            foreach (Match option in Regex.Matches(line.Groups["args"].Value, @"(?<flag>--[a-z][a-z-]*)"))
            {
                string flag = option.Groups["flag"].Value;
                Assert.True(source.Contains($"\"{flag}\"", StringComparison.Ordinal),
                    $"SKILL.md passes {flag} to {script}, which does not define it");
            }
        }

        foreach (string path in PythonFiles())
        {
            // A module with no entry point is a library the other scripts import;
            // anything runnable has to be documented or a model will never run it.
            if (!File.ReadAllText(path).Contains("__main__", StringComparison.Ordinal)) continue;
            Assert.Contains(Path.GetFileName(path), documented);
        }
    }

    [Fact]
    public void TheVerifierRecordedThatTheSkillPasses()
    {
        string path = Path.Combine(Repo, "TensorAgent", "skills", "verdicts.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement entry = document.RootElement.EnumerateArray()
            .SingleOrDefault(e => e.GetProperty("name").GetString() == "documents");

        Assert.True(entry.ValueKind == JsonValueKind.Object,
            "verdicts.json has no entry for 'documents'; that file is what records why a skill is bundled");
        Assert.True(entry.GetProperty("ok").GetBoolean(),
            "verdicts.json says 'documents' does not pass, so the app would not bundle it");
        Assert.Equal(PythonFiles().Count, entry.GetProperty("scripts").GetInt32());
    }

    // =====================================================================================

    private static List<string> PythonFiles() =>
        Directory.GetFiles(Scripts, "*.py", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Top-level module names a Python file imports, absolute imports only —
    /// the same rule scripts/verify-skills.py applies, by regex rather than by
    /// AST because a relative import resolves inside the skill and needs nothing.
    /// </summary>
    private static IEnumerable<string> ImportsOf(string path)
    {
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            Match import = Regex.Match(line, @"^import\s+(?<names>[A-Za-z_][\w., ]*)$");
            if (import.Success)
            {
                foreach (string name in import.Groups["names"].Value.Split(','))
                {
                    string trimmed = name.Trim().Split(' ')[0];
                    if (trimmed.Length > 0) yield return trimmed.Split('.')[0];
                }
                continue;
            }

            Match from = Regex.Match(line, @"^from\s+(?<module>[A-Za-z_][\w.]*)\s+import\s");
            if (from.Success)
                yield return from.Groups["module"].Value.Split('.')[0];
        }
    }

    /// <summary>Module names prepare-python.sh stages into the app bundle.</summary>
    private static HashSet<string> StagedModules()
    {
        string script = File.ReadAllText(Path.Combine(Repo, "TensorAgent", "scripts", "prepare-python.sh"));
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (string list in new[] { "BINARY_PACKAGES", "PURE_PACKAGES" })
        {
            Match block = Regex.Match(script, list + @"=\((?<body>.*?)\n\)", RegexOptions.Singleline);
            Assert.True(block.Success, $"prepare-python.sh no longer declares {list}");
            foreach (Match spec in Regex.Matches(block.Groups["body"].Value, "\"(?<spec>[A-Za-z0-9_.-]+)(==[^\"]*)?\""))
            {
                string name = spec.Groups["spec"].Value;
                Assert.True(ModuleOfPackage.TryGetValue(name, out string? module),
                    $"prepare-python.sh stages {name}, which this test does not know the module name of; "
                    + "add it to ModuleOfPackage");
                modules.Add(module!);
            }
        }
        return modules;
    }

    private static string Front(string text)
    {
        string normalised = text.Replace("\r\n", "\n");
        int end = normalised.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(end > 0, "SKILL.md has no closing front-matter fence");
        return normalised[4..end];
    }

    /// <summary>
    /// The repository root, found by walking up from the test assembly. The
    /// skill is data in the tree, not a compiled resource, so it has to be
    /// located rather than referenced.
    /// </summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "TensorAgent", "skills")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            $"no TensorAgent/skills above {AppContext.BaseDirectory}; run the tests from inside the repository");
    }
    /// <summary>
    /// The staged runtime can actually import what the scripts import.
    ///
    /// <para>
    /// Everything else in this file reads the staging LIST and the scripts' import
    /// statements and checks they agree. That is a check on two documents, and both
    /// can agree perfectly while the interpreter the app really starts imports none
    /// of it -- which is exactly what happened: every static check here passed while
    /// a live model was told "The module 'reportlab' is not installed in this
    /// session's environment" and gave up on the report it had been asked for. The
    /// only thing that settles it is an import, through the app's own interpreter,
    /// under the sandbox the app really applies. It reports sys.path when it fails,
    /// because "not installed" and "installed where nothing looks" are different
    /// problems with different fixes.
    /// </para>
    /// </summary>
    [LivePythonFact]
    public async Task TheStagedRuntimeCanImportEveryModuleTheScriptsNeed()
    {
        var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
        Assert.True(python.IsAvailable, python.UnavailableReason);

        string work = Path.Combine(Path.GetTempPath(), "tensoragent-imports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var policy = new ExecutionPolicy(
                AllowScripts: true,
                AllowNetwork: false,
                WorkRoot: work,
                ReadableRoots: Array.Empty<string>(),
                TempRoot: work);
            var context = new InterpreterContext(work, new Dictionary<string, string> { ["HOME"] = work }, policy);

            // Importing the TOP-LEVEL package proves almost nothing, and the first
            // version of this test made exactly that mistake: `__import__("reportlab")`
            // passed while `from reportlab.lib import colors` -- what make_pdf.py
            // actually writes on its sixth line -- still failed, so the test went green
            // and the live model was still told reportlab was not installed. What the
            // scripts import is what has to be imported here, so the module list is
            // read out of the scripts themselves and every one of them is tried.
            string scripts = Path.Combine(Skill, "scripts");
            string[] modules = Directory.EnumerateFiles(scripts, "*.py")
                .SelectMany(file => Regex.Matches(
                    File.ReadAllText(file),
                    @"^\s*(?:from\s+(?<m>[A-Za-z_][\w.]*)\s+import|import\s+(?<m>[A-Za-z_][\w.]*))",
                    RegexOptions.Multiline).Select(match => match.Groups["m"].Value))
                .Where(name => ThirdParty.Contains(name.Split('.')[0]))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.True(modules.Length >= 5,
                $"only {modules.Length} third-party imports were found across {scripts}; the scan is broken");

            string probe = "import sys\nmissing = []\n"
                + string.Concat(modules.Select(name =>
                    $"try:\n    __import__({Quote(name)})\nexcept Exception as exc:\n"
                    + $"    missing.append({Quote(name)} + ': ' + type(exc).__name__ + ': ' + str(exc))\n"))
                + "if missing:\n    print('MISSING ' + ' | '.join(missing))\n"
                + "    print('sys.path = ' + repr(sys.path))\n    raise SystemExit(1)\n"
                + $"print('all {modules.Length} present')\n";

            ExecutionResult result = await python.RunCodeAsync(probe, [], context, CancellationToken.None);
            Assert.True(result.ExitCode == 0,
                "the staged runtime cannot import what the documents skill's scripts import, so every script "
                + $"that uses one fails at its first line.{Environment.NewLine}{result.Stdout}{result.Stderr}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>The packages that come from the staged runtime rather than the standard library.</summary>
    private static readonly HashSet<string> ThirdParty =
        new(StringComparer.Ordinal) { "reportlab", "pypdf", "openpyxl", "defusedxml", "PIL" };

    private static string Quote(string value) => "'" + value.Replace("'", "\\'") + "'";

    /// <summary>
    /// The skill's own scripts, run the way the app runs them, actually produce
    /// documents.
    ///
    /// <para>
    /// The gap this fills is the one that cost the most time here. Whether these
    /// scripts work end to end was checked only by a live-model scenario: a model had
    /// to choose the right script, invent a spec and invoke it, and when the answer
    /// was "no PDF" the cause could be the model, the prompt, the skill's wording, the
    /// interpreter, the sandbox or the script -- two minutes per attempt to learn
    /// almost nothing. This runs the same scripts through the same shell and the same
    /// interpreter with a spec written HERE, so a failure is the machinery's and
    /// nothing else's, and it takes a second.
    /// </para>
    /// </summary>
    [LivePythonFact]
    public void TheSkillsOwnScriptsProduceRealDocumentsThroughTheAppsShell()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);
            IShellBackend backend = new InProcessShellBackend(python);

            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("docs");
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            string work = workspace.WorkDirectory;

            File.WriteAllText(Path.Combine(work, "sales.csv"),
                "region,units,revenue\nNorthgate,12,4180\nRavensworth,7,2650\nBellhaven,19,7310\n");
            File.WriteAllText(Path.Combine(work, "spec.json"), """
                {"title":"Q3","blocks":[
                  {"type":"heading","level":1,"text":"Summary"},
                  {"type":"table","columns":["Region","Units","Revenue"],
                   "rows":[["Northgate",12,4180],["Ravensworth",7,2650],["Bellhaven",19,7310]]}]}
                """);

            string scripts = Path.Combine(Skill, "scripts");
            foreach ((string command, string output, byte[] magic) in new[]
            {
                ($"python3 '{scripts}/make_pdf.py' --spec spec.json --out report.pdf", "report.pdf", PdfMagic),
                ($"python3 '{scripts}/make_xlsx.py' --csv sales.csv --out book.xlsx", "book.xlsx", ZipMagic),
                ($"python3 '{scripts}/make_docx.py' --spec spec.json --out report.docx", "report.docx", ZipMagic),
                ($"python3 '{scripts}/analyze_table.py' sales.csv", string.Empty, Array.Empty<byte>()),
            })
            {
                ConfinedResult result = backend.Run(new ShellLaunch
                {
                    Command = command,
                    Session = session,
                    WorkingDirectory = work,
                    WriteDirectory = work,
                    ReadOnlyDirectory = scripts,
                    Timeout = TimeSpan.FromSeconds(120),
                });

                Assert.True(result.Ok && result.ExitCode == 0,
                    $"`{command}` failed with exit {result.ExitCode}.{Environment.NewLine}"
                    + $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");

                if (output.Length == 0)
                    continue;

                string produced = Path.Combine(work, output);
                Assert.True(File.Exists(produced), $"`{command}` reported success and wrote no {output}");

                byte[] head = new byte[magic.Length];
                using (FileStream stream = File.OpenRead(produced))
                    Assert.Equal(magic.Length, stream.Read(head, 0, magic.Length));
                Assert.True(head.SequenceEqual(magic),
                    $"{output} does not start with the bytes its format requires, so it is not one");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The same scripts, run the way the MODEL runs them -- through skills_run.
    ///
    /// <para>
    /// Not a duplicate of the shell test above. skills_run does not execute the script
    /// where it lives: it copies it into the session workspace and runs it there, which
    /// is a different working directory, a different sandbox and a different import
    /// path. A live model reaches for skills_run first because that is the tool the
    /// skill's own instructions describe, so this is the path that actually decides
    /// whether the skill works for a user, and the one whose failure was being read as
    /// "the model could not do it".
    /// </para>
    /// </summary>
    [LivePythonFact]
    public void TheSkillsOwnScriptsProduceRealDocumentsThroughSkillsRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);

            var registry = new SkillRegistry(new SkillRegistryOptions
            {
                Roots = new[] { Path.GetDirectoryName(Skill)! },
            });
            Skill? skill = registry.Skills.FirstOrDefault(s => s.Id == "documents");
            Assert.True(skill is not null, "the documents skill did not load out of the repository");

            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("run");
            string work = workspace.WorkDirectory;
            File.WriteAllText(Path.Combine(work, "sales.csv"),
                "region,units,revenue\nNorthgate,12,4180\nRavensworth,7,2650\nBellhaven,19,7310\n");
            File.WriteAllText(Path.Combine(work, "spec.json"), """
                {"title":"Q3","blocks":[{"type":"heading","level":1,"text":"Summary"},
                  {"type":"table","columns":["Region","Units","Revenue"],
                   "rows":[["Northgate",12,4180],["Ravensworth",7,2650],["Bellhaven",19,7310]]}]}
                """);

            var runner = new SkillScriptRunner(new SkillScriptRunnerOptions
            {
                Backend = new InProcessShellBackend(python),
                Workspace = workspace,
                Sandbox = SkillSandboxMode.Required,
                Timeout = TimeSpan.FromSeconds(120),
            });

            foreach ((string script, string[] args, string output, byte[] magic) in new[]
            {
                ("scripts/make_pdf.py", new[] { "--spec", "spec.json", "--out", "report.pdf" }, "report.pdf", PdfMagic),
                ("scripts/make_xlsx.py", new[] { "--csv", "sales.csv", "--out", "book.xlsx" }, "book.xlsx", ZipMagic),
            })
            {
                SkillToolResult result = runner.Run(skill!, script, args);
                Assert.True(result.Ok,
                    $"skills_run {script} failed -- this is the path a model uses.{Environment.NewLine}{result.Content}");

                string produced = Path.Combine(work, output);
                Assert.True(File.Exists(produced),
                    $"skills_run {script} reported success and wrote no {output}.{Environment.NewLine}{result.Content}");

                byte[] head = new byte[magic.Length];
                using (FileStream stream = File.OpenRead(produced))
                    stream.ReadExactly(head);
                Assert.True(head.SequenceEqual(magic), $"{output} does not begin with its format's bytes");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A skill script reaches the network when, and only when, the user's switch is on.
    ///
    /// <para>
    /// Reported as "the research skill does not work and cannot access network". The
    /// chain from AppSettings.AllowNetwork through SkillHostOptions to the launch reads
    /// correctly, but reading is not evidence: this runs the research skill's own
    /// fetcher through the real runner, once with the switch off and once on, and
    /// asserts the refusal in the first case and a real page in the second. If the
    /// switch works, the bug the user hit is that it was off and the refusal did not
    /// say so loudly enough -- which is a different fix from a broken pipe.
    /// </para>
    /// </summary>
    [LivePythonFact]
    public void AResearchScriptReachesTheNetworkOnlyWhenTheSwitchIsOn()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-net-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);

            string skillsRoot = Path.GetDirectoryName(Skill)!;
            var registry = new SkillRegistry(new SkillRegistryOptions { Roots = new[] { skillsRoot } });
            Skill? research = registry.Skills.FirstOrDefault(s => s.Id == "research");
            Assert.True(research is not null, "the research skill did not load out of the repository");

            foreach (bool allow in new[] { false, true })
            {
                var manager = new SessionWorkspaceManager(Path.Combine(root, "s" + allow));
                SessionWorkspace workspace = manager.GetOrCreate("net");
                var runner = new SkillScriptRunner(new SkillScriptRunnerOptions
                {
                    Backend = new InProcessShellBackend(python),
                    Workspace = workspace,
                    Sandbox = SkillSandboxMode.Required,
                    AllowNetwork = allow,
                    Timeout = TimeSpan.FromSeconds(90),
                });

                SkillToolResult result = runner.Run(
                    research!, "scripts/fetch_page.py", new[] { "https://example.com" });
                string body = result.Content ?? string.Empty;

                // The search is the half a user actually starts with ("find me X"),
                // and it fails differently from a fetch: it resolves a second host,
                // follows a redirect and parses HTML. Run it in the same sandbox.
                if (allow)
                {
                    SkillToolResult found = runner.Run(research!, "scripts/search.py", new[] { "financial news" });
                    Assert.True((found.Content ?? string.Empty).Contains("results for", StringComparison.OrdinalIgnoreCase),
                        "the search returned nothing usable through the sandbox: " + found.Content);
                }

                if (!allow)
                {
                    Assert.False(body.Contains("Example Domain", StringComparison.OrdinalIgnoreCase),
                        "the network is off and a page came back anyway");
                    Assert.True(
                        body.Contains("network", StringComparison.OrdinalIgnoreCase),
                        $"with the network off the refusal must SAY so, or the user cannot tell a "
                        + $"blocked fetch from a broken skill. It said: {body}");
                }
                else
                {
                    Assert.True(body.Contains("Example Domain", StringComparison.OrdinalIgnoreCase),
                        $"the switch is on and the skill still could not read a page: {body}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The interpreter can verify a TLS certificate.
    ///
    /// <para>
    /// _ssl ships in the app bundle, so TLS is AVAILABLE on a phone; what is not
    /// there is a trust store. OpenSSL looks for one at a compiled-in path that does
    /// not exist inside an app bundle, so every https:// fails
    /// CERTIFICATE_VERIFY_FAILED -- and only on the device, because a development
    /// machine's interpreter has the system store and quietly succeeds. That
    /// asymmetry is why the research skill was reported as never working while every
    /// test of it passed. This asserts the bundle is found and actually used.
    /// </para>
    /// </summary>
    [LivePythonFact]
    public async Task TheInterpreterHasACertificateStoreAndCanVerifyTls()
    {
        var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
        Assert.True(python.IsAvailable, python.UnavailableReason);

        string work = Path.Combine(Path.GetTempPath(), "tensoragent-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var policy = new ExecutionPolicy(
                AllowScripts: true, AllowNetwork: true, WorkRoot: work,
                ReadableRoots: Array.Empty<string>(), TempRoot: work);
            var context = new InterpreterContext(work, new Dictionary<string, string> { ["HOME"] = work }, policy);

            const string probe = """
                import os, ssl, sys
                where = os.environ.get('SSL_CERT_FILE')
                if not where or not os.path.isfile(where):
                    print('NO CA BUNDLE: SSL_CERT_FILE=' + repr(where))
                    raise SystemExit(1)
                ctx = ssl.create_default_context()
                if ctx.cert_store_stats().get('x509_ca', 0) < 1:
                    print('CA BUNDLE LOADED NOTHING: ' + where)
                    raise SystemExit(1)
                print('ok ' + where + ' with ' + str(ctx.cert_store_stats()['x509_ca']) + ' authorities')
                """;

            ExecutionResult result = await python.RunCodeAsync(probe, [], context, CancellationToken.None);
            Assert.True(result.ExitCode == 0,
                "the interpreter cannot verify a certificate, so every https:// from a skill or from "
                + $"generated code fails on the device.{Environment.NewLine}{result.Stdout}{result.Stderr}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>
    /// A deleted skill stays deleted — including one that ships inside the app.
    ///
    /// <para>
    /// Most of this app's skills live in the bundle, which is read-only and is
    /// rewritten by every install of the app, so "delete" cannot mean "delete the
    /// files". Before this it meant one of two worse things: the removal was refused
    /// outright ("remove it from that directory instead", which a phone user cannot
    /// do), or it appeared to work and the skill was back on the next launch. Both
    /// are worse than not offering the button.
    /// </para>
    /// </summary>
    [Fact]
    public void ARemovedSkillDoesNotComeBackWhenTheRegistryIsRebuilt()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-rm-" + Guid.NewGuid().ToString("N"));
        string bundled = Path.Combine(root, "bundled");
        string installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(installed);

        // A skill that ships with the app: a read-only root, exactly like the bundle.
        string one = Path.Combine(bundled, "weather");
        Directory.CreateDirectory(one);
        File.WriteAllText(Path.Combine(one, "SKILL.md"),
            "---\nname: weather\ndescription: Tells you the weather.\n---\n\n# Weather\n");

        string record = Path.Combine(root, "removed-skills.txt");
        SkillRegistryOptions Options() => new()
        {
            Roots = new[] { bundled, installed },
            InstallDirectory = installed,
            RemovedRecordFile = record,
        };

        var registry = new SkillRegistry(Options());
        Assert.Contains(registry.Skills, s => s.Id == "weather");

        Assert.True(registry.Remove("weather"), "removing a bundled skill must succeed");
        Assert.DoesNotContain(registry.Skills, s => s.Id == "weather");

        // The bytes are still on disk, because a real bundle is read-only and the app
        // cannot delete them. What must not happen is the skill coming back.
        Assert.True(File.Exists(Path.Combine(one, "SKILL.md")),
            "the test's own premise is wrong if the file was deletable");

        // A fresh registry is what the next launch builds.
        var relaunched = new SkillRegistry(Options());
        Assert.DoesNotContain(relaunched.Skills, s => s.Id == "weather");

        try { Directory.Delete(root, true); } catch { }
    }

}
