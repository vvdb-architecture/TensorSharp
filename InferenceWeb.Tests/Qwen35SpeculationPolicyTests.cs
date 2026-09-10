// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.

using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Models;

namespace InferenceWeb.Tests;

public sealed class Qwen35SpeculationPolicyTests
{
    [Theory]
    [InlineData(2, 0)]  // one small IQ4 matrix must not retune a mostly-Q8 trunk
    [InlineData(4, 12)] // equal byte shares meet the predominant-IQ4 threshold
    [InlineData(8, 12)]
    public void NGramDefault_RequiresPredominantlyIq4MatrixBytes(int iq4Rows, int expected)
    {
        using var iq4 = new QuantizedWeight(new byte[136 * iq4Rows], (int)GgmlTensorType.IQ4_XS, 256, iq4Rows);
        using var q8 = new QuantizedWeight(new byte[544], (int)GgmlTensorType.Q8_0, 256, 2);
        var quant = new Dictionary<string, QuantizedWeight>
        {
            ["blk.0.ffn_down.weight"] = iq4, ["blk.0.attn_output.weight"] = q8,
        };
        Assert.Equal(expected, Qwen35Model.SelectMetalNGramDraftWindow(quant, new Dictionary<string, Tensor>(), 2, false));
    }

    [Theory]
    [InlineData("blk.0.attn_output.weight", false, 0)]
    [InlineData("output.weight", false, 0)]
    [InlineData("output.weight", true, 12)]
    [InlineData("token_embd.weight", true, 0)]
    [InlineData("token_embd.weight", false, 12)]
    [InlineData("blk.2.ffn_down.weight", false, 12)]
    [InlineData("blk.0.nextn.eh_proj.weight", false, 12)]
    [InlineData("dflash.blk.0.ffn_down.weight", false, 12)]
    [InlineData("blk.0.ssm_in_proj.weight", false, 12)]
    [InlineData("blk.0.ssm_conv1d.weight", false, 12)]
    public void NGramDefault_CountsActiveF32MatricesAndExcludesDraftOrUnusedPacks(string name, bool tied, int expected)
    {
        var allocator = new CpuAllocator(BlasEnum.DotNet);
        using var f32 = new Tensor(allocator, DType.Float32, 16, 16);
        using var iq4 = new QuantizedWeight(new byte[272], (int)GgmlTensorType.IQ4_XS, 256, 2);
        var quant = new Dictionary<string, QuantizedWeight> { ["blk.0.ffn_down.weight"] = iq4 };
        var floats = new Dictionary<string, Tensor> { [name] = f32 };
        Assert.Equal(expected, Qwen35Model.SelectMetalNGramDraftWindow(quant, floats, 2, tied));
    }

    [Theory]
    [InlineData("ffn_gate_up.weight", "ffn_gate.weight", "ffn_up.weight")]
    [InlineData("attn_qkv.weight", "attn_q.weight", "attn_k.weight")]
    public void NGramDefault_DoesNotCountRetainedProjectionSourcesTwice(string fused, string first, string second)
    {
        var allocator = new CpuAllocator(BlasEnum.DotNet);
        using var f32 = new Tensor(allocator, DType.Float32, 16, 16);
        using var iq4 = new QuantizedWeight(new byte[544], (int)GgmlTensorType.IQ4_XS, 256, 4);
        var quant = new Dictionary<string, QuantizedWeight> { ["blk.0." + fused] = iq4 };
        var floats = new Dictionary<string, Tensor>
        {
            ["blk.0." + first] = f32, ["blk.0." + second] = f32, ["blk.0." + fused] = f32,
        };
        Assert.Equal(12, Qwen35Model.SelectMetalNGramDraftWindow(quant, floats, 2, false));
    }
}
