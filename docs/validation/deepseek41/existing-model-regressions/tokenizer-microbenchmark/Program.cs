using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorSharp.Runtime;
using TensorSharp.Models;

using var gguf = new GgufFile(args[0]);
var tokenizer = ModelBase.CreateTokenizerFromGguf(gguf);
using var inputs = JsonDocument.Parse(File.ReadAllText(args[1]));
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var reports = new List<object>();
int sink = 0;
foreach (var input in inputs.RootElement.EnumerateArray())
{
    string name = input.GetProperty("name").GetString()!;
    var messages = JsonSerializer.Deserialize<List<ChatMessage>>(input.GetProperty("messages").GetRawText(), options)!;
    var tools = input.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
        ? ToolFunction.ParseList(t.GetRawText()) : null;
    string Render() => ChatTemplate.RenderFromGgufTemplate(gguf.GetString("tokenizer.chat_template")!,
        messages, true, gguf.GetString("general.architecture"), tools, false);
    string prompt = Render();
    var tokens = tokenizer.Encode(prompt, addSpecial: true);
    string promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
    string tokenHash = Convert.ToHexString(SHA256.HashData(tokens.SelectMany(BitConverter.GetBytes).ToArray()));
    foreach (string operation in new[] { "encode", "render" })
    {
        Action action = operation == "encode" ? () => sink ^= tokenizer.Encode(prompt, true).Count
            : () => sink ^= Render().Length;
        // Warm both regex/template caches and JIT before measuring allocations or time.
        var warm = Stopwatch.StartNew();
        int warmIterations = 0;
        while (warm.ElapsedMilliseconds < 750 || warmIterations < 64)
        {
            action();
            warmIterations++;
        }
        Thread.Sleep(150);
        var samples = new List<object>();
        for (int block = 0; block < 7; block++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int iterations = 0;
            do
            {
                action();
                iterations++;
            } while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 125);
            long finished = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Add(new { iterations,
                nanoseconds_per_operation = (finished - started) * 1e9 / Stopwatch.Frequency / iterations,
                allocated_bytes_per_operation = (double)bytes / iterations });
        }
        reports.Add(new { name, operation, prompt_characters = prompt.Length, token_count = tokens.Count,
            prompt_sha256 = promptHash, token_sha256 = tokenHash, warm_iterations = warmIterations, samples });
    }
}
File.WriteAllText(args[2], JsonSerializer.Serialize(new { implementation = args[3],
    gguf_metadata_source = args[0], architecture = gguf.GetString("general.architecture"),
    platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    process_architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    utc = DateTime.UtcNow, samples_per_operation = 7, minimum_sample_ms = 125,
    warmup_ms = 750, metadata_only_no_tensor_reads = true, sink, reports }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Measured {reports.Count} operations for {args[3]} on {gguf.GetString("general.architecture")}");
