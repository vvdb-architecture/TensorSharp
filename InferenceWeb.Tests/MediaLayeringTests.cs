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
using System.Xml.Linq;
using TensorSharp.Models.Media;
using TensorSharp.Models.Media.Desktop;

namespace InferenceWeb.Tests;

/// <summary>
/// The seam TensorSharp.Models has for native media: everything that names OpenCvSharp,
/// ImageMagick or a spawned process lives under <c>Media/Desktop/</c>, which the iOS target
/// compiles out. These guards fail the moment a native using drifts back into shared code
/// (which would compile fine on desktop and throw DllNotFoundException on a phone), or the
/// csproj stops keeping the native packages off the ios target.
/// </summary>
public class MediaLayeringTests
{
    private static readonly Regex NativeMediaUse = new(
        @"using\s+OpenCvSharp\b|using\s+ImageMagick\b|\bOpenCvSharp\.[A-Z]|\bImageMagick\.[A-Z]|" +
        @"System\.Diagnostics\.Process\b|\bProcess\.Start\s*\(|\bnew\s+(System\.Diagnostics\.)?ProcessStartInfo\b",
        RegexOptions.Compiled);

    [Fact]
    public void NothingOutsideMediaDesktop_NamesOpenCvMagickOrProcess()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot == null)
            return;

        string models = Path.Combine(repoRoot, "TensorSharp.Models");
        if (!Directory.Exists(models))
            return;

        string desktop = Path.Combine(models, "Media", "Desktop") + Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(models, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.StartsWith(desktop, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Prose may mention the libraries; only code counts.
                string code = lines[i].Split("//", 2)[0];
                if (NativeMediaUse.IsMatch(code))
                    offenders.Add($"{Path.GetRelativePath(models, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these reach a native media library or spawn a process outside Media/Desktop, which compiles on "
            + "desktop and fails at runtime on iOS (no ios-arm64 OpenCV/Magick binaries, no processes):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheDesktopProvider_RegistersItselfBeforeAnyCallerRuns()
    {
        // The module initializer in Media/Desktop ran when the Models assembly loaded, so a
        // desktop host that never heard of MediaCodecs still gets OpenCV + Magick.NET.
        Assert.Same(DesktopMediaProvider.Instance, MediaCodecs.Image);
        Assert.Same(DesktopMediaProvider.Instance, MediaCodecs.Video);
        Assert.Same(DesktopMediaProvider.Instance, MediaCodecs.VideoEncoder);

        // Audio was never native on desktop; it stays on the managed decoders.
        Assert.Same(ManagedMediaProvider.Instance, MediaCodecs.Audio);
        Assert.Contains("DesktopMediaProvider", MediaCodecs.Describe());
    }

    [Fact]
    public void MediaCodecs_RefuseNullProviders()
    {
        Assert.Throws<ArgumentNullException>(() => MediaCodecs.Image = null!);
        Assert.Throws<ArgumentNullException>(() => MediaCodecs.Video = null!);
        Assert.Throws<ArgumentNullException>(() => MediaCodecs.VideoEncoder = null!);
        Assert.Throws<ArgumentNullException>(() => MediaCodecs.Audio = null!);
    }

    [Fact]
    public void TheModelsCsproj_KeepsNativePackagesAndDesktopSourcesOffTheIosTarget()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot == null)
            return;
        string csproj = Path.Combine(repoRoot, "TensorSharp.Models", "TensorSharp.Models.csproj");
        if (!File.Exists(csproj))
            return;

        XDocument doc = XDocument.Load(csproj);

        // The ios target is opt-in, so desktop builds keep the single TFM and its output path.
        XElement? frameworks = doc.Descendants("TargetFrameworks").FirstOrDefault();
        Assert.NotNull(frameworks);
        Assert.Contains("net10.0-ios", frameworks!.Value);
        Assert.Contains("TensorSharpIosTargets", (string?)frameworks.Attribute("Condition") ?? string.Empty);
        XElement? framework = doc.Descendants("TargetFramework").FirstOrDefault();
        Assert.NotNull(framework);
        Assert.Equal("net10.0", framework!.Value);

        foreach (XElement package in doc.Descendants("PackageReference"))
        {
            string include = (string?)package.Attribute("Include") ?? string.Empty;
            string condition = (string?)package.Parent?.Attribute("Condition") ?? string.Empty;
            bool native = include.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase)
                          || include.Contains("OpenCvSharp", StringComparison.OrdinalIgnoreCase);
            if (native)
            {
                Assert.True(condition.Contains("'$(TargetPlatformIdentifier)' != 'ios'", StringComparison.Ordinal),
                    $"{include} has no ios-arm64 binaries and must sit in the ItemGroup conditioned off the ios target");
            }
            else
            {
                Assert.True(condition.Length == 0, $"{include} is pure managed and should be referenced on every target");
            }
        }

        XElement? removal = doc.Descendants("Compile")
            .FirstOrDefault(c => ((string?)c.Attribute("Remove") ?? string.Empty).Replace('\\', '/') == "Media/Desktop/**");
        Assert.NotNull(removal);
        Assert.Contains("'$(TargetPlatformIdentifier)' == 'ios'", (string?)removal!.Parent?.Attribute("Condition") ?? string.Empty);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TensorSharp.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
