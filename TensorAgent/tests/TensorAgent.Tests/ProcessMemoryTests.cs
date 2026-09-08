using TensorAgent.Core.Hosting;

namespace TensorAgent.Tests;

/// <summary>
/// The probe reads the kernel's own numbers -- the footprint a per-process limit is
/// judged against and the machine's wired total, where Metal's claim on the weights
/// shows up. Both come from the same calls on iOS and macOS, so this is the one place
/// they can be exercised without a phone.
/// </summary>
public sealed class ProcessMemoryTests
{
    [Fact]
    public void OnAppleSystems_TheProbeAnswersWithRealNumbers()
    {
        if (!ProcessMemory.IsSupported)
            return; // Linux/Windows CI: nothing to read, and Read() must simply say so.

        ProcessMemory.Snapshot memory = ProcessMemory.Read();

        // A .NET test host is tens of megabytes at least, and no machine runs with zero
        // wired memory or a footprint above its wired total plus free memory by orders.
        Assert.True(memory.Footprint > 16L * 1024 * 1024, $"footprint {memory.Footprint}");
        Assert.True(memory.SystemWired > 0, $"wired {memory.SystemWired}");
        Assert.True(memory.SystemFree >= 0, $"free {memory.SystemFree}");
        Assert.True(memory.SystemCompressor >= 0, $"compressor {memory.SystemCompressor}");

        string line = memory.ToString();
        Assert.Contains("footprint ", line);
        Assert.Contains("system wired ", line);
    }

    [Fact]
    public void ElsewhereItSaysUnknownRatherThanThrowing()
    {
        ProcessMemory.Snapshot memory = ProcessMemory.Read();
        if (ProcessMemory.IsSupported)
            return;
        Assert.Equal(-1, memory.Footprint);
        Assert.Contains("n/a", memory.ToString());
    }
}
