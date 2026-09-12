The published Q2_K checkpoint uses Q2_K routed gate/up projections and Q3_K
routed down projections. The test now retains each projection’s type separately
and compares unsharded and partitioned weights with top-6 routing among eight
synthetic experts. Production TP code and native library b26cac3e were unchanged.

| Execution | Result | Maximum relative L2 |
|---|---|---:|
| CPU standard geometry, F32/BF16/Q2_K/mixed formats | 96/96 strict passes | Mixed: 1.08e-7 |
| CPU mixed, checkpoint dimensions 5120×2304 | 24/24 strict passes | 1.10e-7 |
| CUDA standard geometry, 2/4/8 GPUs | 32/32 strict passes per degree | See per-comparison artifact |
| CUDA mixed checkpoint dimensions, 2 GPUs | Four nt1/5 passes; first nt16 comparison fails | 3.89e-5 |
| CUDA mixed checkpoint dimensions, 4 GPUs | Four nt1/5 passes; first nt16 comparison fails | 3.60e-5 |
| CUDA mixed checkpoint dimensions, 8 GPUs | Four nt1/5 passes; first nt16 comparison fails | 4.06e-5 |

The unchanged relative threshold is 1e-5. Each failed full-dimension run stops
at its first failure; its remaining three comparisons were not executed.
Absolute errors at failure were 3.56e-8–3.94e-8. The failures remain recorded;
these runs do not establish strict full-dimension CUDA parity.

A bounded diagnostic reproduced the same error with graph fusion disabled and
with all strips executed independently on one GPU. That independently assembled
result was **bitwise identical** to the TP result, excluding transfer, partition
upload, and cross-device reduction as the source of this observed difference.
The column-strip gate, up, and SwiGLU outputs differed from their unsharded
counterparts by relative L2 2.27e-7, 2.11e-7, and 3.08e-7, respectively. The
Q3_K down projection increased the output difference to 3.89e-5.

An F32-down control passed all eight two-GPU comparisons, with maximum relative
L2 4.16e-7. This control changes down storage precision and its alignment quantum;
it supports the involvement of quantized matrix multiplication without proving
a specific quantization-bin mechanism. A host estimate found no changed Q8
integer bins. No tolerance was widened and no production kernel was changed.

Reproduce with the native test executable:

```sh
GgmlOpsDsv41TpTest --cuda 2 --checkpoint-shape
GGML_CUDA_DISABLE_FUSION=1 GgmlOpsDsv41TpTest --cuda 2 --checkpoint-shape --diagnose
GGML_CUDA_DISABLE_FUSION=1 GgmlOpsDsv41TpTest --cuda 2 --checkpoint-shape --diagnostic-down-f32
```

Use `--cuda 4` or `--cuda 8` for the other degrees; omit `--cuda` for CPU.
Commands, source and binary hashes, original failures, diagnostic logs and every
measured comparison are preserved in
[tp-mixed-quantization-local.json](tp-mixed-quantization-local.json) and
[tp-mixed-quantization-cuda.json](tp-mixed-quantization-cuda.json).
