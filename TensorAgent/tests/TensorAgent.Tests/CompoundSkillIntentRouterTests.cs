using System.Text;
using TensorAgent.Core.Hosting;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Chat;
using TensorSharp.Runtime;

namespace TensorAgent.Tests;

/// <summary>
/// The deterministic front door for the reported research-to-PPTX failure. These are
/// deliberately model-free: whether a prompt is routed must not depend on sampling.
/// </summary>
public sealed class CompoundSkillIntentRouterTests : IDisposable
{
    private const string ExactPrompt = "搜索apple M6的信息，并对比M5芯片，然后生成pptx报告";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-compound-router-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TheReportedChineseRequestSelectsBothRequiredSkills()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), requestedSkills: null, registry);

        Assert.NotNull(route);
        Assert.Equal(
            new[] { TensorAgentSkillRouter.ResearchSkill, TensorAgentSkillRouter.DocumentsSkill },
            route.Skills);
        WebUiArtifactRequirement artifact = Assert.IsType<WebUiArtifactRequirement>(route.ArtifactRequirement);
        Assert.Equal(".pptx", artifact.Extension);
        Assert.Equal(4, artifact.MinimumSlides);
        Assert.Equal(new[] { "M5", "M6" }, artifact.RequiredVisibleTerms);
        Assert.True(artifact.RequireVisibleHttpUrl);
        Assert.Equal("notes.md", artifact.CitationEvidencePath);
        Assert.True(route.RequiresNetwork);
        Assert.Collection(
            artifact.RequiredRuns,
            run =>
            {
                Assert.Equal("research", run.SkillId);
                Assert.Equal("scripts/research.py", run.ResourcePath);
                Assert.False(run.ProducesArtifact);
                Assert.Equal(
                    new[]
                    {
                        "Apple M6 chip specifications release information compared with Apple M5 chip. "
                            + "User-requested focus: 搜索apple M6的信息，并对比M5芯片",
                        "--pages", "3", "--out", "notes.md",
                    },
                    run.DefaultArguments);
                Assert.True(run.EnforceArguments);
                Assert.Null(run.RequiredInputPath);
            },
            run =>
            {
                Assert.Equal("documents", run.SkillId);
                Assert.Equal("scripts/make_pptx.py", run.ResourcePath);
                Assert.True(run.ProducesArtifact);
                Assert.Equal(
                    new[] { "--spec", "pptx_spec.json", "--out", "report.pptx" },
                    run.DefaultArguments);
                Assert.True(run.EnforceArguments);
                Assert.Equal("pptx_spec.json", run.RequiredInputPath);
            });
        Assert.Contains("skills_read(skill=\"research\"", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("skills_read(skill=\"documents\"", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("source URLs and dates", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("exactly three slide entries", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("below 6,000 characters", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("without reading, copying, or rewriting that script", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("real .pptx", route.Instructions, StringComparison.Ordinal);
        Assert.Contains("shared workspace", route.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEquivalentEnglishRequestIsAlsoCompound()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn("Search for current Apple M6 information, compare it with M5, and create a PowerPoint report."),
            requestedSkills: null,
            registry);

        Assert.NotNull(route);
        Assert.Equal(2, route.Skills.Count);
    }

    [Fact]
    public void AnUnrelatedResearchDeckIsNotBroadenedByTheTargetedRoute()
    {
        const string prompt = "Search current Rust releases and create a PowerPoint report.";
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn(prompt), requestedSkills: null, registry);

        Assert.Null(route);
    }

    [Fact]
    public void ALongCompoundRequestProducesABoundedValidResearchArgument()
    {
        string prompt = "Search Apple M6 information and compare it with M5 " + new string('x', 5_000)
            + " and create a PowerPoint report.";
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn(prompt), requestedSkills: null, registry);

        Assert.NotNull(route);
        string query = route.ArtifactRequirement.RequiredRuns[0].DefaultArguments[0];
        Assert.InRange(query.Length, 1, 4096);
        Assert.StartsWith(
            "Apple M6 chip specifications release information compared with Apple M5 chip. "
                + "User-requested focus: Search Apple M6 information and compare it with M5",
            query,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("搜索 Apple M6 的信息并与 M5 对比")]
    [InlineData("根据我已经附上的资料生成 pptx 报告")]
    [InlineData("Research how to repair a timber slide deck")]
    public void ARequestWithOnlyOneSideOfTheWorkflowIsNotBroadened(string prompt)
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(prompt), requestedSkills: null, registry));
    }

    [Fact]
    public void CuesInDifferentTurnsDoNotBecomeOneCompoundRequest()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Search the web for Apple M6." },
            new() { Role = "assistant", Content = "Here is what I found." },
            new() { Role = "user", Content = "Summarize the attached notes as a pptx." },
        };

        Assert.Null(TensorAgentSkillRouter.Route(messages, requestedSkills: null, registry));
    }

    [Theory]
    [InlineData("Search Apple M50 and M60 information, compare them, and create a PowerPoint report.")]
    [InlineData("Search Apple AM5 and XM6 information, compare them, and create a PowerPoint report.")]
    public void ChipNamesMustBeWholeAsciiTokens(string prompt)
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(prompt), requestedSkills: null, registry));
    }

    [Fact]
    public void AttachedTextCannotActivateTheAutomaticNetworkRoute()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());
        var message = new ChatMessage
        {
            Role = "user",
            Content = "summarize this attachment\n\n" + ExactPrompt,
            AttachmentPaths = new List<string> { "uploaded.txt" },
        };

        Assert.Null(TensorAgentSkillRouter.Route(
            new[] { message }, requestedSkills: null, registry));
    }

    [Theory]
    [InlineData("Research why tools generate PowerPoint presentations")]
    [InlineData("Look up articles that explain how software creates PPTX files")]
    public void AnExplanatoryMentionOfDeckGenerationDoesNotForceADeliverable(string prompt)
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(prompt), requestedSkills: null, registry));
    }

    [Fact]
    public void AnExplicitSkillSelectionIsNeverBroadened()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), new[] { TensorAgentSkillRouter.DocumentsSkill }, registry);

        Assert.Null(route);
    }

    [Fact]
    public void AnExplicitEmptySkillSelectionIsAlsoAnOptOut()
    {
        SkillRegistry registry = Registry(ResearchManifest(), DocumentsManifest());

        WebUiSkillRoute? route = TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), Array.Empty<string>(), registry);

        Assert.Null(route);
    }

    [Fact]
    public void RoutingDoesNotClaimARequiredSkillThatIsNotInstalled()
    {
        SkillRegistry registry = Registry(DocumentsManifest());

        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), requestedSkills: null, registry));
    }

    [Theory]
    [InlineData(TensorAgentSkillRouter.ResearchSkill, "research-v2.py")]
    [InlineData(TensorAgentSkillRouter.DocumentsSkill, "make_pptx-v2.py")]
    public void InstalledShadowWithoutItsExactRequiredScriptCannotActivateTheRoute(
        string shadowedSkill,
        string wrongScriptName)
    {
        string bundled = Path.Combine(_root, "bundled");
        string installed = Path.Combine(_root, "installed");
        WriteSkill(bundled, TensorAgentSkillRouter.ResearchSkill, ResearchManifest(), includeScript: true);
        WriteSkill(bundled, TensorAgentSkillRouter.DocumentsSkill, DocumentsManifest(), includeScript: true);
        string shadowManifest = shadowedSkill == TensorAgentSkillRouter.ResearchSkill
            ? ResearchManifest()
            : DocumentsManifest();
        WriteSkill(installed, shadowedSkill, shadowManifest, includeScript: false);
        string wrongScripts = Path.Combine(installed, shadowedSkill, "scripts");
        Directory.CreateDirectory(wrongScripts);
        File.WriteAllText(Path.Combine(wrongScripts, wrongScriptName), "print('wrong resource')");

        var registry = new SkillRegistry(new SkillRegistryOptions
        {
            Roots = new[] { bundled },
            InstallDirectory = installed,
        });

        Assert.True(registry.TryGet(shadowedSkill, out Skill winner));
        Assert.Equal(SkillOrigin.Installed, winner.Origin);
        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), requestedSkills: null, registry));
    }

    [Theory]
    [InlineData(TensorAgentSkillRouter.ResearchSkill)]
    [InlineData(TensorAgentSkillRouter.DocumentsSkill)]
    public void InstalledShadowWithTheSameScriptPathStillCannotBeAutoExecuted(
        string shadowedSkill)
    {
        string bundled = Path.Combine(_root, "bundled-exact");
        string installed = Path.Combine(_root, "installed-exact");
        WriteSkill(bundled, TensorAgentSkillRouter.ResearchSkill, ResearchManifest(), includeScript: true);
        WriteSkill(bundled, TensorAgentSkillRouter.DocumentsSkill, DocumentsManifest(), includeScript: true);
        WriteSkill(
            installed,
            shadowedSkill,
            shadowedSkill == TensorAgentSkillRouter.ResearchSkill
                ? ResearchManifest("USER_CONTROLLED_SENTINEL")
                : DocumentsManifest("USER_CONTROLLED_SENTINEL"),
            includeScript: true);

        var registry = new SkillRegistry(new SkillRegistryOptions
        {
            Roots = new[] { bundled },
            InstallDirectory = installed,
        });

        Assert.True(registry.TryGet(shadowedSkill, out Skill winner));
        Assert.Equal(SkillOrigin.Installed, winner.Origin);
        Assert.Null(TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), requestedSkills: null, registry));
    }

    [Fact]
    public void TheEightKPromptSelectsBothSkillsWithoutInliningTheirBodies()
    {
        const string researchSentinel = "RESEARCH_FULL_BODY_MUST_NOT_BE_INLINED";
        const string documentsSentinel = "DOCUMENTS_FULL_BODY_MUST_NOT_BE_INLINED";
        string research = ResearchManifest(researchSentinel + new string('R', 13_000));
        string documents = DocumentsManifest(documentsSentinel + new string('D', 13_000));
        SkillRegistry registry = Registry(research, documents);
        WebUiSkillRoute route = Assert.IsType<WebUiSkillRoute>(TensorAgentSkillRouter.Route(
            UserTurn(ExactPrompt), requestedSkills: null, registry));

        IReadOnlyList<Skill> selected = registry.Resolve(route.Skills, out IReadOnlyList<string> unknown);
        SkillPlan plan = SkillPrompt.Plan(selected, registry.Skills, new SkillPromptOptions
        {
            ContextTokens = 8_192,
            ToolsAvailable = true,
        });
        List<ChatMessage> messages = SkillPrompt.Apply(UserTurn(ExactPrompt).ToList(), plan);
        messages = SkillPrompt.Apply(messages, route.Instructions);

        Assert.Empty(unknown);
        Assert.Equal(2, plan.Selected.Count);
        Assert.Empty(plan.Inlined);
        Assert.Equal(2, plan.Deferred.Count);
        Assert.DoesNotContain(researchSentinel, messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(documentsSentinel, messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("- documents:", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("- research:", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(TensorAgentSkillRouter.ActivationInstructions, messages[0].Content, StringComparison.Ordinal);

        int promptBytes = Encoding.UTF8.GetByteCount(messages[0].Content);
        Assert.True(promptBytes < 6_000,
            $"The compact route used {promptBytes} prompt bytes; it should not approach the 26 KB skill bodies.");
    }

    private static IReadOnlyList<ChatMessage> UserTurn(string content) =>
        new[] { new ChatMessage { Role = "user", Content = content } };

    private SkillRegistry Registry(params string[] manifests)
    {
        foreach (string manifest in manifests)
        {
            string name = manifest.Contains("name: research", StringComparison.Ordinal)
                ? TensorAgentSkillRouter.ResearchSkill
                : TensorAgentSkillRouter.DocumentsSkill;
            WriteSkill(_root, name, manifest, includeScript: true);
        }

        return new SkillRegistry(new SkillRegistryOptions { Roots = new[] { _root } });
    }

    private static void WriteSkill(string root, string name, string manifest, bool includeScript)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SkillManifestParser.SkillFileName), manifest);
        if (!includeScript)
            return;

        string scripts = Path.Combine(directory, "scripts");
        Directory.CreateDirectory(scripts);
        string scriptName = name == TensorAgentSkillRouter.ResearchSkill
            ? "research.py"
            : "make_pptx.py";
        File.WriteAllText(Path.Combine(scripts, scriptName), "print('fixture')");
    }

    private static string ResearchManifest(string? body = null) => $$"""
        ---
        name: research
        description: Research a question on the open web, find sources, compare claims, and cite their URLs and dates.
        ---
        # Research
        Use the bundled research workflow and write notes.md plus notes.json.
        {{body}}
        """;

    private static string DocumentsManifest(string? body = null) => $$"""
        ---
        name: documents
        description: Create real PDF, XLSX, DOCX and PPTX documents with bundled writers in the shared workspace.
        ---
        # Documents
        Use the bundled make_pptx.py writer for a PowerPoint deck.
        {{body}}
        """;
}
