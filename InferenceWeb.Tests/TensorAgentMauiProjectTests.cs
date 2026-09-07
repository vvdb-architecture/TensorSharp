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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace InferenceWeb.Tests;

/// <summary>
/// Drift guards for the TensorAgent iOS head (TensorAgent/src/TensorAgent.Maui).
///
/// <para>
/// The head cannot be referenced from a net10.0 test project (it targets
/// net10.0-ios and needs the maui-ios workload), and the facts that make it
/// work are all declarative: the static NativeReference to the GgmlOps
/// xcframework with the exported_symbol linker flags, the phone-specific Web UI
/// bundled from the app's own wwwroot, the loopback ATS exception,
/// and the device-only memory entitlements. Each of these was hit for real
/// while bringing the app up, and each fails silently when removed - the
/// project still builds, and the app then dies at first P/Invoke or shows a
/// blank WebView. So the tests read the project files as XML and pin them.
/// </para>
/// </summary>
public class TensorAgentMauiProjectTests
{
    private static readonly XNamespace Ns = XNamespace.None;

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TensorSharp.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    private static string MauiDir => Path.Combine(RepoRoot, "TensorAgent", "src", "TensorAgent.Maui");

    private static XDocument Csproj => XDocument.Load(Path.Combine(MauiDir, "TensorAgent.Maui.csproj"));

    private static string? Property(XDocument doc, string name) =>
        doc.Descendants(Ns + name).Select(e => e.Value.Trim()).FirstOrDefault();

    [Fact]
    public void Solution_ListsTheMauiHead()
    {
        XDocument slnx = XDocument.Load(Path.Combine(RepoRoot, "TensorAgent", "TensorAgent.slnx"));
        Assert.Contains(
            slnx.Descendants("Project"),
            p => p.Attribute("Path")?.Value.Replace('\\', '/') == "src/TensorAgent.Maui/TensorAgent.Maui.csproj");
    }

    [Fact]
    public void Head_TargetsIosWithTheAgreedIdentity()
    {
        XDocument doc = Csproj;
        Assert.Equal("net10.0-ios", Property(doc, "TargetFramework"));
        Assert.Equal("true", Property(doc, "UseMaui"));
        Assert.Equal("ai.tensorsharp.tensoragent", Property(doc, "ApplicationId"));
        Assert.Equal("TensorAgent", Property(doc, "ApplicationTitle"));
        // Must match build-ios.sh's deployment target or the static archive
        // refuses to link.
        Assert.Equal("17.0", Property(doc, "SupportedOSPlatformVersion"));
        Assert.Equal("partial", Property(doc, "TrimMode"));
        Assert.Equal("true", Property(doc, "JsonSerializerIsReflectionEnabledByDefault"));
        Assert.Equal("true", Property(doc, "TensorSharpSkipGgmlNative"));
        Assert.Equal("true", Property(doc, "TensorSharpSkipMlxNative"));
    }

    [Fact]
    public void Head_LinksGgmlOpsStaticallyAndExportsItsSymbols()
    {
        // The head links more than one framework now (CPython is embedded beside the
        // engine), so this picks out the engine's own reference rather than assuming
        // it is the only one.
        XElement native = Assert.Single(Csproj.Descendants(Ns + "NativeReference")
            .Where(e => e.Attribute("Include")!.Value.Replace('\\', '/').Contains("GgmlOps.xcframework", StringComparison.Ordinal)));
        Assert.EndsWith("TensorSharp.GGML.Native/build-ios/GgmlOps.xcframework", native.Attribute("Include")!.Value.Replace('\\', '/'));
        Assert.Equal("Static", native.Attribute("Kind")?.Value);
        Assert.Equal("True", native.Attribute("ForceLoad")?.Value);
        Assert.Equal("True", native.Attribute("IsCxx")?.Value);
        Assert.Equal("False", native.Attribute("SmartLink")?.Value);

        // GgmlNative.ImportResolver resolves DllImport("GgmlOps") to the main
        // program handle on iOS; dlsym only sees symbols the linker exported.
        string flags = native.Attribute("LinkerFlags")?.Value ?? string.Empty;
        Assert.Contains("-Wl,-exported_symbol,_TSGgml_*", flags);
        Assert.Contains("-Wl,-exported_symbol,_ggml_*", flags);
        Assert.Contains("-lc++", flags);

        string frameworks = native.Attribute("Frameworks")?.Value ?? string.Empty;
        foreach (string framework in new[] { "Foundation", "Metal", "MetalKit", "MetalPerformanceShaders", "MetalPerformanceShadersGraph", "Accelerate" })
            Assert.Contains(framework, frameworks.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Head_RetainsBonsaiNativeEntryPointsInRelease()
    {
        XDocument symbols = XDocument.Load(Path.Combine(MauiDir, "GgmlExportedSymbols.targets"));
        string[] retained = symbols.Descendants(Ns + "ReferenceNativeSymbol")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .ToArray();

        foreach (string export in new[]
                 {
                     "TSGgml_Qwen3ModelPrefill",
                     "TSGgml_Qwen3ModelDecodeLogits",
                     "TSGgml_Qwen3DropDecodeCache",
                     "TSGgml_Qwen3ResetDecodeCache",
                     "TSGgml_TransformerLayerDecode",
                     "TSGgml_TransformerModelDecode",
                     "TSGgml_Qwen35ArenaDiscardHostPointer",
                 })
        {
            Assert.Contains(export, retained);
        }

        // The failure is architectural rather than Bonsai-specific: an export
        // added to the static archive but omitted here survives Debug/simulator
        // builds and then disappears under the Release device strip step. Keep
        // the manifest identical to the native source exports so the next model
        // kernel cannot repeat that delayed EntryPointNotFound failure.
        string nativeDir = Path.Combine(RepoRoot, "TensorSharp.GGML.Native");
        var exportPattern = new Regex(
            @"^\s*TSG_EXPORT[^\r\n]*\b(TSGgml_[A-Za-z0-9_]+)\s*\(",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        string[] nativeExports = Directory.EnumerateFiles(nativeDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".cpp", StringComparison.Ordinal) ||
                           path.EndsWith(".c", StringComparison.Ordinal) ||
                           path.EndsWith(".h", StringComparison.Ordinal))
            .SelectMany(path => exportPattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(nativeExports,
            retained.Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Head_ReferencesAndRootsEveryEngineAssembly()
    {
        XDocument doc = Csproj;
        string[] engine =
        {
            "TensorSharp.Core",
            "TensorSharp.Runtime",
            "TensorSharp.Runtime.Logging",
            "TensorSharp.Models",
            "TensorSharp.Backends.GGML",
            "TensorSharp.AgentHost",
            "TensorSharp.Chat",
            "TensorAgent.Core",
        };

        var references = doc.Descendants(Ns + "ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToList();
        var roots = doc.Descendants(Ns + "TrimmerRootAssembly")
            .Select(e => e.Attribute("Include")!.Value)
            .ToList();

        foreach (string assembly in engine)
        {
            Assert.Contains(assembly, references);
            Assert.Contains(assembly, roots);
        }

        // TensorSharp.Server is ASP.NET Core, which has no iOS runtime pack.
        Assert.DoesNotContain("TensorSharp.Server", references);
    }

    [Fact]
    public void Head_BundlesItsOwnPhoneWwwroot()
    {
        // Several things are bundled now — the skills, the Python standard library —
        // so this is the one that matters: the phone-specific Web UI. It deliberately
        // differs from the desktop Server page while speaking the same loopback API.
        XElement bundle = Assert.Single(Csproj.Descendants(Ns + "BundleResource")
            .Where(e => e.Attribute("Include")!.Value.Replace('\\', '/') == "wwwroot/**/*"));
        Assert.StartsWith("webui/", bundle.Attribute("Link")?.Value.Replace('\\', '/'));

        string page = Path.Combine(MauiDir, "wwwroot", "index.html");
        Assert.True(File.Exists(page), "TensorAgent.Maui must carry its phone-specific index.html.");
        Assert.DoesNotContain(
            Csproj.Descendants(Ns + "BundleResource"),
            e => e.Attribute("Include")!.Value.Replace('\\', '/')
                .Contains("TensorSharp.Server/wwwroot", StringComparison.Ordinal));
    }

    [Fact]
    public void InfoPlist_AllowsLoopbackHttpAndDeclaresTheUsageStrings()
    {
        string plist = File.ReadAllText(Path.Combine(MauiDir, "Platforms", "iOS", "Info.plist"));
        // WKWebView -> http://127.0.0.1 is blocked by ATS without this.
        Assert.Contains("<key>NSAllowsLocalNetworking</key>", plist);
        foreach (string key in new[]
                 {
                     "NSCameraUsageDescription",
                     "NSMicrophoneUsageDescription",
                     "NSPhotoLibraryUsageDescription",
                     "NSSpeechRecognitionUsageDescription",
                     "UIFileSharingEnabled",
                     "LSSupportsOpeningDocumentsInPlace",
                 })
        {
            Assert.Contains($"<key>{key}</key>", plist);
        }
    }

    [Fact]
    public void Entitlements_AreDeviceOnlyAndRaiseTheMemoryLimit()
    {
        string entitlements = File.ReadAllText(Path.Combine(MauiDir, "Platforms", "iOS", "Entitlements.plist"));
        Assert.Contains("<key>com.apple.developer.kernel.increased-memory-limit</key>", entitlements);
        Assert.Contains("<key>com.apple.developer.kernel.extended-virtual-addressing</key>", entitlements);

        // The simulator has no jetsam limit and CompileEntitlements must not emit
        // codesign entitlements for simulator builds, so the file is wired up
        // for the device RID only.
        XElement codesign = Assert.Single(Csproj.Descendants(Ns + "CodesignEntitlements"));
        Assert.Contains("ios-arm64", codesign.Attribute("Condition")?.Value);
        Assert.DoesNotContain("simulator", codesign.Attribute("Condition")?.Value);
    }
}
