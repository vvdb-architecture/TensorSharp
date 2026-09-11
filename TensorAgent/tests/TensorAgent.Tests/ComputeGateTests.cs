// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorSharp.Runtime.Scheduling;

namespace TensorAgent.Tests;

/// <summary>
/// The gate that stops the model running while the app is not in front of the user.
///
/// <para>
/// It exists because of an iOS rule with no exceptions — an app that is not frontmost
/// may not submit GPU work — and because of what ggml-metal does when that rule is
/// broken: it sets a sticky error flag whose own comment says the backend has to be
/// recreated to clear it. So a single command buffer committed a moment after the user
/// swipes away does not cost a token, it costs the model for the rest of the process.
/// </para>
/// <para>
/// None of that can be observed from a terminal, which is exactly why the gate is a
/// platform-neutral object with the iOS notifications on the outside of it: what CAN be
/// checked here is that closing it stops work, that opening it releases everything
/// waiting — the engine's synchronous step loop and the host's async stream both — and
/// that nothing is lost in between.
/// </para>
/// </summary>
public sealed class ComputeGateTests
{
    [Fact]
    public void AnOpenGateCostsNothingOnThePathOfEveryToken()
    {
        var gate = new ComputeGate();

        Assert.True(gate.IsOpen);
        // Reference equality with the completed singleton: this is consulted once per
        // token, so it must not allocate a task or yield in the ordinary case.
        Assert.Same(Task.CompletedTask, gate.WaitAsync());
        Assert.Same(Task.CompletedTask, gate.WaitAsync(CancellationToken.None));
        // And the blocking form returns at once.
        gate.Wait();
    }

    [Fact]
    public async Task ClosingTheGateStopsTheNextStepAndOpeningItReleasesIt()
    {
        var gate = new ComputeGate();
        gate.Close();
        Assert.False(gate.IsOpen);

        Task waiting = gate.WaitAsync();
        Assert.False(waiting.IsCompleted, "a closed gate must hold the generation");

        gate.Open();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public async Task TheBlockingWaitParksAThreadAndOpeningReleasesIt()
    {
        // The engine's worker is a plain thread with no async context; it parks here
        // between two steps. What must hold: it does not return while closed, it does
        // return when opened, and it returns on ITS thread rather than the opener's.
        var gate = new ComputeGate();
        gate.Close();

        var released = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            gate.Wait();
            released.SetResult(Environment.CurrentManagedThreadId);
        });
        worker.Start();

        await Task.Delay(100);
        Assert.False(released.Task.IsCompleted, "the worker must stay parked while the gate is closed");

        int opener = Environment.CurrentManagedThreadId;
        gate.Open();
        int resumedOn = await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(opener, resumedOn);
        Assert.Equal(worker.ManagedThreadId, resumedOn);
    }

    [Fact]
    public void TheBlockingWaitLeavesOnCancellationWithTheEnginesOwnExceptionType()
    {
        // Shutdown cancels the engine's token while the loop may be parked here. The
        // loop catches OperationCanceledException; an AggregateException wrapping one
        // would escape it and take the worker thread down with an unhandled exception.
        var gate = new ComputeGate();
        gate.Close();
        using var cancel = new CancellationTokenSource(50);

        Assert.ThrowsAny<OperationCanceledException>(() => gate.Wait(cancel.Token));
    }

    [Fact]
    public async Task EveryWaiterIsReleasedByOneOpen()
    {
        // Several turns can be parked at once: the manager runs one generation per
        // conversation and the user can have more than one going.
        var gate = new ComputeGate();
        gate.Close();
        Task[] waiting = Enumerable.Range(0, 16).Select(_ => gate.WaitAsync()).ToArray();
        Assert.All(waiting, w => Assert.False(w.IsCompleted));

        gate.Open();
        await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClosingAndOpeningAreBothIdempotent()
    {
        // The notifications they are driven by are not guaranteed to alternate: iOS
        // sends willResignActive for a Control Centre pull that never becomes a
        // background transition, and didBecomeActive can arrive without a matching
        // resign at launch.
        var gate = new ComputeGate();
        gate.Open();
        gate.Open();
        Assert.True(gate.IsOpen);

        gate.Close();
        gate.Close();
        Assert.False(gate.IsOpen);
        Assert.Equal(1, gate.Closures);

        Task waiting = gate.WaitAsync();
        gate.Open();
        gate.Open();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ACancelledTurnLeavesTheGateRatherThanHangingOnIt()
    {
        // Shutdown and Stop both cancel; a turn parked on a closed gate has to come out
        // of it, or AgentAppHost.Dispose waits forever for an engine that will never
        // report itself idle.
        var gate = new ComputeGate();
        gate.Close();
        using var cancel = new CancellationTokenSource();

        Task waiting = gate.WaitAsync(cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ClosuresCountsRatherThanFlagsSoATurnCanAskWhetherItWasEverAway()
    {
        // A flag cannot answer the question that matters after a failure. By the time a
        // failed turn is handled the app is always in front again, so "is the gate open
        // now" is always true; what decides whether the GPU is to blame is whether it
        // was ever CLOSED while that turn was running.
        var gate = new ComputeGate();
        long before = gate.Closures;

        gate.Close();
        gate.Open();
        gate.Close();
        gate.Open();

        Assert.Equal(before + 2, gate.Closures);
    }

    [Fact]
    public async Task TheGateReportsHowLongItHeldThingsUp()
    {
        var gate = new ComputeGate();
        gate.Close();
        await Task.Delay(60);
        Assert.True(gate.TotalClosed > TimeSpan.Zero, "time spent closed must be visible while still closed");

        gate.Open();
        TimeSpan held = gate.TotalClosed;
        Assert.True(held >= TimeSpan.FromMilliseconds(40), $"reported {held.TotalMilliseconds:0} ms");

        // And it stops accruing once the app is back.
        await Task.Delay(60);
        Assert.Equal(held, gate.TotalClosed);
    }

    [Fact]
    public void ChangedSaysWhichWayItWentAndOnlyOnARealChange()
    {
        var gate = new ComputeGate();
        var seen = new List<bool>();
        gate.Changed += open => seen.Add(open);

        gate.Open();    // already open: nothing to say
        gate.Close();
        gate.Close();   // already closed: nothing to say
        gate.Open();

        Assert.Equal(new[] { false, true }, seen);
    }

    [Fact]
    public async Task OpeningDoesNotRunTheWaitersOnTheThreadThatOpened()
    {
        // Open() is called from a UIKit lifecycle notification, on the main thread. A
        // waiter resumed inline there would run a decode step on the UI thread.
        var gate = new ComputeGate();
        gate.Close();

        int openingThread = Environment.CurrentManagedThreadId;
        int resumedOn = 0;
        Task waiting = gate.WaitAsync().ContinueWith(
            _ => resumedOn = Environment.CurrentManagedThreadId,
            TaskContinuationOptions.ExecuteSynchronously);

        gate.Open();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(openingThread, resumedOn);
    }

    [Fact]
    public async Task AGatedStreamStopsPullingAndResumesWhereItStopped()
    {
        // The shape AgentAppHost.GatedChatFrames uses, on a stand-in for the generation:
        // a caller that waits before each pull neither skips nor repeats a frame across
        // a pause.
        var gate = new ComputeGate();
        int pulled = 0;

        async IAsyncEnumerable<int> Source()
        {
            for (int i = 0; i < 100; i++)
            {
                pulled++;
                yield return i;
                await Task.Yield();
            }
        }

        async IAsyncEnumerable<int> Gated()
        {
            await using IAsyncEnumerator<int> source = Source().GetAsyncEnumerator();
            while (true)
            {
                await gate.WaitAsync();
                if (!await source.MoveNextAsync())
                    yield break;
                yield return source.Current;
            }
        }

        var taken = new List<int>();
        IAsyncEnumerator<int> frames = Gated().GetAsyncEnumerator();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await frames.MoveNextAsync());
            taken.Add(frames.Current);
        }

        gate.Close();
        int pulledWhenClosed = pulled;
        ValueTask<bool> blocked = frames.MoveNextAsync();
        await Task.Delay(80);
        Assert.False(blocked.IsCompleted, "the source must not be pulled while the gate is closed");
        Assert.Equal(pulledWhenClosed, pulled);

        gate.Open();
        Assert.True(await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        taken.Add(frames.Current);

        // Resumed exactly where it stopped: nothing skipped, nothing repeated.
        Assert.Equal(new[] { 0, 1, 2, 3 }, taken);
        await frames.DisposeAsync();
    }
}
