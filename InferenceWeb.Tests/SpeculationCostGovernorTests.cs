using System.Diagnostics;
using TensorSharp.Runtime.Speculative;
using Xunit;

namespace InferenceWeb.Tests;

/// <summary>The cost governor's verdicts, driven with synthetic step timings.</summary>
public class SpeculationCostGovernorTests
{
    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    /// <summary>Drive a full first round: a probe that wins (4 tokens per 20 ms step
    /// against a 15 ms plain step) and the plain calibration behind it.</summary>
    private static SpeculationCostGovernor WinningRound()
    {
        var g = new SpeculationCostGovernor { Enabled = true };
        g.Reset();
        // The first speculative sample is discarded (graph builds), then 8 are measured.
        for (int i = 0; i < 9; i++)
        {
            Assert.True(g.AllowsSpeculation(), $"probe step {i}");
            g.Record(speculated: true, tokensEmitted: 4, elapsedTicks: Ticks(20));
        }
        // Then the plain baseline: one discarded, three measured.
        for (int i = 0; i < 4; i++)
        {
            Assert.False(g.AllowsSpeculation(), $"calibration step {i}");
            g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
        }
        Assert.True(g.AllowsSpeculation(), "the round should have ended in a win");
        Assert.False(g.IsParked);
        return g;
    }

    [Fact]
    public void AWinKeepsHoldingWhileItsStepsStayCheap()
    {
        var g = WinningRound();
        for (int i = 0; i < 40; i++)
        {
            Assert.True(g.AllowsSpeculation(), $"held step {i}");
            g.Record(speculated: true, tokensEmitted: 3, elapsedTicks: Ticks(20));   // 6.7 ms/token vs 15 plain
        }
        Assert.False(g.IsParked);
    }

    [Fact]
    public void AWinParksAsSoonAsAWindowOfItsStepsLoses()
    {
        var g = WinningRound();
        // The text turned into free prose: 2 tokens per 63 ms step = 31 ms/token.
        for (int i = 0; i < 7; i++)
        {
            Assert.True(g.AllowsSpeculation(), $"window step {i}");
            g.Record(speculated: true, tokensEmitted: 2, elapsedTicks: Ticks(63));
        }
        Assert.False(g.IsParked, "seven losing steps are not yet a verdict");
        g.Record(speculated: true, tokensEmitted: 2, elapsedTicks: Ticks(63));
        Assert.True(g.IsParked, "the eighth losing step parks the hold");
        Assert.False(g.AllowsSpeculation());
        Assert.True(g.SpecMsPerToken > g.PlainMsPerToken * 1.15);
    }

    [Fact]
    public void PlainStepsInsideAHoldAreNotCountedAgainstIt()
    {
        var g = WinningRound();
        // A head that refuses to draft below its gate produces plain steps; they
        // say nothing about speculation's cost and must not trip the window.
        for (int i = 0; i < 30; i++)
            g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
        Assert.False(g.IsParked);
        Assert.True(g.AllowsSpeculation());
    }
}

public class SpeculationCostGovernorNewSequenceTests
{
    private static long Ticks(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    [Fact]
    public void ANewRequestCapsAnInheritedParkAtTheFirstInterval()
    {
        var g = new SpeculationCostGovernor { Enabled = true };
        g.Reset();
        // Two losing rounds in a row: the second park is 64 steps.
        for (int round = 0; round < 2; round++)
        {
            while (g.AllowsSpeculation())
                g.Record(speculated: true, tokensEmitted: 1, elapsedTicks: Ticks(60));
            while (!g.AllowsSpeculation() && !g.IsParked)
                g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
            Assert.True(g.IsParked, $"round {round} should end parked");
            if (round == 0)
                for (int i = 0; i < 32; i++) g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
        }
        // Parked for 64; a new request shortens that to 32 and forgets the backoff.
        g.NewSequence();
        for (int i = 0; i < 32; i++)
        {
            Assert.False(g.AllowsSpeculation(), $"parked step {i}");
            g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
        }
        Assert.True(g.AllowsSpeculation(), "the new turn re-probes after thirty-two steps");
        // That re-probe loses too: the park restarts at the first interval, not at 128.
        while (g.AllowsSpeculation())
            g.Record(speculated: true, tokensEmitted: 1, elapsedTicks: Ticks(60));
        while (!g.AllowsSpeculation() && !g.IsParked)
            g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15));
        int parked = 0;
        while (!g.AllowsSpeculation()) { g.Record(speculated: false, tokensEmitted: 1, elapsedTicks: Ticks(15)); parked++; }
        Assert.Equal(64, parked);
        Assert.Equal(3, g.Losses);
        Assert.Equal(0, g.Wins);
    }
}

public class SpeculationCostGovernorZeroAcceptTests
{
    private static long Ticks(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    [Fact]
    public void AProbeThatAcceptsNothingEndsAfterFourSteps()
    {
        var g = new SpeculationCostGovernor { Enabled = true };
        g.Reset();
        // One discarded first sample, then four measured one-token speculative steps.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(g.AllowsSpeculation(), $"probe step {i}");
            g.Record(speculated: true, tokensEmitted: 1, elapsedTicks: Ticks(45));
        }
        Assert.True(g.IsParked, "four measured zero-acceptance steps are a verdict");
        Assert.Equal(1, g.Losses);
    }

    [Fact]
    public void AProbeThatAcceptsSomethingRunsItsFullLength()
    {
        var g = new SpeculationCostGovernor { Enabled = true };
        g.Reset();
        for (int i = 0; i < 5; i++)
            g.Record(speculated: true, tokensEmitted: i == 2 ? 2 : 1, elapsedTicks: Ticks(45));
        Assert.False(g.IsParked);
        Assert.True(g.AllowsSpeculation(), "the probe is still measuring");
    }
}
