# Tokenizer/render diagnostic reproduction

The nine BMP prompts are in `inputs.json`; `Program.cs` measures seven warm-JIT samples for encode and render. Per-run JSON files retain every sample, rendered/token hashes, lengths and allocations. `run-manifest.json` retains commands, run order, start times and exit status. Its original 46 MB stderr consisted of complete GGUF metadata/vocabulary dumps; those dumps are preserved externally at `/tmp/dsv41-tokenizer-microbench/run-manifest-full-original.json`, with per-run SHA256 hashes here. No timing samples were removed.

`model-sources.json` records exact Hugging Face repositories, immutable revisions, filenames, full-file SHA256 and sizes. These match the download metadata on the validation VM. Use `huggingface_hub.hf_hub_download(repo_id=..., revision=..., filename=...)` for each entry, and verify its SHA256. Full models remain on the VM under `/workspace/models`.

Only the first 16 MiB of each file was needed for this diagnostic. To reconstruct the metadata-only inputs without committing weights:

```python
from pathlib import Path
import hashlib, json

sources = json.loads(Path("model-sources.json").read_text())
expected = json.loads(Path("provenance.json").read_text())["metadata_sha256"]
for source in sources:
    # Set checkpoint to the full downloaded GGUF verified against source["sha256"].
    checkpoint = Path("/workspace/models") / source["repo"].split("/")[-1] / source["filename"]
    with checkpoint.open("rb") as f:
        data = f.read(16 * 1024 * 1024)
    assert hashlib.sha256(data).hexdigest() == expected[source["model"]]
    Path(source["model"] + "-metadata-only.gguf").write_bytes(data)
```

These prefixes are intentionally truncated and cannot run inference. `GgufFile` reads their tokenizer metadata; the probe performs no tensor reads and invokes no native model operations. Full GGUF files produce the same inputs.

Build baseline source commit `04a5faa9e641fc0b06de661be1191afbc6edaf50` in a separate directory. Build `probe.csproj` for each implementation with `dotnet build probe.csproj -c Release -p:EngineBin=/path/to/its/managed/binaries`, then run `dotnet probe.dll MODEL.gguf inputs.json output.json LABEL`. Reproduce the recorded current/baseline/baseline/current order on an otherwise idle machine. Recorded timings are macOS ARM64 diagnostics, not VM performance claims.
