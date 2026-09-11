# Named-tool serialization replay

The actual TP8/3,566-test host's `named_thinking` request failed at its 2,048-token
limit, with no tool calls and assistant content consisting of two newlines. The
other 29 tool-policy cases passed. This replay does not change that result or
execute another inference request.

The raw output closed reasoning after 1,536 tokens, then began a `get_weather`
invoke. Its city argument started `Paris</parameter_name>`, followed by more wrong
tags and invented weather prose. Those unrelated tags were legal content of the
unbounded raw city string; no complete invoke was produced. The output parser
correctly withheld it.

Using the actual Q2_K vocabulary and retained 3,566-stage Runtime, canonical
re-encoding produces 2,048 tokens. The grammar activates at token index 1,536,
admits every token, never becomes dead, and remains incomplete. As a diagnostic,
appending the actual city delimiter and remaining correct envelope closes yields
a call whose city contains 1,934 characters of wrong tags/prose. That appended
suffix is **not** a repaired response and is never credited as an actual success.

The subsequent serialization rule reserves the raw prefixes `<param`, `</param`,
`<invoke`, and `</invoke`. Replaying the same failure against that rule rejects
token **41523**, `parameter`, at zero-based index **1564**: after the already legal
`</` prefix, this token would begin the malformed `</parameter_name>` tag. Replay
stops before committing that denied token; it does not invent the token that
inference would select instead.

Seven separate valid paths pass every token mask, complete the grammar, and parse
to exactly their intended values: canonical Paris/celsius, unrelated `<x>ok</x>`
XML, comparisons, Unicode, and three reserved-tag-family values carried through
the existing `string="false"` JSON fallback. Thus arbitrary logical string values
remain expressible. Full-model verification of this serialization change remains
pending.

- [Original request, failure and source provenance](request-and-provenance.json)
- [Old mask replay](before.json)
- [New mask replay and seven valid paths](after.json)

The old Runtime SHA is
`a0dd4212d7f399241ffa0fc78204206cbc3da3df6ed888c12997bae118d5e86e`;
the new Runtime SHA is
`1542edf883c89fb5a53c93dadabff111316fd1ae49582acd74e48f862e3b0e09`.
These are local built assemblies with source-equivalent stage changes; the raw
request/response provenance retains the separately built actual host identity.
Tokens in both replays are canonical re-encoding of the retained raw output,
rather than captured original generation token IDs.

The local replay programs are
`/tmp/deepseek41-reference/named-thinking-replay/Program.cs` and
`/tmp/deepseek41-reference/named-thinking-replay-new/Program.cs`; their hashes are
in the reports. Both use an isolated project referencing existing assemblies:

```sh
dotnet build /tmp/deepseek41-reference/named-thinking-replay-new/replay.csproj \
  -c Release -p:EngineBin=/absolute/path/to/existing/test-or-host-output \
  -o /tmp/deepseek41-reference/named-thinking-replay-new/bin
dotnet /tmp/deepseek41-reference/named-thinking-replay-new/bin/replay.dll \
  /tmp/deepseek41-reference/q2-header-1.json \
  /tmp/deepseek41-reference/tp8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-chunk1024-b26c-tools-named-thinking-failure.json \
  /tmp/deepseek41-reference/named-thinking-replay-new/report.json
```
