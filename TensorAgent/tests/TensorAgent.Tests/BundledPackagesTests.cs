// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.RegularExpressions;
using TensorAgent.Core.Python;

namespace TensorAgent.Tests;

/// <summary>
/// What the app knows about its own bundle before it asks PyPI anything.
///
/// <para>
/// The failure this guards was observed on a phone: asked for a .pptx, the model
/// wrote a python-pptx script, was told <c>lxml</c> could not be installed because
/// "every published wheel is built for a specific interpreter and platform", and
/// produced an HTML file instead. lxml is compiled into the app; the installer had
/// simply never been told, and consulted the index for something it was standing on.
/// </para>
/// </summary>
public sealed class BundledPackagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-bundle-" + Guid.NewGuid().ToString("N"));

    public BundledPackagesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>
    /// Lays down what a staged wheel leaves behind: a dist-info with METADATA and a
    /// RECORD that still names the .so files the staging turned into frameworks.
    /// </summary>
    private void Stage(string distInfo, string name, string version, params string[] recordPaths)
    {
        string info = Path.Combine(_root, distInfo);
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Combine(info, "METADATA"),
            $"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\nSummary: probe\n\nA long description that mentions Name: Decoy\n");
        if (recordPaths.Length > 0)
            File.WriteAllLines(Path.Combine(info, "RECORD"), recordPaths.Select(p => p + ",sha256=abc,1"));
    }

    [Fact]
    public void TheScanReadsNameVersionAndWhetherExtensionModulesShip()
    {
        Stage("lxml-6.1.3.dist-info", "lxml", "6.1.3",
            "lxml/__init__.py", "lxml/etree.cpython-313-iphoneos.so", "lxml-6.1.3.dist-info/RECORD");
        Stage("python_pptx-1.0.2.dist-info", "python-pptx", "1.0.2",
            "pptx/__init__.py", "pptx/api.py");
        Stage("typing_extensions-4.16.0.dist-info", "typing_extensions", "4.16.0",
            "typing_extensions.py");
        // Already converted: RECORD rewritten to the placeholder spelling still counts.
        Stage("pillow-10.4.0.dist-info", "Pillow", "10.4.0",
            "PIL/__init__.py", "PIL/_imaging.cpython-313-iphoneos.fwork");
        // No METADATA at all: not a distribution, not listed, not an exception.
        Directory.CreateDirectory(Path.Combine(_root, "broken-0.1.dist-info"));

        IReadOnlyList<BundledDistribution> found = BundledPackages.Scan(_root);

        Assert.Equal(new[] { "lxml", "pillow", "python-pptx", "typing-extensions" },
            found.Select(d => d.CanonicalName).ToArray());
        BundledDistribution lxml = found.Single(d => d.CanonicalName == "lxml");
        Assert.Equal("6.1.3", lxml.Version);
        Assert.True(lxml.Compiled);
        Assert.True(found.Single(d => d.CanonicalName == "pillow").Compiled);
        BundledDistribution pptx = found.Single(d => d.CanonicalName == "python-pptx");
        Assert.Equal("python-pptx", pptx.Name);
        Assert.False(pptx.Compiled);
        Assert.False(found.Single(d => d.CanonicalName == "typing-extensions").Compiled);
    }

    [Fact]
    public void AMissingOrEmptyPackageDirectoryIsAnEmptyAnswer()
    {
        Assert.Empty(BundledPackages.Scan(null));
        Assert.Empty(BundledPackages.Scan(Path.Combine(_root, "nowhere")));
        Assert.Empty(BundledPackages.Scan(_root));
    }

    [Theory]
    [InlineData("python-pptx", "python-pptx")]
    [InlineData("Python_PPTX", "python-pptx")]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("typing_extensions", "typing-extensions")]
    [InlineData("PyYAML", "pyyaml")]
    [InlineData("lxml", "lxml")]
    public void CanonicalNamesFollowPep503(string spelled, string canonical)
        => Assert.Equal(canonical, BundledPackages.Canonical(spelled));

    /// <summary>
    /// The list the model is TOLD is bundled and the list the staging script actually
    /// stages are two documents, and this holds them to each other in both
    /// directions: a package added to prepare-python.sh the model is not told about is
    /// one it will try to install; one the model is told about that is no longer
    /// staged is a promise the phone breaks at import time.
    /// </summary>
    [Fact]
    public void TheBundledListToldToTheModelMatchesTheStagingScript()
    {
        string script = File.ReadAllText(Path.Combine(FindRepoRoot(), "TensorAgent", "scripts", "prepare-python.sh"));
        var staged = new HashSet<string>(StringComparer.Ordinal);
        var compiledStaged = new HashSet<string>(StringComparer.Ordinal);
        foreach (string list in new[] { "BINARY_PACKAGES", "LOCAL_BINARY_PACKAGES", "PURE_PACKAGES" })
        {
            Match block = Regex.Match(script, list + @"=\((?<body>.*?)\n\)", RegexOptions.Singleline);
            Assert.True(block.Success, $"prepare-python.sh no longer declares {list}");
            foreach (Match spec in Regex.Matches(block.Groups["body"].Value, "\"(?<spec>[A-Za-z0-9_.-]+)(==[^\"]*)?\""))
            {
                string canonical = BundledPackages.Canonical(spec.Groups["spec"].Value);
                staged.Add(canonical);
                if (list != "PURE_PACKAGES")
                    compiledStaged.Add(canonical);
            }
        }
        Assert.True(staged.Count >= 10, "the staging lists parsed to almost nothing; the regex is broken");

        var told = InstallHookPackageInstaller.BundledPackageNames
            .Select(entry => BundledPackages.Canonical(entry.Split(' ')[0]))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(staged.Except(told));
        Assert.Empty(told.Except(staged));

        // The "cannot be replaced" sentence names exactly the distributions the two
        // binary lists stage: a compiled package the model is not told about is one it
        // will try to replace, and a pure one called compiled is a download refused.
        var compiledTold = InstallHookPackageInstaller.CompiledBundledPackageNames
            .Select(BundledPackages.Canonical)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(compiledStaged.OrderBy(n => n, StringComparer.Ordinal),
            compiledTold.OrderBy(n => n, StringComparer.Ordinal));

        // Every entry whose import name differs from its distribution name says so —
        // that is the part of the sentence a model acts on — and the sentence the model
        // reads embeds the list verbatim.
        var importOf = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Pillow"] = "PIL",
            ["python-pptx"] = "pptx",
            ["python-docx"] = "docx",
            ["XlsxWriter"] = "xlsxwriter",
            ["PyYAML"] = "yaml",
        };
        foreach ((string distribution, string module) in importOf)
        {
            Assert.Contains($"{distribution} (import {module})", InstallHookPackageInstaller.BundledPackageNames);
        }
        Assert.Contains(string.Join(", ", InstallHookPackageInstaller.BundledPackageNames),
            InstallHookPackageInstaller.ModelProvidedPackagesInstructions, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "TensorAgent", "skills")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
