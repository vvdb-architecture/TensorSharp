// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;
using TensorSharp.GGML;

namespace TensorAgent.Tests;

/// <summary>
/// <see cref="LiveModelFactAttribute"/> with one more condition, in the same shape
/// and for the same reason: these say what is missing instead of passing silently.
///
/// <para>
/// The extra condition is Metal itself. Falling back to the CPU the way
/// <c>EndToEndChatTests</c> does would be worse than not running here — everything
/// these tests are about (device buffers, residency sets, what a teardown gives
/// back) simply does not exist on the CPU backend, so a CPU run would go green
/// while proving nothing. xUnit 2.9 has no way to skip from inside a test body,
/// so the decision has to be made here, at discovery.
/// </para>
/// </summary>
public sealed class MetalModelFactAttribute : FactAttribute
{
    public MetalModelFactAttribute()
    {
        if (EndToEndChatTests.Unavailable(out _, out _) is { } noWeights)
            Skip = noWeights;
        else if (MetalLifetimeTests.MetalUnavailable() is { } noMetal)
            Skip = noMetal;
    }
}

/// <summary>
/// What happens to the GPU's memory when a model goes away.
///
/// <para>
/// Metal is the backend the app uses on a phone, and the phone is where a model is
/// loaded, dropped and replaced constantly — the user switches models, the system
/// asks for memory back, the app is closed. None of that is exercised by a test
/// that loads a model and answers a question, and the failure mode is not a wrong
/// answer: ggml-metal's device is a C++ static whose destructor asserts that every
/// residency set has been handed back, so anything our side forgets to release
/// turns into <c>GGML_ASSERT([rsets-&gt;data count] == 0)</c> and SIGABRT at exit,
/// long after the test that caused it reported success.
/// </para>
/// <para>
/// A process cannot watch its own exit, so these do not wait for the abort. They
/// measure the device allocation directly, before and after, which is both the
/// mechanism behind that assert and the thing a phone actually cares about.
/// </para>
/// </summary>
[Collection(LiveModelCollection.Name)]
public sealed class MetalLifetimeTests : IDisposable
{
    /// <summary>
    /// How much the device may still be holding after everything has been released.
    ///
    /// <para>
    /// It measures zero on a development Mac, and the assertion could say so — but
    /// zero is not something ggml-metal promises. The backend keeps a compiled
    /// kernel library, a command queue and a probe allocation that a model teardown
    /// is not supposed to free, and whether any of that lands in the device's
    /// allocated size is ggml's business and changes when ggml changes. So this is
    /// headroom rather than a measurement. It can afford to be generous: the leak
    /// these were written for was a gigabyte of reuse gallocr plus per-graph compute
    /// buffer, an order of magnitude clear of the budget either way.
    /// </para>
    /// </summary>
    private const long LeftoverBudgetBytes = 128L * 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-metal-" + Guid.NewGuid().ToString("N"));
    private AgentAppHost? _host;
    private HttpClient? _client;

    /// <summary>
    /// Establish the floor these tests measure against.
    ///
    /// <para>
    /// The device allocation is per process, not per test, and the leak these were
    /// written for is a fixed-size buffer rather than one that grows per model — so
    /// an earlier test in the same process that left it behind would raise the
    /// "idle" reading by exactly the amount being looked for, and the assertion
    /// would go quiet on the very bug it exists to catch. Handing the graph scratch
    /// back here makes each test start from the same floor whatever ran before it.
    /// This is deliberately not the thing under test: what is asserted below is that
    /// disposing a model gets back to this floor on its own.
    /// </para>
    /// </summary>
    public MetalLifetimeTests()
    {
        if (MetalUnavailable() is null)
            GgmlBasicOps.ReleaseReuseComputeBuffers();
    }

    public void Dispose()
    {
        ReleaseHost();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Why Metal cannot be exercised here, or null when it can.</summary>
    internal static string? MetalUnavailable()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            return "the ggml_metal backend exists only on Apple platforms; these need a Mac";

        try
        {
            if (!GgmlBasicOps.CanInitializeBackend(GgmlBackendType.Metal))
                return "this build of GgmlOps has no ggml_metal; rebuild it with TensorSharp.GGML.Native/build-macos.sh";
        }
        catch (Exception ex)
        {
            return $"the native GGML library could not be loaded ({ex.GetType().Name}: {ex.Message}); "
                + "build it with TensorSharp.GGML.Native/build-macos.sh";
        }

        return null;
    }

    /// <summary>
    /// Bytes the Metal device has allocated for this process, as ggml reports them.
    ///
    /// <para>
    /// <c>MTLDevice.currentAllocatedSize</c> counts this process's allocations only,
    /// so the number does not move because something else on the machine is busy —
    /// which is what makes a before/after comparison worth asserting on.
    /// </para>
    /// </summary>
    private static long DeviceAllocatedBytes()
    {
        Assert.True(GgmlBasicOps.TryGetBackendMemory(out long free, out long total),
            "the GGML backend would not report its device memory, so nothing here can be measured");
        return total - free;
    }

    [MetalModelFact]
    public async Task UnloadingOnMetalHandsBackEveryDeviceBufferTheModelTook()
    {
        Assert.Null(EndToEndChatTests.Unavailable(out CatalogModel model, out string weights));

        // Before anything is loaded, but with the backend already up: this is the
        // floor a clean unload has to come back to.
        long idle = DeviceAllocatedBytes();

        Start(model, weights);
        await LoadOnMetalAsync(model);

        // Generate, rather than only loading. The buffers that were being leaked are
        // graph scratch, and a graph is only built once something is computed — a
        // load-and-unload test would have passed throughout the bug.
        Assert.False(string.IsNullOrWhiteSpace(await AnswerAsync("Reply with exactly the word: pineapple")),
            "the model produced no text, so no graph was built and nothing was measured");

        long busy = DeviceAllocatedBytes();
        Assert.True(busy > idle, "loading and generating allocated nothing on the device; this did not run on Metal");

        ReleaseHost();

        long afterUnload = DeviceAllocatedBytes();
        long leftover = afterUnload - idle;
        Console.WriteLine($"metal: idle {Mib(idle)} MiB, loaded {Mib(busy)} MiB, after unload {Mib(afterUnload)} MiB "
            + $"(leftover {Mib(leftover)} MiB)");

        Assert.True(leftover < LeftoverBudgetBytes,
            $"unloading the model left {Mib(leftover)} MiB allocated on the Metal device (budget {Mib(LeftoverBudgetBytes)} MiB). "
            + "Every one of those buffers is still registered in the device's residency set, and the ggml-metal device "
            + "destructor asserts that set is empty, so this process will abort on exit.");
    }

    [MetalModelFact]
    public async Task SwitchingModelsOnMetalLoadsUnloadsAndLoadsAgain()
    {
        Assert.Null(EndToEndChatTests.Unavailable(out CatalogModel model, out string weights));

        long idle = DeviceAllocatedBytes();
        Start(model, weights);

        await LoadOnMetalAsync(model);
        Assert.False(string.IsNullOrWhiteSpace(await AnswerAsync("Reply with exactly the word: pineapple")),
            "the first load answered nothing");
        long afterFirst = DeviceAllocatedBytes();

        // What the app does when the user picks a different model: the same route,
        // which unloads what is loaded and loads the replacement. Using the same
        // file keeps this runnable on a machine with one model in it; the code path
        // does not know or care that the bytes are the same.
        await LoadOnMetalAsync(model);
        Assert.False(string.IsNullOrWhiteSpace(await AnswerAsync("Reply with exactly the word: pineapple")),
            "the model stopped answering after being switched");
        long afterSecond = DeviceAllocatedBytes();

        long growth = afterSecond - afterFirst;
        Console.WriteLine($"metal: after first load {Mib(afterFirst)} MiB, after reload {Mib(afterSecond)} MiB "
            + $"(growth {Mib(growth)} MiB)");

        // A switch that does not release the outgoing model keeps its buffers wired
        // for the three minutes ggml-metal's heartbeat thread requests residency,
        // on a device that has to fit the incoming model at the same time.
        Assert.True(growth < LeftoverBudgetBytes,
            $"switching models grew the Metal device allocation by {Mib(growth)} MiB (budget {Mib(LeftoverBudgetBytes)} MiB); "
            + "the outgoing model's buffers were not released before the incoming one was loaded");

        // And then the app is closed, which is the half of the cycle that aborts.
        // Reaching the idle floor from HERE — after a switch rather than after a
        // single load — is the assertion, because a teardown that only works on a
        // model that was loaded once is not the one a phone performs.
        ReleaseHost();
        long leftover = DeviceAllocatedBytes() - idle;
        Console.WriteLine($"metal: after closing {Mib(leftover)} MiB above idle");
        Assert.True(leftover < LeftoverBudgetBytes,
            $"closing after a model switch left {Mib(leftover)} MiB allocated on the Metal device "
            + $"(budget {Mib(LeftoverBudgetBytes)} MiB); those buffers are still in the device's residency set and "
            + "the ggml-metal device destructor asserts it is empty, so this process will abort on exit");
    }

    private static long Mib(long bytes) => bytes / (1024 * 1024);

    private void Start(CatalogModel model, string weights)
    {
        var paths = new AgentPaths(Path.Combine(_root, "data"), Path.Combine(_root, "cache")) { DeviceMemoryGB = 16 };
        paths.EnsureCreated();

        // Link, do not copy: these files are gigabytes.
        string target = Path.Combine(paths.ModelsDirectory, model.Id);
        Directory.CreateDirectory(target);
        File.CreateSymbolicLink(Path.Combine(target, model.Weights.FileName), weights);

        var settings = new SettingsStore(paths.SettingsFile);
        AppSettings chosen = settings.Load();
        chosen.SelectedModelId = model.Id;
        chosen.MaxTokens = 32;
        settings.Save(chosen);

        _host = new AgentAppHost(paths);
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri(_host.Server.BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.Add("Cookie", $"{LoopbackServer.TokenCookie}={_host.Server.Token}");
    }

    /// <summary>
    /// Load on Metal and nowhere else. <c>EndToEndChatTests</c> falls back to the CPU
    /// because the backend is not what it is testing; here it is the only thing being
    /// tested, so a Metal load that fails is a failure and says what the route said.
    /// </summary>
    private async Task LoadOnMetalAsync(CatalogModel model)
    {
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/api/models/load", new
        {
            model = model.Weights.FileName,
            backend = "ggml_metal",
        });
        string payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode && payload.Contains("\"ok\":true", StringComparison.Ordinal),
            $"loading on ggml_metal failed: {(int)response.StatusCode} {payload}");
    }

    /// <summary>One turn through the page's own route, returned as the text it renders.</summary>
    private async Task<string> AnswerAsync(string prompt)
    {
        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                sessionId = session.GetProperty("sessionId").GetString(),
                messages = new[] { new { role = "user", content = prompt } },
                maxTokens = 32,
                think = false,
            }), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode, $"chat failed: {(int)response.StatusCode}");

        var text = new StringBuilder();
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            JsonElement frame = JsonSerializer.Deserialize<JsonElement>(line[6..]);
            if (frame.TryGetProperty("token", out JsonElement token) && token.GetString() is { } piece)
                text.Append(piece);
            else if (frame.TryGetProperty("replace", out JsonElement replace) && replace.GetString() is { } whole)
                text.Clear().Append(whole);
        }
        return text.ToString();
    }

    /// <summary>
    /// Tear the host down and let go of it, so the measurement that follows sees the
    /// unload and the class's own <see cref="Dispose"/> does not do it a second time.
    /// </summary>
    private void ReleaseHost()
    {
        _client?.Dispose();
        _client = null;
        _host?.Dispose();
        _host = null;
    }
}
