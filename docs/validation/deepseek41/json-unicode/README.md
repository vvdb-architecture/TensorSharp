# Generic JSON UTF-8 validation

This probe uses the actual Q2_K checkpoint's 129,280-token vocabulary, token
types and merges. It does not load tensor weights, run inference, or use the
DSML tool grammar. Every generated candidate is checked against the token mask
and masked logits before acceptance. Complete text is decoded with strict UTF-8
and parsed as JSON.

| Runtime stage | Valid JSON paths | Invalid-byte checks |
|---|---:|---|
| Exact 3,433-test HTTP host | 3/7; all four literal/split rocket paths rejected | Initial four negative probes retained; this stage also admitted a closing quote before the emoji completed |
| Updated partial-byte masks, before scalar validation | 7/7 | All five complete malformed UTF-8 strings admitted as completed JSON; one impossible prefix also admitted |
| Partial-byte masks and scalar validation | 7/7 | 9/9 rejected: four invalid prefixes and five complete malformed strings |

The exact old host Runtime SHA-256 is
`12ce3bc3f780853aab91eafee2dec49d53eba34493cb6881d3c07b54130e67ff`.
The valid rocket tokenization contains token 74287 (`F0 9F 9A`) followed by
token 225 (`80`). Both were masked by that host. Forced individual UTF-8 bytes
use tokens `[175, 256, 251, 225]`; all four were also masked. Lowercase and
uppercase JSON surrogate escapes remained allowed. The updated mask handling
permits all of these encodings without relaxing JSON syntax.

The separate scalar-validation defect accepted overlong encodings (`C0 AF`,
`E0 80 AF`, `F0 80 80 AF`), a UTF-16 surrogate (`ED A0 80`), and a value above
U+10FFFF (`F4 90 80 80`). These sequences could complete the grammar while a
normal UTF-8 decoder substituted U+FFFD. The decoder now rejects illegal
leading bytes and impossible first continuations before a token can commit
them. Escaped ASCII surrogate pairs remain legal JSON; they do not pass through
the raw UTF-8 scalar decoder as surrogate characters.

The 22 new regression tests include all 1,112,064 valid Unicode scalar values,
decoded one byte at a time, and whole-token/split-token invalid-byte masking.
The combined focused grammar lane passed **114/114**, with no skips. The actual
checkpoint-tokenizer probe passed **16/16**. Final full-model Unicode HTTP
verification must be recorded separately; these artifacts establish the
masking/decoding defects and their local fixes, not model-level output parity.

Artifacts retain each token, raw byte spelling, mask decision, reconstructed
output and runtime/source hash:

- [Exact old server](exact-3433-before.json)
- [Partial-mask fix before scalar validation](partial-mask-fix-before-scalar-validation.json)
- [Final actual-tokenizer check](final.json)

Reproduce with a built host or test output directory and the cached GGUF header
JSON containing `metadata`:

```sh
dotnet build eng/tests/dsv41-json-unicode/dsv41-json-unicode.csproj \
  -c Release -p:EngineBin=/absolute/path/to/host-or-test-output \
  -o /tmp/dsv41-json-unicode
dotnet /tmp/dsv41-json-unicode/dsv41-json-unicode.dll \
  /tmp/deepseek41-reference/q2-header-1.json /tmp/json-unicode.json
dotnet test InferenceWeb.Tests/InferenceWeb.Tests.csproj -c Release \
  -p:TensorSharpSkipGgmlNative=true \
  --filter 'FullyQualifiedName~GrammarUtf8ValidationTests|FullyQualifiedName~GrammarConstrainedDecodingTests|FullyQualifiedName~DeepSeek41GrammarTriggerTests'
```

The isolated probe references existing assemblies and does not rebuild them.
The saved final Runtime SHA is
`a0dd4212d7f399241ffa0fc78204206cbc3da3df6ed888c12997bae118d5e86e`.
