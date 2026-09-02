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

            const string probe = """
                import sys
                missing = []
                for name in ("reportlab", "pypdf", "openpyxl", "defusedxml", "PIL"):
                    try:
                        __import__(name)
                    except Exception as exc:
                        missing.append(f"{name}: {type(exc).__name__}: {exc}")
                if missing:
                    print("MISSING " + " | ".join(missing))
                    print("sys.path = " + repr(sys.path))
                    raise SystemExit(1)
                print("all present")
                """;

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

}
