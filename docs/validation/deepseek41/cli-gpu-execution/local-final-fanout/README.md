# Final Engram fanout CPU verification

The locally rebuilt final combined source passed **41/41** stable-fixture CPU checks at `atol=rtol=2e-5`, including chunked prefill, decode, reset, rejected rewind continuation, and interleaved slots. All greedy tokens match the independent PyTorch oracle. Maximum absolute error: `5.16325235367e-06`.

Source SHA-256: `24246c67729630b9e6c95c8f660068d06f0b53f8b523e7c935c1e5c0e5d6609d`. Local native SHA-256: `ec84fb46750f343dcdf5a291adffd27f15fdedffdb0b90f587fd54809ed48541`. The exact command, cleared override names, fixture/source hashes, build/test logs, and full per-check results are retained here. The local native library is copied here to preserve this test's identity.

This verifies the final local CPU fixture behavior. It is not a VM execution or full-model performance measurement. The previously recorded actual Metal fallback rejection tests remain in the parent evidence directory and were not repeated.
