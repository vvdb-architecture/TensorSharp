using TensorSharp.GGML;
using TensorSharp.Models;
using TensorSharp.Models.Architecture;

namespace InferenceWeb.Tests;

/// <summary>
/// Small contracts needed by the published Bonsai GGUFs. These deliberately avoid
/// loading a checkpoint: the 8B file is enough to expose all three integration seams
/// (the Q1_0 row layout, the qwen3 model factory, and the ChatML response protocol).
/// </summary>
public sealed class BonsaiCompatibilityTests
{
    [Fact]
    public void Q10_UsesTheUpstreamGgmlTypeAndRowLayout()
    {
        // GGML_TYPE_Q1_0 is part of the file format. Renumbering it or treating it as
        // a 256-value K-quant makes GGUF offsets wrong for every following tensor.
        Assert.Equal(41u, (uint)GgmlTensorType.Q1_0);
        Assert.Equal(128, GgufFile.GetBlockSize(GgmlTensorType.Q1_0));
        Assert.Equal(18, GgufFile.GetTypeSize(GgmlTensorType.Q1_0));

        Assert.Equal(18, ManagedQuantizedOps.RowSize((int)GgmlTensorType.Q1_0, 128));
        Assert.Equal(36, GgmlGgufTensorDequant.GetRowSizeBytes((int)GgmlTensorType.Q1_0, 256));
    }

    [Fact]
    public void ManagedQ10_MatchesNativeGgmlForAHandBuiltBlock()
    {
        const int valuesPerBlock = 128;
        const float scale = 0.75f;
        byte[] block = new byte[18];

        ushort scaleBits = BitConverter.HalfToUInt16Bits((Half)scale);
        block[0] = (byte)scaleBits;
        block[1] = (byte)(scaleBits >> 8);

        // Q1_0 stores one sign bit per value, least-significant bit first. Use a
        // deliberately asymmetric pattern so byte order and bit order are both tested.
        block[2] = 0b_1001_0110;
        for (int i = 1; i < 16; i++)
            block[2 + i] = (byte)(0xA5 ^ (i * 29));

        float[] managed = new float[valuesPerBlock];
        ManagedQuantizedOps.DequantizeToFloat32(
            (int)GgmlTensorType.Q1_0, block, 0, managed, 0, valuesPerBlock);

        float[] native = new float[valuesPerBlock];
        GgmlGgufTensorDequant.DequantizeToFloat32(
            (int)GgmlTensorType.Q1_0, block, 0, native, 0, valuesPerBlock);

        Assert.Equal(-scale, managed[0]);
        Assert.Equal(scale, managed[1]);
        Assert.Equal(scale, managed[2]);
        Assert.Equal(-scale, managed[3]);
        Assert.Equal(scale, managed[7]);
        Assert.Equal(native, managed);
    }

    [Fact]
    public void Qwen3_IsRoutedToTheRestoredModelPlugin()
    {
        Assert.True(ModelArchitectureRegistry.TryGet("qwen3", out var qwen3));
        Assert.Equal("qwen3", qwen3.Id);
        Assert.Contains("qwen3", qwen3.Aliases, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(qwen3.Factory);

        // Bonsai-27B declares qwen35 and must continue to use the hybrid GDN plugin;
        // registering qwen3 must not steal the neighbouring architecture.
        Assert.True(ModelArchitectureRegistry.TryGet("qwen35", out var qwen35));
        Assert.Equal("qwen35", qwen35.Id);
        Assert.NotSame(qwen3, qwen35);
    }

    [Fact]
    public void Qwen3_UsesTheThinkingAndToolAwareChatMlProtocol()
    {
        ChatProtocol? protocol = ChatProtocolRegistry.For("qwen3");
        Assert.NotNull(protocol);
        Assert.Equal("qwen3", protocol.Id);

        IOutputParser parser = OutputParserFactory.Create("qwen3");
        Assert.IsType<ChatMlOutputParser>(parser);
        Assert.True(parser.HasThinkingSupport);
        Assert.True(parser.HasToolSupport);

        Assert.Equal("<think>\n\n</think>\n\n",
            KVCachePromptRenderer.GetAssistantGenerationSuffix("qwen3", enableThinking: false));
        Assert.Equal("<think>\n",
            KVCachePromptRenderer.GetAssistantGenerationSuffix("qwen3", enableThinking: true));
        Assert.True(protocol.EmitsEmptyThinkBlockForPastTurns!(false));
        Assert.Equal(ToolCallRawSplicing.Always, protocol.ToolCallRawSplicing);
    }

    [Fact]
    public void QwenFimControls_AreEndOfGenerationLikeLlamaCpp()
    {
        string[] vocabulary =
        [
            "ordinary", "<|im_end|>", "<|fim_pad|>", "<|repo_name|>", "<|file_sep|>",
        ];

        Assert.Equal([1, 2, 3, 4], ModelBase.ResolveEogTokenIds(vocabulary, eosId: 1));
    }
}
