using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Models;

string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
using var gguf = new GgufFile(args[0]);
var tokenizer = ModelBase.CreateTokenizerFromGguf(gguf);
var special = tokenizer is ISpecialTokenVocabulary st
    ? st.SpecialTokenIds.ToHashSet() : tokenizer.EosTokenIds.ToHashSet();
var byteIds = new Dictionary<string, List<int>>();
var buffer = new List<byte>();
int unreadable = 0, empty = 0;
for (int id = 0; id < tokenizer.VocabSize; id++)
{
    if (special.Contains(id)) continue;
    buffer.Clear();
    try { tokenizer.AppendTokenBytes(id, buffer); }
    catch { unreadable++; continue; }
    if (buffer.Count == 0) { empty++; continue; }
    string hex = Convert.ToHexString(buffer.ToArray());
    if (!byteIds.TryGetValue(hex, out var ids)) byteIds[hex] = ids = new();
    ids.Add(id);
}
string[] prefixes = { "", "{", "{\n\n", "{\n\n\"", "{\n\n\"name", "{\n\n\"name\": ",
    "{\n\n  ", "{\n\n  \"", "{\n\n  \"Mars\"", "{\n\n  \"Mars\": " };
var cache = GrammarLibrary.ForJsonObject(tokenizer);
var proofs = new List<object>();
Directory.CreateDirectory(args[1]);
for (int i = 0; i < prefixes.Length; i++)
{
    var grammar = GrammarLibrary.NewConstraint(cache, tokenizer);
    int[] tokens = tokenizer.Encode(prefixes[i], addSpecial: false).ToArray();
    foreach (int token in tokens) grammar.Accept(token);
    if (grammar.IsDead) throw new Exception("Invalid prefix " + i);
    ulong[] mask = grammar.CurrentMask();
    byte[] bytes = mask.SelectMany(BitConverter.GetBytes).ToArray();
    File.WriteAllBytes(Path.Combine(args[1], $"prefix-{i}.bin"), bytes);
    proofs.Add(new { prefix = prefixes[i], tokens, mask_sha256 = Hash(bytes),
        allowed_count = mask.Sum(word => System.Numerics.BitOperations.PopCount(word)) });
}
// A global byte table allows the independent comparison to describe changed IDs.
File.WriteAllText(Path.Combine(args[1], "token-bytes.json"), JsonSerializer.Serialize(byteIds));
File.WriteAllText(Path.Combine(args[1], "report.json"), JsonSerializer.Serialize(new {
    label = args[2], tokenizer.VocabSize, special_count = special.Count, unreadable, empty,
    duplicates = byteIds.Where(p => p.Value.Count > 1).Select(p => new { bytes_hex = p.Key, ids = p.Value }),
    metadata_sha256 = Hash(File.ReadAllBytes(args[0])),
    runtime_sha256 = Hash(File.ReadAllBytes(typeof(GrammarConstraint).Assembly.Location)),
    grammar_sha256 = Hash(Encoding.UTF8.GetBytes(JsonSchemaGrammarCompiler.JsonObjectGrammar)),
    proofs, scope = "Metadata-only local exact-byte JSON mask comparison, no inference or model tensor reads."
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"{args[2]}: {byteIds.Count(p => p.Value.Count > 1)} duplicate byte groups, {proofs.Count} masks");
