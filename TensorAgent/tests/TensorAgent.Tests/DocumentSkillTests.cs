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
[Collection(LivePythonCollection.Name)]
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
    [LiveStagedPythonFact]
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

    /// <summary>
    /// A small but semantically checkable deck fixture for the reported workflow.
    /// It deliberately distinguishes established M5 material from M6 research so a
    /// writer that drops either side of the comparison cannot pass on ZIP magic alone.
    /// </summary>
    private const string AppleChipDeckSpec = """
        {
          "title": "Apple M6 vs M5",
          "author": "TensorAgent",
          "slides": [
            {"layout":"title","title":"Apple M6 vs M5","subtitle":"Evidence-backed comparison"},
            {"layout":"table","title":"M5 and M6 at a glance",
             "columns":["Chip","Status","Scope"],
             "rows":[["M5","Established baseline","Published capabilities"],
                     ["M6","Research subject","Confirmed facts and clearly labeled reports"]]},
            {"layout":"bullets","title":"Reporting rules",
             "bullets":["Cite every M5 fact","Keep M6 reports separate from confirmed information"]}
          ]
        }
        """;

    private static void AssertDeckContainsBothChips(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        string slideXml = string.Join("\n", archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .Select(entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }));

        Assert.Contains("M5", slideXml, StringComparison.Ordinal);
        Assert.Contains("M6", slideXml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live M5/M6 run produced a coherent but non-canonical shape: every slide
    /// used <c>content: []</c> without a layout and citations lived in root
    /// <c>sources</c>. Both fields used to be ignored/rejected, leaving empty slides
    /// and another expensive model repair round. Their meaning is unambiguous, so the
    /// writer normalizes them and must put every claim and URL into slide XML.
    /// </summary>
    [LiveStagedPythonFact]
    public void PptxContentAndSourcesAliasesRenderVisibleSlidesOnTheFirstRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-pptx-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);
            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("aliases");
            string work = workspace.WorkDirectory;
            IShellBackend backend = new InProcessShellBackend(python);
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            string script = Path.Combine(Scripts, "make_pptx.py");

            ConfinedResult Run(string name, string spec)
            {
                File.WriteAllText(Path.Combine(work, name + ".json"), spec);
                return backend.Run(new ShellLaunch
                {
                    Command = $"python3 '{script}' --spec {name}.json --out {name}.pptx",
                    Session = session,
                    WorkingDirectory = work,
                    WriteDirectory = work,
                    ReadOnlyDirectory = Scripts,
                    Timeout = TimeSpan.FromSeconds(30),
                });
            }

            File.WriteAllBytes(
                Path.Combine(work, "pixel.png"),
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            const string aliasSpec = """
                {
                  "title": "Apple M6 vs M5",
                  "slides": [
                    {"title":"Overview","content":["M5_BASELINE_VISIBLE",null,{"text":null},"  - M6_RESEARCH_VISIBLE",
                      {"text":"NESTED_HEADING_VISIBLE","bullets":["NESTED_DETAIL_VISIBLE",
                        {"text":"NESTED_CITATION_VISIBLE","date":"2026-09-04","url":"https://example.com/nested"}]}]},
                    {"title":"M5","content":["M5_ARCHITECTURE_VISIBLE"]},
                    {"title":"M6","content":["M6_REPORTS_VISIBLE"],
                      "sources":[{"title":"Slide-local source","date":"2026-09-03","url":"https://example.com/local"}]},
                    {"title":"Comparison","content":["M5_M6_COMPARISON_VISIBLE",
                      {"text":"INLINE_CITATION_VISIBLE","date":"2026-09-05","url":"https://example.com/inline"}]},
                    {"layout":"text","title":"Narrative","text":"TEXT_LAYOUT_VISIBLE"},
                    {"layout":"text","title":"Mixed sources","text":"MIXED_INTRO_VISIBLE",
                      "bullets":[{"text":"MIXED_SOURCE_VISIBLE","url":"https://example.com/mixed"}]},
                    {"layout":"image","title":"Evidence image","image":"pixel.png","caption":"IMAGE_LAYOUT_VISIBLE"}
                  ],
                  "sources": [
                    {"url":"https://example.com/apple-m5","date":"2025-03-05","title":"M5 source"},
                    {"url":"https://example.com/apple-m6","date":"2026-08-25","title":"M6 source"},
                    {"url":"https://example.com/comparison","date":"2026-09-01","title":"Comparison source"}
                  ]
                }
                """;

            ConfinedResult alias = Run("aliases", aliasSpec);
            Assert.True(alias.Ok && alias.ExitCode == 0,
                $"the common aliases should succeed on their first run.{Environment.NewLine}"
                + $"stdout: {alias.Stdout}{Environment.NewLine}stderr: {alias.Stderr}");

            string deck = Path.Combine(work, "aliases.pptx");
            Assert.True(File.Exists(deck), "the alias spec reported success but produced no deck");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(deck))
            {
                var entries = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(8, entries.Count); // seven requested slides plus the normalized Sources slide
                string xml = string.Join("\n", entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open());
                    return reader.ReadToEnd();
                }));

                foreach (string visible in new[]
                {
                    "M5_BASELINE_VISIBLE", "M6_RESEARCH_VISIBLE", "M5_ARCHITECTURE_VISIBLE",
                    "M6_REPORTS_VISIBLE", "M5_M6_COMPARISON_VISIBLE", "TEXT_LAYOUT_VISIBLE",
                    "MIXED_INTRO_VISIBLE", "MIXED_SOURCE_VISIBLE", "https://example.com/mixed",
                    "NESTED_HEADING_VISIBLE", "NESTED_DETAIL_VISIBLE", "NESTED_CITATION_VISIBLE",
                    "2026-09-04", "https://example.com/nested",
                    "INLINE_CITATION_VISIBLE", "2026-09-05", "https://example.com/inline",
                    "IMAGE_LAYOUT_VISIBLE", "Sources",
                    "M5 source", "2025-03-05", "https://example.com/apple-m5",
                    "M6 source", "2026-08-25", "https://example.com/apple-m6",
                    "Comparison source", "2026-09-01", "https://example.com/comparison",
                    "Slide-local source", "2026-09-03", "https://example.com/local",
                })
                {
                    Assert.Contains(visible, xml, StringComparison.Ordinal);
                }
                Assert.Contains("<a:t></a:t>", xml, StringComparison.Ordinal);
                Assert.DoesNotContain(">None<", xml, StringComparison.Ordinal);
                Assert.Contains(archive.Entries, entry => entry.FullName == "ppt/media/image1.png");
            }

            // The existing canonical fixture remains valid under the stricter schema.
            ConfinedResult canonical = Run("canonical", AppleChipDeckSpec);
            Assert.True(canonical.Ok && canonical.ExitCode == 0,
                $"canonical slide fields regressed.{Environment.NewLine}"
                + $"stdout: {canonical.Stdout}{Environment.NewLine}stderr: {canonical.Stderr}");
            AssertDeckContainsBothChips(Path.Combine(work, "canonical.pptx"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A slide-local <c>sources</c> array can be the complete body of a Sources
    /// slide. The writer must replace that placeholder with one real slide, not
    /// validate an empty remainder and not append a duplicate Sources slide.
    /// Synthesized source bullets must also take the ordinary normalization path,
    /// including its bounded-list check, before any package is written.
    /// </summary>
    [LiveStagedPythonFact]
    public void PptxSourcesOnlySlideIsReplacedOnceAndObeysBulletLimits()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-pptx-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);
            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("sources");
            string work = workspace.WorkDirectory;
            IShellBackend backend = new InProcessShellBackend(python);
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            string script = Path.Combine(Scripts, "make_pptx.py");

            ConfinedResult Run(string name, object spec)
            {
                File.WriteAllText(Path.Combine(work, name + ".json"), JsonSerializer.Serialize(spec));
                return backend.Run(new ShellLaunch
                {
                    Command = $"python3 '{script}' --spec {name}.json --out {name}.pptx",
                    Session = session,
                    WorkingDirectory = work,
                    WriteDirectory = work,
                    ReadOnlyDirectory = Scripts,
                    Timeout = TimeSpan.FromSeconds(30),
                });
            }

            const string repeatedUrl = "https://example.com/shared-source";
            var sourcesOnly = new
            {
                title = "Evidence deck",
                slides = new object[]
                {
                    new { layout = "title", title = "Evidence deck" },
                    new
                    {
                        layout = "bullets",
                        title = "References",
                        sources = new object[]
                        {
                            new { title = "Shared evidence", date = "2026-09-05", url = repeatedUrl },
                            new { title = "Second source", date = "2026-09-04", url = "https://example.com/second" },
                        },
                    },
                },
                // An identical root citation is common after merging research
                // notes; it should not consume a second line on the final slide.
                sources = new object[]
                {
                    new { title = "Shared evidence", date = "2026-09-05", url = repeatedUrl },
                },
            };

            ConfinedResult valid = Run("sources-only", sourcesOnly);
            Assert.True(valid.Ok && valid.ExitCode == 0,
                $"a sources-only placeholder should succeed on its first run.{Environment.NewLine}"
                + $"stdout: {valid.Stdout}{Environment.NewLine}stderr: {valid.Stderr}");

            using (var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(work, "sources-only.pptx")))
            {
                var entries = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(2, entries.Count); // title plus one replacement References slide
                string xml = string.Join("\n", entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open());
                    return reader.ReadToEnd();
                }));
                Assert.Single(Regex.Matches(xml, ">References<", RegexOptions.CultureInvariant).Cast<Match>());
                Assert.Single(Regex.Matches(xml, Regex.Escape(repeatedUrl), RegexOptions.CultureInvariant).Cast<Match>());
                Assert.Contains("Second source", xml, StringComparison.Ordinal);
            }

            // The exact Chinese workflow can redundantly author a localized
            // citation-bullets slide as well as the canonical root sources. Fold
            // those citations into one final localized slide instead of keeping
            // the authored copy and appending another English Sources slide.
            var localizedSources = new
            {
                slides = new object[]
                {
                    new { layout = "title", title = "芯片对比" },
                    new
                    {
                        layout = "bullets",
                        title = "来源",
                        bullets = new object[]
                        {
                            new { text = "Shared evidence", date = "2026-09-05", url = repeatedUrl },
                            new { text = "Second source", date = "2026-09-04", url = "https://example.com/second" },
                        },
                    },
                },
                sources = new object[]
                {
                    new { title = "Shared evidence", date = "2026-09-05", url = repeatedUrl },
                },
            };
            ConfinedResult localized = Run("localized-sources", localizedSources);
            Assert.True(localized.Ok && localized.ExitCode == 0,
                $"a localized authored Sources slide should normalize once.{Environment.NewLine}"
                + $"stdout: {localized.Stdout}{Environment.NewLine}stderr: {localized.Stderr}");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(work, "localized-sources.pptx")))
            {
                var entries = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(2, entries.Count);
                string xml = string.Join("\n", entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open());
                    return reader.ReadToEnd();
                }));
                Assert.Single(Regex.Matches(xml, ">来源<", RegexOptions.CultureInvariant).Cast<Match>());
                Assert.Single(Regex.Matches(xml, Regex.Escape(repeatedUrl), RegexOptions.CultureInvariant).Cast<Match>());
                Assert.Single(Regex.Matches(xml, "https://example.com/second", RegexOptions.CultureInvariant).Cast<Match>());
            }

            // MAX_BULLETS is 512 in make_pptx.py. The generated Sources slide
            // previously bypassed flatten_bullets/validate_slide entirely.
            var tooManySources = new
            {
                slides = new object[]
                {
                    new { layout = "title", title = "Bounded sources" },
                    new
                    {
                        layout = "bullets",
                        title = "Sources",
                        sources = Enumerable.Range(1, 513)
                            .Select(i => $"https://example.com/source-{i}")
                            .ToArray(),
                    },
                },
            };
            ConfinedResult bounded = Run("too-many-sources", tooManySources);
            string diagnostic = bounded.Stdout + Environment.NewLine + bounded.Stderr;
            Assert.False(bounded.Ok, "a synthesized slide with 513 source bullets unexpectedly succeeded");
            Assert.Contains("slide 2", diagnostic, StringComparison.Ordinal);
            Assert.Contains("contains more than 512 bullet items", diagnostic, StringComparison.Ordinal);
            Assert.Contains("Accepted fields for this layout:", diagnostic, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(work, "too-many-sources.pptx")),
                "source limits were checked only after writing a deceptive deck");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Ambiguous or misspelled fields must fail before a valid-looking ZIP is
    /// written. Each error names the exact slide and enough of the schema for the
    /// model to repair that one object rather than regenerate the entire deck.
    /// </summary>
    [LiveStagedPythonFact]
    public void PptxInvalidSlideFieldsNameTheBrokenSlideAndItsAcceptedSchema()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-pptx-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);
            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("invalid");
            string work = workspace.WorkDirectory;
            IShellBackend backend = new InProcessShellBackend(python);
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            string script = Path.Combine(Scripts, "make_pptx.py");

            var cases = new[]
            {
                ("unknown-content", """{"slides":[{"layout":"table","title":"Bad","content":["lost"]}]}""",
                    "unknown key 'content'"),
                ("nested-content", """{"slides":[{"layout":"bullets","bullets":[{"content":"lost"}]}]}""",
                    "bullet 1 has unknown key 'content'"),
                ("scalar-alias", """{"slides":[{"title":"Bad","content":"not an array"}]}""",
                    "alias 'content', but that alias must be an array"),
                ("title", """{"slides":[{"layout":"title","subtitle":"No heading"}]}""",
                    "needs a non-empty 'title'"),
                ("bullets", """{"slides":[{"layout":"bullets","title":"No body","bullets":[]}]}""",
                    "needs a non-empty 'bullets' array"),
                ("bullet-level", """{"slides":[{"layout":"bullets","bullets":[{"text":"Too deep","level":3}]}]}""",
                    "needs 'level' between 0 and 2"),
                ("bullet-size", """{"slides":[{"layout":"bullets","bullets":[{"text":"Huge","size":1e309}]}]}""",
                    "needs a finite positive 'size'"),
                ("table", """{"slides":[{"layout":"table","title":"No cells"}]}""",
                    "needs a non-empty 'columns' or 'rows' array"),
                ("image", """{"slides":[{"layout":"image","title":"No picture"}]}""",
                    "needs a non-empty string 'image' path"),
                ("text", """{"slides":[{"layout":"text","title":"No body","text":"   "}]}""",
                    "needs non-empty 'text' body content"),
            };

            foreach ((string name, string spec, string expected) in cases)
            {
                File.WriteAllText(Path.Combine(work, name + ".json"), spec);
                ConfinedResult result = backend.Run(new ShellLaunch
                {
                    Command = $"python3 '{script}' --spec {name}.json --out {name}.pptx",
                    Session = session,
                    WorkingDirectory = work,
                    WriteDirectory = work,
                    ReadOnlyDirectory = Scripts,
                    Timeout = TimeSpan.FromSeconds(30),
                });
                string diagnostic = result.Stdout + Environment.NewLine + result.Stderr;

                Assert.False(result.Ok, $"{name} unexpectedly succeeded:{Environment.NewLine}{diagnostic}");
                Assert.Contains("slide 1", diagnostic, StringComparison.Ordinal);
                Assert.Contains(expected, diagnostic, StringComparison.Ordinal);
                Assert.Contains("Accepted fields for this layout:", diagnostic, StringComparison.Ordinal);
                Assert.Contains("Accepted layouts: bullets, image, table, text, title.", diagnostic,
                    StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(work, name + ".pptx")),
                    $"{name} failed validation only after writing a deceptive deck");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A Windows CSV is not necessarily UTF-8. The real election-results file that
    /// exposed this contained two otherwise ordinary names with an <c>0xE9</c> byte;
    /// the analyzer read 64 KiB as UTF-8 and stopped before it could report a row.
    /// Exercise the same script through the app's shell, and construct the fixture as
    /// bytes so a source-file or test-runner encoding cannot accidentally turn it into
    /// valid UTF-8.
    /// </summary>
    [LivePythonFact]
    public void AnalyzeTableFallsBackToWindows1252WithoutLosingNamesOrStatistics()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-cp1252-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);

            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("legacy-csv");
            string work = workspace.WorkDirectory;
            byte[] legacyCsv =
            [
                .. "candidate,votes\r\nSam M"u8.ToArray(),
                0xE9,
                .. "ndez,10\r\nRuth P"u8.ToArray(),
                0xE9,
                .. "rez,20\r\n"u8.ToArray(),
            ];
            File.WriteAllBytes(Path.Combine(work, "legacy.csv"), legacyCsv);

            IShellBackend backend = new InProcessShellBackend(python);
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            ConfinedResult result = backend.Run(new ShellLaunch
            {
                Command = $"python3 '{Path.Combine(Scripts, "analyze_table.py")}' legacy.csv",
                Session = session,
                WorkingDirectory = work,
                WriteDirectory = work,
                ReadOnlyDirectory = Scripts,
                Timeout = TimeSpan.FromSeconds(30),
            });

            Assert.True(result.Ok && result.ExitCode == 0,
                $"analyze_table.py failed with exit {result.ExitCode}.{Environment.NewLine}"
                + $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");

            using JsonDocument report = JsonDocument.Parse(result.Stdout);
            JsonElement rootElement = report.RootElement;
            Assert.Equal(2, rootElement.GetProperty("rows_read").GetInt32());
            JsonElement profiles = rootElement.GetProperty("profile");
            string[] names = profiles[0].GetProperty("top_values").EnumerateArray()
                .Select(item => item[0].GetString()!)
                .ToArray();
            Assert.Contains("Sam Méndez", names);
            Assert.Contains("Ruth Pérez", names);

            JsonElement voteProfile = profiles[1];
            Assert.True(voteProfile.GetProperty("numeric").GetBoolean());
            JsonElement stats = voteProfile.GetProperty("stats");
            Assert.Equal(2, stats.GetProperty("count").GetInt32());
            Assert.Equal(30, stats.GetProperty("sum").GetDouble());
            Assert.Equal(15, stats.GetProperty("mean").GetDouble());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Excel-style "Unicode Text" exports are commonly UTF-16 tab-separated files.
    /// Without checking their BOM before the single-byte fallback, the CSV reader sees
    /// a NUL after every character and cannot recover the headings or rows.
    /// </summary>
    [LivePythonFact]
    public void AnalyzeTableHonorsAUtf16BomForTabSeparatedInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-utf16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var python = new EmbeddedPython(Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
            Assert.True(python.IsAvailable, python.UnavailableReason);

            var manager = new SessionWorkspaceManager(Path.Combine(root, "sessions"));
            SessionWorkspace workspace = manager.GetOrCreate("utf16-tsv");
            string work = workspace.WorkDirectory;
            byte[] utf16Tsv =
            [
                0xFF, 0xFE,
                .. System.Text.Encoding.Unicode.GetBytes("candidate\tvotes\r\nZoë\t7\r\nRenée\t9\r\n"),
            ];
            File.WriteAllBytes(Path.Combine(work, "unicode.tsv"), utf16Tsv);

            IShellBackend backend = new InProcessShellBackend(python);
            var session = new ShellSession(workspace, ShellProgram.InProcess());
            ConfinedResult result = backend.Run(new ShellLaunch
            {
                Command = $"python3 '{Path.Combine(Scripts, "analyze_table.py")}' unicode.tsv",
                Session = session,
                WorkingDirectory = work,
                WriteDirectory = work,
                ReadOnlyDirectory = Scripts,
                Timeout = TimeSpan.FromSeconds(30),
            });

            Assert.True(result.Ok && result.ExitCode == 0,
                $"analyze_table.py failed with exit {result.ExitCode}.{Environment.NewLine}"
                + $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");

            using JsonDocument report = JsonDocument.Parse(result.Stdout);
            JsonElement rootElement = report.RootElement;
            Assert.Equal(new[] { "candidate", "votes" }, rootElement.GetProperty("columns").EnumerateArray()
                .Select(column => column.GetString())
                .ToArray());
            Assert.Equal(2, rootElement.GetProperty("rows_read").GetInt32());
            JsonElement profiles = rootElement.GetProperty("profile");
            string[] names = profiles[0].GetProperty("top_values").EnumerateArray()
                .Select(item => item[0].GetString()!)
                .ToArray();
            Assert.Contains("Zoë", names);
            Assert.Contains("Renée", names);
            Assert.Equal(16, profiles[1].GetProperty("stats").GetProperty("sum").GetDouble());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

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
    [LiveStagedPythonFact]
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
            File.WriteAllText(Path.Combine(work, "deck-spec.json"), AppleChipDeckSpec);

            string scripts = Path.Combine(Skill, "scripts");
            foreach ((string command, string output, byte[] magic) in new[]
            {
                ($"python3 '{scripts}/make_pdf.py' --spec spec.json --out report.pdf", "report.pdf", PdfMagic),
                ($"python3 '{scripts}/make_xlsx.py' --csv sales.csv --out book.xlsx", "book.xlsx", ZipMagic),
                ($"python3 '{scripts}/make_docx.py' --spec spec.json --out report.docx", "report.docx", ZipMagic),
                ($"python3 '{scripts}/make_pptx.py' --spec deck-spec.json --out comparison.pptx", "comparison.pptx", ZipMagic),
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
                if (output.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                    AssertDeckContainsBothChips(produced);
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
    [LiveStagedPythonFact]
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
            File.WriteAllText(Path.Combine(work, "deck-spec.json"), AppleChipDeckSpec);

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
                ("scripts/make_pptx.py", new[] { "--spec", "deck-spec.json", "--out", "comparison.pptx" }, "comparison.pptx", ZipMagic),
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
                if (output.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                    AssertDeckContainsBothChips(produced);
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

                // Discovery is the half a user actually starts with ("find me X"),
                // and it fails differently from a fetch: it resolves several hosts,
                // follows redirects, decompresses and parses both HTML and JSON. Run
                // it in the same sandbox, and require a URL rather than merely a
                // heading -- an index that answered with a challenge page produces a
                // heading and no sources at all.
                if (allow)
                {
                    SkillToolResult found = runner.Run(
                        research!, "scripts/discover.py", new[] { "financial news", "--count", "5" });
                    string sources = found.Content ?? string.Empty;
                    Assert.True(sources.Contains("sources for", StringComparison.OrdinalIgnoreCase),
                        "discovery returned nothing usable through the sandbox: " + sources);
                    Assert.True(sources.Contains("https://", StringComparison.Ordinal),
                        "discovery named no URL, so every index it asked was refused: " + sources);
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
    [LiveStagedPythonFact]
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
