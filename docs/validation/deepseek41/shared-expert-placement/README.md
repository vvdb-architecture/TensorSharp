# V4.1 shared-expert backend placement

Native candidate `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014` explicitly assigns V4.1 shared gate, up and down projections to the layer device. The shared expert stays on that device when routed experts use CPU offload or tensor parallelism. CPU-only layer devices remain CPU. Other architectures and global weight-buffer policies are unchanged.

The former scheduler placement put shared gate/up projections on CPU after a CPU-routed branch. Ordinary GPU weight buffers have usage `ANY`, so they do not activate the scheduler's weight-owner preference. The routed branch precedes the shared branch in graph traversal. The fused SwiGLU backend handles CUSTOM nodes but cannot propagate a GPU assignment into the preceding ordinary GEMMs; subsequent CPU assignment expansion therefore claims those GEMMs. The shared down projection already returned to the GPU.

The test inspects actual allocated scheduler assignments, comparing backend objects with each layer's expected device. Its test-only legacy switch disables just the new pins in the same binary. In the five-layer CUDA fixture, the legacy control reproduced these counts at each tested shape:

| Configuration | Shared gate on wrong CPU | Shared up on wrong CPU | Shared down on wrong CPU |
|---|---:|---:|---:|
| Resident layers | 0 | 0 | 0 |
| CPU-MoE: one routed layer | 1 | 1 | 0 |
| Routed TP: two GPUs | 5 | 5 | 0 |
| Routed TP plus one CPU-MoE layer | 5 | 5 | 0 |

All fixed configurations had zero misplaced shared projections. Prefill/decode shapes of three, one and four tokens were checked, including both layer GPUs. Every mode also matched an independent PyTorch oracle at `atol=rtol=2e-5`; the largest measured relative L2 error was below `9.1e-7`. The legacy failures are preserved as explicit backend observations in [native-shared-placement-cuda2.json](native-shared-placement-cuda2.json), while the regression assertions verify successful reproduction of those failures.

| Check | Result |
|---|---:|
| Local CPU placement/oracle checks | 384/384 |
| CUDA2 placement/oracle checks | 597/597 |
| Local text/image slot recovery | 158/158 |
| CUDA2 text/image TP slot recovery | 230/230 |
| Native CTests | 13/13 |

The recovery suite retains injected partial-graph, post-mutation and post-compute failures, healthy-slot continuation and reset-to-oracle behavior. The placement observer and legacy switch exist only in test-enabled builds; production exports remain unchanged.

The confirmed fixture placement explains a mechanism by which the prior TP path depended on fallback CPU thread count. Applying the same graph construction to the full checkpoint predicts one shared gate/up pair on CPU per TP layer, but these artifacts do not contain a full-checkpoint placement dump or a measured end-to-end improvement. Existing performance baselines and the archived `8e66aaf4…` candidate are preserved. Commands, source hashes and all artifact hashes are in [manifest.json](manifest.json).
