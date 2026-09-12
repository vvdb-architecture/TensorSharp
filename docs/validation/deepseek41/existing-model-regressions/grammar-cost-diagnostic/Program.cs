using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Models;

// Metadata-only: no native operations, GPU, or model tensor reads.
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
using var gguf = new GgufFile(args[0]);
var timer = Stopwatch.StartNew();
var tokenizer = ModelBase.CreateTokenizerFromGguf(gguf);
double tokenizerMs = timer.Elapsed.TotalMilliseconds;
string output = "{\"name\":\"Mars\",\"moons\":2,\"habitable\":false}";
int[] tokens = tokenizer.Encode(output, addSpecial: false).ToArray();
timer.Restart();
var cache = GrammarLibrary.ForJsonObject(tokenizer);
double coldFactoryMs = timer.Elapsed.TotalMilliseconds;
timer.Restart();
var first = GrammarLibrary.NewConstraint(cache, tokenizer);
var initialMask = first.CurrentMask();
double coldInitialMaskMs = timer.Elapsed.TotalMilliseconds;
timer.Restart();
var maskProof = new List<object>();
foreach (int token in tokens)
{
    ulong[] mask = first.CurrentMask();
    if ((mask[token >> 6] & (1UL << (token & 63))) == 0)
        throw new Exception("Recorded output token rejected: " + token);
    maskProof.Add(new { token, mask_sha256 = Hash(mask.SelectMany(BitConverter.GetBytes).ToArray()) });
    first.Accept(token);
}
if (!first.IsComplete || first.IsDead) throw new Exception("Recorded response is incomplete/dead");
double coldResponseMasksAndProofMs = timer.Elapsed.TotalMilliseconds;

long sink = 0;
var logits = new float[tokenizer.VocabSize];
var shared = GrammarLibrary.NewConstraint(cache, tokenizer);
void RequestMasks()
{
    var grammar = GrammarLibrary.NewConstraint(GrammarLibrary.ForJsonObject(tokenizer), tokenizer);
    foreach (int token in tokens)
    {
        sink ^= (long)grammar.CurrentMask()[token >> 6];
        grammar.Accept(token);
    }
    if (!grammar.IsComplete) throw new Exception("Warm response became incomplete");
}
void RequestApply()
{
    var grammar = GrammarLibrary.NewConstraint(GrammarLibrary.ForJsonObject(tokenizer), tokenizer);
    foreach (int token in tokens)
    {
        grammar.ApplyMask(logits, grammar.IsComplete);
        grammar.Accept(token);
    }
    sink ^= grammar.IsComplete ? 1 : 0;
}
object Measure(string name, Action action, int minIterations = 10)
{
    var warm = Stopwatch.StartNew();
    int warmIterations = 0;
    while (warm.ElapsedMilliseconds < 350 || warmIterations < minIterations)
    {
        action(); warmIterations++;
    }
    var blocks = new List<object>();
    for (int block = 0; block < 5; block++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        int iterations = 0;
        do { action(); iterations++; }
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 100 || iterations < minIterations);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        blocks.Add(new { iterations, nanoseconds_per_operation = elapsed / iterations,
            allocated_bytes_per_operation = (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)iterations });
    }
    return new { name, warmIterations, blocks };
}
var measured = new List<object>
{
    Measure("warmed_factory_and_new_constraint", () => {
        var c = GrammarLibrary.NewConstraint(GrammarLibrary.ForJsonObject(tokenizer), tokenizer);
        sink ^= c.IsActive ? 1 : 0;
    }),
    Measure("warmed_factory_and_initial_mask", () => {
        var c = GrammarLibrary.NewConstraint(GrammarLibrary.ForJsonObject(tokenizer), tokenizer);
        sink ^= (long)c.CurrentMask()[0];
    }),
    Measure("warmed_initial_apply_mask", () => shared.ApplyMask(logits, false)),
    Measure("warmed_full_response_masks_and_accept", RequestMasks),
    Measure("warmed_full_response_apply_and_accept", RequestApply),
};
var coldMasks = new List<object>();
// Reuse the immutable token trie but explicitly discard the parser/mask cache.
// This estimates a first visit; the HTTP server normally shares its warm cache.
for (int i = 0; i < 4; i++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long bytes = GC.GetAllocatedBytesForCurrentThread();
    var cold = new GrammarConstraint(Grammar.JsonObject(), tokenizer);
    timer.Restart();
    foreach (int token in tokens) { cold.CurrentMask(); cold.Accept(token); }
    coldMasks.Add(new { milliseconds = timer.Elapsed.TotalMilliseconds,
        allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - bytes, complete = cold.IsComplete });
}
var report = new {
    label = args[2], utc = DateTime.UtcNow,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    process_architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    metadata_only_no_native_calls = true,
    metadata_sha256 = Hash(File.ReadAllBytes(args[0])),
    runtime_assembly_sha256 = Hash(File.ReadAllBytes(typeof(GrammarConstraint).Assembly.Location)),
    models_assembly_sha256 = Hash(File.ReadAllBytes(typeof(ModelBase).Assembly.Location)),
    grammar_sha256 = Hash(Encoding.UTF8.GetBytes(JsonSchemaGrammarCompiler.JsonObjectGrammar)),
    tokenizer.VocabSize, response = output, tokens, maskProof,
    tokenizerMs, coldFactoryMs, coldInitialMaskMs, coldResponseMasksAndProofMs,
    measured, cold_masks_with_shared_vocabulary = coldMasks, sink,
    scope = "Local macOS ARM64 diagnostic, not VM latency/GPU throughput. Cold proof includes mask hashing; warmed operations exclude hashing/tokenization."
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"{args[2]}: {tokens.Length} response tokens admitted; {measured.Count} operations measured");
