# Managed V4.1 vision checks

The committed `InferenceWeb.Tests/Fixtures/DeepSeek41Vision` PNGs and manifest compare every normalized BF16 patch value against the pinned official Python processor (Pillow 11.3.0). All six full-buffer hashes match, including alpha discard, padding, downsampling and patch order.

`report.json` records eight managed P/Invoke calls through the independent small F32/BF16 native vision fixtures. Maximum absolute error was 1.02e-6 for F32 and 0.00390625 for BF16. The fixtures have two vision layers, vision width 128 and text width 256; they establish numerical/ABI behavior, not full-model image quality or performance. `Program.cs` records the probe; the fixture generators live under `eng/tests`.

The final managed correctness lane passed 3,400/3,400 tests locally and on the requested VM, with no skips, after image/video integration, JSON final-response handling, delayed thinking grammar, audio rejection and GPU-selection validation. It initially caught a missing iOS native-symbol retention update; the manifest now includes all six vision exports, and its audit includes `.inc` implementations. The [managed correctness artifacts](../managed-correctness/README.md) retain the exact filter and remote result.
