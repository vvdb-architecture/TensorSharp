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
using TensorSharp.Server.Hosting;
using TensorSharp.Runtime;
using TensorSharp.Server.Skills;

namespace InferenceWeb.Tests;

/// <summary>
/// One <c>skills_read</c> must not be able to overrun the model's whole context.
///
/// <para>
/// The ceiling was a flat 48 KB — a quarter of a 48k-token window, and twice the
/// ENTIRE window of an 8k one. Observed on a real run: a model with 8,192 tokens of
/// context read a 17 KB SKILL.md, and the following round threw
/// <c>PromptContextOverflowException</c> ("the protected prompt requires 10572
/// tokens"), which ends the turn. Six rounds of work were discarded — including the
/// round that had already fetched the data the answer needed.
/// </para>
/// </summary>
public class SkillReadContextBudgetTests
{
    [Fact]
    public void TheReadCeilingFollowsTheContextAndNeverExceedsTheOldDefault()
    {
        // A window large enough for the old default keeps it, unchanged.
        Assert.Equal(SkillTools.DefaultMaxReadBytes, SkillRequestPlan.ReadCapFor(262_144));
        Assert.Equal(SkillTools.DefaultMaxReadBytes, SkillRequestPlan.ReadCapFor(196_608));

        // A quarter of the context, at four bytes per token.
        Assert.Equal(32_768, SkillRequestPlan.ReadCapFor(32_768));
        Assert.Equal(8_192, SkillRequestPlan.ReadCapFor(8_192));

        // Small models still get a floor worth reading: a 2 KB cap would make
        // skills_read useless rather than merely bounded.
        Assert.Equal(8 * 1024, SkillRequestPlan.ReadCapFor(4_096));

        // A caller that does not know the context is not punished for it.
        Assert.Equal(SkillTools.DefaultMaxReadBytes, SkillRequestPlan.ReadCapFor(0));
        Assert.Equal(SkillTools.DefaultMaxReadBytes, SkillRequestPlan.ReadCapFor(-1));
    }

    [Fact]
    public void AnOversizedSkillFileComesBackTruncatedWithAWayToContinue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-readcap-" + Guid.NewGuid().ToString("N"));
        string skill = Path.Combine(dir, "big");
        Directory.CreateDirectory(skill);
        try
        {
            File.WriteAllText(Path.Combine(skill, "SKILL.md"),
                "---\nname: big\ndescription: A skill whose instructions are longer than a small model's context.\n---\n\n"
                + string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"Step {i}: do the {i}th thing carefully.")));

            var registry = new SkillRegistry(new SkillRegistryOptions { Roots = new[] { dir } });
            ServerHostingOptions options = ServerOptionsBuilder.Build(
                new[] { "--model", "x.gguf", "--skills-dir", dir }, dir);

            SkillRequestPlan plan = SkillRequestPlan.Create(
                registry, new[] { "big" }, discovery: false, clientTools: null,
                architecture: "gemma4", contextTokens: 8_192, options,
                out IReadOnlyList<string> unknown);

            Assert.Empty(unknown);
            Assert.NotNull(plan);
            Assert.Equal(8_192, plan.ToolContext.MaxReadBytes);

            SkillToolResult read = SkillTools.Execute(
                new ToolCall
                {
                    Name = SkillTools.ReadToolName,
                    Arguments = new Dictionary<string, object>
                    {
                        ["skill"] = "big",
                        ["path"] = "SKILL.md",
                    },
                },
                plan.ToolContext);

            Assert.True(read.Ok, read.Content);
            string result = read.Content ?? string.Empty;
            // Bounded by the cap, not by the file: the whole point is that one read
            // cannot spend the window the conversation still needs.
            Assert.True(result.Length < 12_000,
                $"the read returned {result.Length} chars against an 8,192-byte ceiling");
            Assert.Contains("Truncated", result, StringComparison.OrdinalIgnoreCase);
            // Truncation is only acceptable because the model is told how to go on.
            Assert.Contains("offset", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
