using TensorAgent.Core.Hosting;
using TensorSharp.Runtime.Scheduling;

namespace TensorAgent.Tests;

/// <summary>
/// The file that makes the first message of a launch fast has to be exactly the
/// checkpoint it claims to be, or nothing: a stale or half-written one restored into
/// the engine would be a subtly different model for every chat that clones it.
/// </summary>
public sealed class PrefixCheckpointFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tensoragent-ckpt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly int[] Prefix = Enumerable.Range(1, 500).Select(i => i * 7).ToArray();
    private const string Model = "qwen35|L=32|dtype=q4_0|prefix-checkpoint-v1";

    [Fact]
    public void SavedBytesComeBackForTheSameModelAndTokensOnly()
    {
        var store = new PrefixCheckpointFileStore(_dir);
        byte[] payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();

        Assert.True(store.Save(Model, Prefix, s => s.Write(payload)));
        Assert.True(File.Exists(store.PathFor(Model, Prefix)));

        Assert.True(store.TryOpen(Model, Prefix, out Stream opened));
        using (opened)
        {
            var back = new byte[payload.Length];
            opened.ReadExactly(back);
            Assert.Equal(payload, back);
            Assert.Equal(-1, opened.ReadByte());   // nothing after the payload
        }

        // Another prefix, another model, other WEIGHTS of the same shape: absent, not
        // "close enough" -- a checkpoint is the state of one file's weights.
        int[] other = (int[])Prefix.Clone();
        other[^1]++;
        Assert.False(store.TryOpen(Model, other, out _));
        Assert.False(store.TryOpen(Model + "|different", Prefix, out _));
        var otherWeights = new PrefixCheckpointFileStore(_dir, weightsIdentity: "Qwen3.5-9B-Q4_K_XL.gguf|123|456;");
        Assert.False(otherWeights.TryOpen(Model, Prefix, out _));
    }

    [Fact]
    public void TheWeightsIdentityNamesEveryFileTheModelWasLoadedFrom()
    {
        Directory.CreateDirectory(_dir);
        string weights = Path.Combine(_dir, "model.gguf");
        File.WriteAllBytes(weights, new byte[1234]);
        string before = PrefixCheckpointFileStore.WeightsIdentityOf(weights, null);
        Assert.Contains("model.gguf|1234|", before);

        // A re-download of a different file under the same name is a different identity.
        File.WriteAllBytes(weights, new byte[1235]);
        Assert.NotEqual(before, PrefixCheckpointFileStore.WeightsIdentityOf(weights));
    }

    [Fact]
    public void OrphanedModelDirectoriesAreSweptAndCatalogOnesKept()
    {
        string root = Path.Combine(_dir, "prefix-cache");
        var kept = new PrefixCheckpointFileStore(Path.Combine(root, "kept-model"));
        var gone = new PrefixCheckpointFileStore(Path.Combine(root, "gone-model"));
        Assert.True(kept.Save(Model, Prefix, s => s.Write(new byte[100])));
        Assert.True(gone.Save(Model, Prefix, s => s.Write(new byte[100])));

        long freed = PrefixCheckpointFileStore.SweepOrphans(root, id => id == "kept-model");

        Assert.True(freed > 0);
        Assert.True(Directory.Exists(kept.Directory));
        Assert.False(Directory.Exists(gone.Directory));
    }

    [Fact]
    public void AFileThatDoesNotDescribeItsNameIsDeletedAndCountsAsAbsent()
    {
        var store = new PrefixCheckpointFileStore(_dir);
        Directory.CreateDirectory(_dir);
        string path = store.PathFor(Model, Prefix);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });   // not even a header

        Assert.False(store.TryOpen(Model, Prefix, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AWriterThatThrowsLeavesNothingBehind()
    {
        var store = new PrefixCheckpointFileStore(_dir);
        Assert.False(store.Save(Model, Prefix, _ => throw new InvalidOperationException("the model declined")));
        Assert.False(File.Exists(store.PathFor(Model, Prefix)));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>());
    }

    [Fact]
    public void OnlyTheNewestFewCheckpointsAreKept()
    {
        var store = new PrefixCheckpointFileStore(_dir);
        for (int i = 0; i < PrefixCheckpointFileStore.MaxFilesPerModel + 2; i++)
        {
            int[] prefix = (int[])Prefix.Clone();
            prefix[0] = 1000 + i;
            Assert.True(store.Save(Model, prefix, s => s.Write(new byte[64])));
            // The clock on a file is coarse; keep the order unambiguous.
            File.SetLastWriteTimeUtc(store.PathFor(Model, prefix), DateTime.UtcNow.AddMinutes(i));
        }
        Assert.Equal(PrefixCheckpointFileStore.MaxFilesPerModel, Directory.GetFiles(_dir, "*.ckpt").Length);

        // The newest survives, the oldest is gone.
        int[] newest = (int[])Prefix.Clone(); newest[0] = 1000 + PrefixCheckpointFileStore.MaxFilesPerModel + 1;
        int[] oldest = (int[])Prefix.Clone(); oldest[0] = 1000;
        Assert.True(store.TryOpen(Model, newest, out Stream s1)); s1.Dispose();
        Assert.False(store.TryOpen(Model, oldest, out _));
    }

    [Fact]
    public void ClearRemovesTheModelsCheckpoints()
    {
        var store = new PrefixCheckpointFileStore(_dir);
        Assert.True(store.Save(Model, Prefix, s => s.Write(new byte[16])));
        Assert.True(store.TotalBytes() > 0);
        store.Clear();
        Assert.False(Directory.Exists(_dir));
        Assert.Equal(0, store.TotalBytes());
    }
}
