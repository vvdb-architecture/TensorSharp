// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorSharp.AgentHost.Skills;

namespace TensorAgent.Tests;

/// <summary>
/// Every skill this app bundles has to appear in the catalog the model is given.
///
/// <para>
/// It did not. The catalog is filled in ordinal id order under a 1,024-token budget,
/// and the 13 bundled descriptions come to roughly 1,450 tokens — so six were dropped,
/// and which six was decided by the alphabet. The survivors were academy-guide,
/// algorithmic-art, brand-guidelines, canvas-design, discernment-nudge, doc-coauthoring
/// and frontend-design; the casualties included <c>documents</c> and <c>research</c>,
/// the two that TensorAgentSkillRouter itself routes to. Two ~990-character entries at
/// the head of the alphabet took half the budget between them.
/// </para>
/// <para>
/// It is not a theoretical loss. Asked to look something up, a model on this build
/// reasoned that it had "skills for learning, art, design, and documentation" — the
/// seven that survived — found nothing that fetches a page, and refused the request.
/// The router's own comment records the other half of it: "Qwen overlooked the
/// documents catalog entry". There was no documents catalog entry.
/// </para>
/// </summary>
public sealed class SkillCatalogVisibilityTests
{
    /// <summary>The budget a phone actually gets: Clamp(context * 2%, 1024, 10000).</summary>
    private const int PhoneContextTokens = 32_768;

    private static SkillRegistry BundledSkills()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, "TensorAgent", "skills")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return new SkillRegistry(new SkillRegistryOptions
        {
            Roots = new[] { Path.Combine(directory!.FullName, "TensorAgent", "skills") },
        });
    }

    [Fact]
    public void EveryBundledSkillReachesTheModelsCatalog()
    {
        SkillRegistry registry = BundledSkills();
        Assert.True(registry.Skills.Count >= 12,
            $"only {registry.Skills.Count} skills were discovered; the bundle is not being read");

        SkillPlan plan = SkillPrompt.Plan(
            Array.Empty<Skill>(),
            registry.Skills,
            new SkillPromptOptions { ContextTokens = PhoneContextTokens, ToolsAvailable = true });

        Assert.Equal(registry.Skills.Count, plan.Catalog.Count);
        Assert.Equal(0, plan.OmittedFromCatalog);

        // The two the app's own router depends on, named rather than merely counted:
        // they are the ones the alphabet dropped, and a count could pass while they are
        // the pair still missing.
        foreach (string required in new[] { "documents", "research" })
        {
            Assert.Contains(plan.Catalog, skill =>
                string.Equals(skill.Id, required, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("- " + required + ":", plan.Instructions, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Shortening is what makes room, so each line must still say enough to choose on,
    /// and the whole block must still fit the budget it was fitted to.
    /// </summary>
    [Fact]
    public void TheCatalogFitsItsBudgetAndEveryLineStillDescribesItsSkill()
    {
        SkillRegistry registry = BundledSkills();
        SkillPlan plan = SkillPrompt.Plan(
            Array.Empty<Skill>(),
            registry.Skills,
            new SkillPromptOptions { ContextTokens = PhoneContextTokens, ToolsAvailable = true });

        // That it fits is what OmittedFromCatalog == 0 above already proves — the fit
        // is the production code's arithmetic and re-deriving it here would only test
        // the copy. What this checks is the MECHANISM that made it fit: the rendered
        // lines are materially shorter than the raw descriptions they came from.
        int rendered = plan.Instructions
            .Split('\n')
            .Where(l => registry.Skills.Any(s => l.StartsWith("- " + s.Id + ":", StringComparison.Ordinal)))
            .Sum(l => l.Length);
        int raw = registry.Skills.Sum(s => s.Description.Length + s.Id.Length + 4);
        Assert.True(rendered < raw,
            $"nothing was shortened ({rendered} vs {raw} chars), yet all {registry.Skills.Count} fitted — "
            + "either the budget grew or this test is no longer measuring anything");

        foreach (Skill skill in registry.Skills)
        {
            string line = plan.Instructions
                .Split('\n')
                .Single(l => l.StartsWith("- " + skill.Id + ":", StringComparison.Ordinal));
            string described = line[(skill.Id.Length + 4)..].Trim();
            Assert.True(described.Length >= 60,
                $"'{skill.Id}' is described in {described.Length} characters, too few to choose on: {described}");
        }
    }

    /// <summary>
    /// A catalog that already fits is left exactly as it was — the shortening exists to
    /// prevent eviction, not to trim prompts that were never in trouble.
    /// </summary>
    [Fact]
    public void ACatalogThatAlreadyFitsIsNotShortened()
    {
        SkillRegistry registry = BundledSkills();
        Skill[] few = registry.Skills.Take(3).ToArray();

        SkillPlan plan = SkillPrompt.Plan(
            Array.Empty<Skill>(), few,
            new SkillPromptOptions { ContextTokens = PhoneContextTokens, ToolsAvailable = true });

        Assert.Equal(3, plan.Catalog.Count);
        foreach (Skill skill in few)
        {
            Assert.Contains(
                "- " + skill.Id + ": " + skill.Description.Replace("\n", " "),
                plan.Instructions.Replace("\n- ", "- ").Replace("\n", " ").Replace("- ", "\n- "),
                StringComparison.Ordinal);
        }
    }
}
