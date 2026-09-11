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
using System.Collections.Generic;
using System.Linq;
using TensorSharp.Runtime.Scheduling;
using Xunit;

namespace InferenceWeb.Tests;

/// <summary>
/// The engine's loop detector, on token streams shaped like the failure it was
/// written for and like the writing it must let through.
///
/// <para>
/// The failure: a 4-bit 9B model on a phone, writing XML inside a Python string,
/// fell into <c>","+","+","+"</c> and produced 230 copies of it over five and a half
/// minutes, ending only when the user tapped Stop. No sampling setting rules that
/// out for every model and every text, so the engine has to notice.
/// </para>
/// </summary>
public sealed class RepetitionGuardTests
{
    private static List<int> Repeat(IReadOnlyList<int> unit, int times, int prefixLength = 0)
    {
        var tokens = new List<int>();
        for (int i = 0; i < prefixLength; i++)
            tokens.Add(1000 + i);            // distinct, never periodic
        for (int r = 0; r < times; r++)
            tokens.AddRange(unit);
        return tokens;
    }

    [Fact]
    public void TheObservedLoop_ThreeTokenUnit_IsCaughtOnceItSpansTheMinimum()
    {
        // `","+"` -> three tokens. 42 copies is 126 tokens: not yet. 43 is 129: caught.
        var unit = new[] { 7, 8, 9 };
        List<int> notYet = Repeat(unit, 42, prefixLength: 50);
        Assert.False(RepetitionGuard.IsLooping(notYet, notYet.Count, out _, out _));

        List<int> caught = Repeat(unit, 43, prefixLength: 50);
        Assert.True(RepetitionGuard.IsLooping(caught, caught.Count, out int period, out int repeats));
        Assert.Equal(3, period);
        Assert.Equal(43, repeats);
    }

    [Fact]
    public void ASingleTokenRunNeeds128Copies()
    {
        List<int> short127 = Repeat(new[] { 5 }, 127, prefixLength: 10);
        Assert.False(RepetitionGuard.IsLooping(short127, short127.Count, out _, out _));

        List<int> run = Repeat(new[] { 5 }, 128, prefixLength: 10);
        Assert.True(RepetitionGuard.IsLooping(run, run.Count, out int period, out int repeats));
        Assert.Equal(1, period);
        Assert.Equal(128, repeats);
    }

    /// <summary>
    /// The corner the thresholds exist for: FOUR byte-identical copies of a long block
    /// is ordinary structured output — four empty paragraphs of OOXML, four identical
    /// table rows — and must survive. Eight in a row, with no variation at all, is not
    /// something a model writes on purpose.
    /// </summary>
    [Fact]
    public void ALongUnitNeedsEightCopies_AndTheLongestUnitCountsToo()
    {
        int[] paragraph = Enumerable.Range(200, 40).ToArray();
        foreach (int copies in new[] { 3, 4, 7 })
        {
            List<int> tolerated = Repeat(paragraph, copies, prefixLength: 5);
            Assert.False(RepetitionGuard.IsLooping(tolerated, tolerated.Count, out _, out _));
        }

        List<int> eight = Repeat(paragraph, 8, prefixLength: 5);
        Assert.True(RepetitionGuard.IsLooping(eight, eight.Count, out int period, out int repeats));
        Assert.Equal(40, period);
        Assert.Equal(8, repeats);

        int[] longest = Enumerable.Range(300, RepetitionGuard.MaxPeriod).ToArray();
        List<int> atLimit = Repeat(longest, RepetitionGuard.MinRepeats);
        Assert.True(RepetitionGuard.IsLooping(atLimit, atLimit.Count, out period, out _));
        Assert.Equal(RepetitionGuard.MaxPeriod, period);

        // A unit longer than the cap is never a loop, however many times it recurs.
        int[] tooLong = Enumerable.Range(300, RepetitionGuard.MaxPeriod + 1).ToArray();
        List<int> pastLimit = Repeat(tooLong, 12);
        Assert.False(RepetitionGuard.IsLooping(pastLimit, pastLimit.Count, out _, out _));
    }

    [Fact]
    public void WritingThatMerelyRepeatsShortStretchesIsLeftAlone()
    {
        // A wide markdown table's separator row: "| ---" ten times is 20 tokens.
        List<int> separator = Repeat(new[] { 30, 31 }, 10, prefixLength: 200);
        Assert.False(RepetitionGuard.IsLooping(separator, separator.Count, out _, out _));

        // A loop body that recurs with a varying line between copies is not periodic.
        var tokens = new List<int>();
        for (int i = 0; i < 60; i++)
        {
            tokens.AddRange(new[] { 40, 41, 42 });
            tokens.Add(500 + i);
        }
        Assert.False(RepetitionGuard.IsLooping(tokens, tokens.Count, out _, out _));

        // Fewer tokens than the minimum span can never be a loop, whatever they are.
        List<int> few = Repeat(new[] { 1 }, 100);
        Assert.False(RepetitionGuard.IsLooping(few, few.Count, out _, out _));
        Assert.False(RepetitionGuard.IsLooping(null, 0, out _, out _));
    }

    [Fact]
    public void OnlyTheCountedPrefixIsJudged_SoAnUnpublishedSpeculativeTailCannotTrigger()
    {
        // 127 copies then two more that a speculative step appended but the engine
        // has not yet accepted: judged at 127 the answer is no, at 129 it is yes.
        List<int> tokens = Repeat(new[] { 3 }, 129);
        Assert.False(RepetitionGuard.IsLooping(tokens, 127, out _, out _));
        Assert.True(RepetitionGuard.IsLooping(tokens, 129, out _, out int repeats));
        Assert.Equal(129, repeats);
    }

    [Fact]
    public void RepeatsAreCountedBackToWhereTheLoopBegan()
    {
        List<int> tokens = Repeat(new[] { 7, 8, 9 }, 230, prefixLength: 30);
        Assert.True(RepetitionGuard.IsLooping(tokens, tokens.Count, out int period, out int repeats));
        Assert.Equal(3, period);
        Assert.Equal(230, repeats);
    }

    [Fact]
    public void TheDescriptionQuotesTheUnitAndTheCount()
    {
        List<int> tokens = Repeat(new[] { 7, 8, 9 }, 43);
        Assert.True(RepetitionGuard.IsLooping(tokens, tokens.Count, out int period, out int repeats));

        string described = RepetitionGuard.Describe(tokens, tokens.Count, period, repeats,
            ids => string.Concat(ids.Select(id => id == 7 ? "\"," : id == 8 ? "\"" : "+")));
        Assert.Equal("the output repeated `\",\"+` 43 times in a row", described);

        // No tokenizer, or one that throws: still a sentence, never an exception.
        Assert.Equal("the output repeated a 3-token sequence 43 times in a row",
            RepetitionGuard.Describe(tokens, tokens.Count, period, repeats, null));
        Assert.Equal("the output repeated a 3-token sequence 43 times in a row",
            RepetitionGuard.Describe(tokens, tokens.Count, period, repeats, _ => throw new InvalidOperationException()));

        // Newlines are shown, not printed, and a long unit is cut.
        string multiline = RepetitionGuard.Describe(tokens, tokens.Count, period, repeats, _ => "a\nb");
        Assert.Contains("`a\\nb`", multiline);
        string longUnit = RepetitionGuard.Describe(tokens, tokens.Count, period, repeats, _ => new string('x', 100));
        Assert.Contains(new string('x', 48) + "…", longUnit);
    }

    [Fact]
    public void TheTextFormFindsTheSameLoopInCharacters()
    {
        string tail = "office_theme = '''<a:latin type=\"nn\" bon=\"Aa+" + string.Concat(Enumerable.Repeat("\",\"+", 230));
        Assert.True(RepetitionGuard.TryFindTextLoop(tail, out string unit, out int repeats));
        Assert.Equal("\",\"+", unit);
        Assert.Equal(230, repeats);

        // Seventy-eight characters, so the periodicity scan judges it rather than the
        // length guard turning it away before the scan is reached.
        Assert.False(RepetitionGuard.TryFindTextLoop(
            "a perfectly ordinary sentence, long enough to be scanned, with no loop in it at all.", out _, out _));
        Assert.False(RepetitionGuard.TryFindTextLoop(string.Empty, out _, out _));
        Assert.False(RepetitionGuard.TryFindTextLoop(null, out _, out _));
        // Sixty-three characters of one letter is under the minimum span; sixty-four is not.
        Assert.False(RepetitionGuard.TryFindTextLoop(new string('+', 63), out _, out _));
        Assert.True(RepetitionGuard.TryFindTextLoop(new string('+', 64), out unit, out repeats));
        Assert.Equal("+", unit);
        Assert.Equal(64, repeats);

        // A long unit is found too: the cap is 512 characters, not 128, because a
        // 64-token unit (the token guard's cap) can be several hundred characters and
        // the description would otherwise fall back to the vague sentence.
        string longUnit = new string('a', 200) + "|";
        Assert.True(RepetitionGuard.TryFindTextLoop(string.Concat(Enumerable.Repeat(longUnit, 4)), out unit, out repeats));
        Assert.Equal(longUnit, unit);
        Assert.Equal(4, repeats);
    }
}
