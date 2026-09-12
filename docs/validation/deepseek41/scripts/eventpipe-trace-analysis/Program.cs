using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

static class Program
{
    const string ParserSha = "7ad04f5abbd704e8bd9c7b29f9aeab951f20cdc5d5c8e701f256be52a6e8543e";
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    static object Source(string p) => new { path = Path.GetFullPath(p), bytes = new FileInfo(p).Length, sha256 = Hash(p) };
    static void Write(string p, object value) => File.WriteAllText(p, JsonSerializer.Serialize(value, Pretty));
    static void Line(StreamWriter s, object value) => s.WriteLine(JsonSerializer.Serialize(value));
    static double Number(JsonElement e) { var d = e.GetDouble(); if (!double.IsFinite(d)) throw new InvalidDataException("Nonfinite timestamp"); return d; }
    static DateTimeOffset Utc(JsonElement e) => DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    record Anchor(DateTimeOffset Utc, double Monotonic, double Uncertainty)
    {
        public static Anchor Read(JsonElement e)
        {
            var a = new Anchor(Program.Utc(e.GetProperty("utc")), Number(e.GetProperty("monotonic")), Number(e.GetProperty("anchor_uncertainty_seconds")));
            if (a.Uncertainty < 0 || Math.Abs(a.Utc.ToUnixTimeMilliseconds() / 1000.0 - Number(e.GetProperty("unix_seconds"))) > .002)
                throw new InvalidDataException("Invalid anchor or UTC/unix mismatch");
            return a;
        }
        public DateTimeOffset Map(double mono) => Utc.AddSeconds(mono - Monotonic);
    }
    record Window(DateTimeOffset Start, DateTimeOffset End)
    {
        public bool Contains(DateTimeOffset x) => x >= Start && x <= End;
        public double OverlapMilliseconds(DateTimeOffset start, DateTimeOffset end) => Math.Max(0, (Min(End, end) - Max(Start, start)).TotalMilliseconds);
        static DateTimeOffset Min(DateTimeOffset x, DateTimeOffset y) => x < y ? x : y;
        static DateTimeOffset Max(DateTimeOffset x, DateTimeOffset y) => x > y ? x : y;
    }
    record RequestWindow(string Tag, int Concurrency, DateTimeOffset Start, DateTimeOffset First, DateTimeOffset End);
    sealed class Counts { public long samples; public long missing_stack; public long engine_samples; public Dictionary<string, long> sample_types = new(); public Dictionary<int, long> stacks = new(); }
    static void Add<K>(Dictionary<K, long> d, K key) where K : notnull => d[key] = d.GetValueOrDefault(key) + 1;
    sealed class PauseTracker(Window window)
    {
        public record PendingSuspend(DateTimeOffset Start, string Reason);
        public record SuspendInterval(string InstanceThread, string Reason, DateTimeOffset Start, DateTimeOffset End, double SpanMilliseconds, double WindowOverlapMilliseconds);
        public readonly Dictionary<string, PendingSuspend> Pending = new();
        public readonly List<SuspendInterval> Pauses = new();
        public readonly List<object> Issues = new();
        public void Feed(string name, string instance, DateTimeOffset at, string reason = "unknown")
        {
            if (name == "GC/SuspendEEStart")
            {
                if (Pending.TryGetValue(instance, out var old)) Issues.Add(new { issue = "unmatched prior suspend start", instance, at = old });
                Pending[instance] = new(at, reason);
            }
            else if (name == "GC/RestartEEStop")
            {
                if (Pending.Remove(instance, out var start))
                {
                    if (at < start.Start) Issues.Add(new { issue = "negative pause interval", instance, start, end = at });
                    else if (window.OverlapMilliseconds(start.Start, at) > 0)
                        Pauses.Add(new(instance, start.Reason, start.Start, at, (at - start.Start).TotalMilliseconds, window.OverlapMilliseconds(start.Start, at)));
                }
                else if (window.Contains(at)) Issues.Add(new { issue = "restart without observed suspend", instance, at });
            }
        }
    }
    static Dictionary<string, string> Payload(TraceEvent e)
    {
        var r = new Dictionary<string, string>();
        foreach (var n in e.PayloadNames) r[n] = Convert.ToString(e.PayloadByName(n), CultureInfo.InvariantCulture) ?? "";
        return r;
    }
    static List<RequestWindow> Requests(JsonElement r, Anchor a, Window window, int expected)
    {
        var list = new List<RequestWindow>();
        foreach (var c in r.GetProperty("cases").EnumerateArray())
        {
            var turns = c.GetProperty("turns");
            if (turns.GetArrayLength() != 1) throw new InvalidDataException("Expected one turn per JSON case");
            var m = turns[0].GetProperty("metrics");
            var w = new RequestWindow(c.GetProperty("tag").GetString()!, c.GetProperty("concurrency").GetInt32(),
                a.Map(Number(m.GetProperty("t_start_abs"))), a.Map(Number(m.GetProperty("t_first_abs"))), a.Map(Number(m.GetProperty("t_end_abs"))));
            if (w.Start > w.First || w.First > w.End || !window.Contains(w.Start) || !window.Contains(w.End))
                throw new InvalidDataException("Request timestamps are unordered or outside recorded case interval");
            list.Add(w);
        }
        if (list.Count != expected || list.Select(x => x.Tag).Distinct().Count() != expected) throw new InvalidDataException("Incomplete or duplicate case coverage");
        return list;
    }
    static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" })) return SelfTest();
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2) { if (i + 1 >= args.Length || !new[] { "--trace", "--metadata", "--report", "--out" }.Contains(args[i]) || !opts.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Usage: --trace FILE --metadata FILE --report FILE --out NEWDIR"); }
        foreach (var name in new[] { "--trace", "--metadata", "--report", "--out" }) if (!opts.ContainsKey(name)) throw new ArgumentException("Missing " + name);
        string output = Path.GetFullPath(opts["--out"]);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Refusing existing output");
        var parser = typeof(TraceLog).Assembly.Location;
        if (Hash(parser) != ParserSha) throw new InvalidDataException("Pinned TraceEvent parser changed");
        using var metadata = JsonDocument.Parse(File.ReadAllText(opts["--metadata"]));
        using var requestReport = JsonDocument.Parse(File.ReadAllText(opts["--report"]));
        var root = metadata.RootElement;
        var requestRoot = requestReport.RootElement;
        var boundMetadata = requestRoot.GetProperty("eventpipe_evidence");
        if (boundMetadata.GetProperty("sha256").GetString() != Hash(opts["--metadata"]) || boundMetadata.GetProperty("bytes").GetInt64() != new FileInfo(opts["--metadata"]).Length)
            throw new InvalidDataException("Request report does not bind this exact capture metadata");
        if (!requestRoot.GetProperty("run_complete").GetBoolean() || requestRoot.GetProperty("version").GetString() != root.GetProperty("version").GetString())
            throw new InvalidDataException("Incomplete request report or capture version mismatch");
        int pid = root.GetProperty("host_pid").GetInt32();
        if (pid <= 0) throw new InvalidDataException("Invalid target PID");
        var raw = root.GetProperty("raw_nettrace");
        if (Hash(opts["--trace"]) != raw.GetProperty("sha256").GetString() || new FileInfo(opts["--trace"]).Length != raw.GetProperty("bytes").GetInt64()) throw new InvalidDataException("Raw trace differs from collection manifest");
        var ready = Anchor.Read(root.GetProperty("collection_ready"));
        var stop = Anchor.Read(root.GetProperty("stop_requested"));
        var final = Anchor.Read(root.GetProperty("finalized"));
        var attach = Anchor.Read(root.GetProperty("attach_requested"));
        var interval = root.GetProperty("actual_case_monotonic_interval");
        if (interval.GetArrayLength() != 2) throw new InvalidDataException("Invalid request interval");
        double startMono = Number(interval[0]), endMono = Number(interval[1]);
        var requestInterval = requestRoot.GetProperty("timed_monotonic_interval");
        if (requestInterval.GetArrayLength() != 2 || Number(requestInterval[0]) != startMono || Number(requestInterval[1]) != endMono)
            throw new InvalidDataException("Report and capture request intervals differ");
        if (!(attach.Monotonic <= ready.Monotonic && ready.Monotonic <= startMono && startMono < endMono && endMono <= stop.Monotonic && stop.Monotonic <= final.Monotonic)) throw new InvalidDataException("Collection does not enclose timed interval");
        var window = new Window(ready.Map(startMono), ready.Map(endMono));
        var requests = Requests(requestReport.RootElement, ready, window, root.GetProperty("expected_measured_cases").GetInt32());
        double clockDrift = new[] { attach, stop, final }.Max(a => Math.Abs((a.Utc - ready.Map(a.Monotonic)).TotalSeconds));
        double uncertainty = new[] { attach, ready, stop, final }.Max(a => a.Uncertainty);
        var lost = new List<object>();
        var summary = new Dictionary<string, object?> {
            ["scope"] = "Diagnostic managed thread-stack sampling, not measured on-CPU time. Raw GC/threading/contention events retained. No kernel/native stack or off-CPU scheduler attribution.",
            ["analysis_complete"] = false, ["target_pid"] = pid,
            ["inputs"] = new { trace = Source(opts["--trace"]), metadata = Source(opts["--metadata"]), requests = Source(opts["--report"]), parser = Source(parser), analyzer = Source(Assembly.GetExecutingAssembly().Location) },
            ["window"] = new { start_utc = window.Start, end_utc = window.End, start_monotonic = startMono, end_monotonic = endMono, anchor = ready, max_anchor_clock_drift_seconds = clockDrift, max_anchor_uncertainty_seconds = uncertainty, alignment_method = "collection_ready UTC plus monotonic delta; no sample-duration to CPU conversion" },
            ["request_windows"] = requests, ["lost_event_callbacks"] = lost,
            ["collection_complete"] = root.GetProperty("collection_complete").GetBoolean(),
            ["limitations"] = new[] { "Per-case sample tallies overlap for concurrent requests; they are temporal correlations, not attribution to a request.", "Unresolved managed/native boundary frames do not reveal native CPU or GPU execution.", "Runtime suspend spans are separated by Reason. SuspendOther includes profiler sampling; GCStart-to-GCStop is not used as pause time.", "Thread and sample counts can vary with runtime sampler scheduling; no exact CPU milliseconds are calculated." }
        };
        Directory.CreateDirectory(output);
        Write(Path.Combine(output, "summary.json"), summary);
        try
        {
            var options = new TraceLogOptions { ContinueOnError = false, KeepAllEvents = true, ConversionLogName = Path.Combine(output, "conversion.log"),
                OnLostEvents = (truncated, lostCount, totalCount) => lost.Add(new { callback_flag = truncated, lost_count = lostCount, total_count = totalCount }) };
            string etlx = TraceLog.CreateFromEventPipeDataFile(opts["--trace"], Path.Combine(output, "trace.etlx"), options);
            using var log = new TraceLog(etlx);
            var providers = new Dictionary<string, long>(); var windowProviders = new Dictionary<string, long>();
            var threads = new Dictionary<int, Counts>();
            var stackTable = new Dictionary<string, int>(); var stackDefinitions = new List<object>();
            var requestCounts = requests.ToDictionary(r => r.Tag, _ => new Dictionary<string, long>());
            long targetEvents = 0, samples = 0, missing = 0, unresolved = 0, truncatedStacks = 0, engine = 0;
            DateTimeOffset? firstTarget = null, lastTarget = null, firstSample = null, lastSample = null;
            var pauses = new PauseTracker(window);
            using var sampleOut = new StreamWriter(Path.Combine(output, "samples.jsonl"));
            using var clrOut = new StreamWriter(Path.Combine(output, "runtime-events.jsonl"));
            foreach (var e in log.Events)
            {
                if (e.ProcessID != pid) continue;
                targetEvents++;
                var at = new DateTimeOffset(e.TimeStamp.ToUniversalTime());
                firstTarget = firstTarget is null || at < firstTarget ? at : firstTarget;
                lastTarget = lastTarget is null || at > lastTarget ? at : lastTarget;
                string key = e.ProviderName + "/" + e.EventName;
                Add(providers, key);
                bool inside = window.Contains(at);
                if (inside) Add(windowProviders, key);
                bool clr = e.ProviderName == "Microsoft-Windows-DotNETRuntime";
                bool runtime = clr && (e.EventName.StartsWith("GC/") || e.EventName.Contains("Thread") || e.EventName.Contains("Contention"));
                if (runtime)
                {
                    var payload = Payload(e);
                    pauses.Feed(e.EventName, payload.GetValueOrDefault("ClrInstanceID", "unknown") + ":" + e.ThreadID, at, payload.GetValueOrDefault("Reason", "unknown"));
                    // Retain whole-session runtime events so boundary pairs remain auditable.
                    Line(clrOut, new { utc = at, relative_ms = e.TimeStampRelativeMSec, pid, tid = e.ThreadID, inside_window = inside, provider = e.ProviderName, name = e.EventName, payload });
                }
                if (!inside || e.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
                samples++;
                firstSample = firstSample is null || at < firstSample ? at : firstSample;
                lastSample = lastSample is null || at > lastSample ? at : lastSample;
                if (!threads.TryGetValue(e.ThreadID, out var count)) threads[e.ThreadID] = count = new Counts();
                count.samples++;
                var samplePayload = Payload(e);
                string type = string.Join(",", samplePayload.Select(p => p.Key + "=" + p.Value));
                Add(count.sample_types, type);
                var frames = new List<string>(); var stack = log.GetCallStackForEvent(e);
                bool hasUnresolved = false;
                while (stack != null && frames.Count < 2048)
                {
                    string method = stack.CodeAddress.FullMethodName;
                    if (string.IsNullOrWhiteSpace(method)) { method = (stack.CodeAddress.ModuleName ?? "unknown") + "!0x" + stack.CodeAddress.Address.ToString("x"); hasUnresolved = true; }
                    frames.Add(method); stack = stack.Caller;
                }
                bool truncated = stack != null;
                if (truncated) truncatedStacks++;
                if (hasUnresolved) unresolved++;
                if (frames.Count == 0) { missing++; count.missing_stack++; }
                bool isEngine = frames.Any(f => f.Contains("InferenceEngine") && f.Contains("WorkerLoop"));
                if (isEngine) { engine++; count.engine_samples++; }
                string frameKey = string.Join("\n", frames);
                if (!stackTable.TryGetValue(frameKey, out int id))
                {
                    id = stackTable.Count; stackTable[frameKey] = id;
                    stackDefinitions.Add(new { id, frames_leaf_to_root = frames, engine_worker_loop = isEngine, unresolved_frame = hasUnresolved, depth_limit_reached = truncated });
                }
                Add(count.stacks, id);
                var matches = requests.Where(r => at >= r.Start && at <= r.End).Select(r => new { tag = r.Tag, phase = at < r.First ? "before_first_token" : "after_first_token" }).ToArray();
                foreach (var r in matches) { Add(requestCounts[r.tag], r.phase + "_all_thread_samples"); if (isEngine) Add(requestCounts[r.tag], r.phase + "_engine_thread_samples"); }
                Line(sampleOut, new { utc = at, relative_ms = e.TimeStampRelativeMSec, pid, tid = e.ThreadID, event_name = e.EventName, payload = samplePayload, stack_id = id, requests = matches });
            }
            foreach (var p in pauses.Pending) if (p.Value.Start <= window.End) pauses.Issues.Add(new { issue = "suspend has no observed restart", instance = p.Key, start = p.Value });
            sampleOut.Flush(); clrOut.Flush();
            Write(Path.Combine(output, "stacks.json"), stackDefinitions);
            var threadInfo = log.Threads.Where(t => t.Process.ProcessID == pid).Select(t => new { tid = t.ThreadID, thread_index = t.ThreadIndex.ToString(), name = t.VerboseThreadName, info = t.ThreadInfo, start_relative_ms = t.StartTimeRelativeMSec, end_relative_ms = t.EndTimeRelativeMSec }).ToArray();
            summary["thread_metadata"] = threadInfo;
            summary["per_thread"] = threads.OrderByDescending(kv => kv.Value.samples).Select(kv => new { tid = kv.Key, samples = kv.Value.samples, missing_stack = kv.Value.missing_stack, engine_samples = kv.Value.engine_samples, sample_types = kv.Value.sample_types, stacks = kv.Value.stacks.OrderByDescending(s => s.Value).Select(s => new { stack_id = s.Key, samples = s.Value }) }).ToArray();
            summary["per_request_temporal_sample_counts"] = requestCounts;
            summary["session"] = new { start_utc = log.SessionStartTime.ToUniversalTime(), end_utc = log.SessionEndTime.ToUniversalTime(), duration_ms = log.SessionEndTimeRelativeMSec, all_event_count = log.EventCount, target_events = targetEvents, first_target_utc = firstTarget, last_target_utc = lastTarget, target_sample_count = samples, engine_worker_samples = engine, first_window_sample_utc = firstSample, last_window_sample_utc = lastSample, missing_stack_samples = missing, unresolved_frame_samples = unresolved, depth_limit_samples = truncatedStacks, has_callstacks = log.HasCallStacks, events_lost = log.EventsLost, first_time_inversion = log.FirstTimeInversion.ToString() };
            summary["target_event_counts_full_trace"] = providers;
            summary["target_event_counts_window"] = windowProviders;
            Write(Path.Combine(output, "suspensions.json"), pauses.Pauses);
            summary["gc_reason_suspensions"] = pauses.Pauses.Where(p => p.Reason is "SuspendForGC" or "SuspendForGCPrep").ToArray();
            summary["runtime_suspension_by_reason"] = pauses.Pauses.GroupBy(p => p.Reason).Select(g => new { reason = g.Key, count = g.Count(), summed_window_overlap_ms = g.Sum(p => p.WindowOverlapMilliseconds), maximum_span_ms = g.Max(p => p.SpanMilliseconds) }).ToArray();
            summary["runtime_suspension_note"] = "Paired by CLR instance and initiating OS thread; concurrent profiler and GC suspend attempts may overlap. Summed spans are not exclusive stop-the-world time. SuspendOther is not labeled GC (observed sampler cadence).";
            summary["runtime_suspension_pairing_issues"] = pauses.Issues;
            var issues = new List<string>();
            if (!root.GetProperty("collection_complete").GetBoolean()) issues.Add("Collector did not report complete collection");
            if (clockDrift > .005 + uncertainty * 2) issues.Add("UTC/monotonic anchor drift exceeds 5 ms plus measurement uncertainty");
            if (log.EventsLost != 0 || lost.Count != 0) issues.Add("Parser reported event loss or a loss callback");
            if (log.FirstTimeInversion != EventIndex.Invalid) issues.Add("Trace contains a time inversion");
            if (firstTarget is null || firstTarget > window.Start || lastTarget < window.End) issues.Add("Observed target-process events do not enclose the request window");
            if (log.SessionStartTime.ToUniversalTime() > window.Start.UtcDateTime || log.SessionEndTime.ToUniversalTime() < window.End.UtcDateTime) issues.Add("Trace session does not enclose the request window");
            if (samples == 0 || !log.HasCallStacks) issues.Add("No target stack samples in request window");
            if (missing != 0 || truncatedStacks != 0) issues.Add("Missing or depth-limited sampled stacks");
            if (pauses.Issues.Count != 0) issues.Add("Unpaired or invalid runtime suspension endpoints");
            summary["integrity_issues"] = issues;
            summary["analysis_complete"] = true;
            summary["window_integrity_passed"] = issues.Count == 0;
            summary["unresolved_stacks_warning"] = unresolved != 0;
            summary["outputs"] = new[] { "samples.jsonl", "runtime-events.jsonl", "stacks.json", "suspensions.json", "conversion.log" }.Where(n => File.Exists(Path.Combine(output, n))).Select(n => Source(Path.Combine(output, n))).ToArray();
            Write(Path.Combine(output, "summary.json"), summary);
            Console.WriteLine(JsonSerializer.Serialize(new { output, samples, engine_samples = engine, issues }));
            return issues.Count == 0 ? 0 : 2;
        }
        catch (Exception e)
        {
            summary["analysis_error"] = e.ToString(); summary["window_integrity_passed"] = false;
            Write(Path.Combine(output, "summary.json"), summary); Console.Error.WriteLine(e); return 1;
        }
    }
    static int SelfTest()
    {
        var checks = new List<string>();
        void Check(string name, bool ok) { if (!ok) throw new Exception(name); checks.Add(name); }
        var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z"); var w = new Window(start, start.AddSeconds(2));
        Check("window excludes before", !w.Contains(start.AddTicks(-1)));
        Check("window excludes after", !w.Contains(start.AddSeconds(2).AddTicks(1)));
        Check("window includes boundaries", w.Contains(start) && w.Contains(start.AddSeconds(2)));
        var a = new Anchor(start, 500, .000001);
        Check("UTC mapped from monotonic delta", a.Map(501) == start.AddSeconds(1));
        Check("clip overlapping pause", w.OverlapMilliseconds(start.AddSeconds(-1), start.AddSeconds(1)) == 1000);
        Check("nonoverlap has zero duration", w.OverlapMilliseconds(start.AddSeconds(3), start.AddSeconds(4)) == 0);
        var p = new PauseTracker(w); p.Feed("GC/SuspendEEStart", "1", start.AddSeconds(-1)); p.Feed("GC/RestartEEStop", "1", start.AddSeconds(1));
        Check("pause spanning window retained", p.Pauses.Count == 1 && p.Pending.Count == 0 && p.Issues.Count == 0);
        p.Feed("GC/RestartEEStop", "2", start.AddSeconds(1)); Check("unmatched restart reported", p.Issues.Count == 1);
        p.Feed("GC/SuspendEEStart", "3", start); p.Feed("GC/SuspendEEStart", "3", start.AddSeconds(1)); Check("duplicate suspend reported", p.Issues.Count == 2);
        p.Feed("GC/RestartEEStop", "3", start); Check("negative pause reported", p.Issues.Count == 3);
        var q = new PauseTracker(w); q.Feed("GC/Start", "1", start); q.Feed("GC/Stop", "1", start.AddSeconds(1)); Check("GC collection is not pause interval", q.Pauses.Count == 0);
        var counts = new Dictionary<string, long>(); Add(counts, "x"); Add(counts, "x"); Check("stack samples counted without duration weights", counts["x"] == 2);
        var parallel = new PauseTracker(w); parallel.Feed("GC/SuspendEEStart", "0:1", start, "SuspendForGCPrep"); parallel.Feed("GC/SuspendEEStart", "0:2", start.AddMilliseconds(1), "SuspendOther"); parallel.Feed("GC/RestartEEStop", "0:1", start.AddMilliseconds(2)); parallel.Feed("GC/RestartEEStop", "0:2", start.AddMilliseconds(3));
        Check("concurrent GC and sampler suspensions pair independently", parallel.Pauses.Count == 2 && parallel.Issues.Count == 0 && parallel.Pending.Count == 0);
        Check("suspension reasons retained without relabeling profiler as GC", parallel.Pauses.Select(p => p.Reason).SequenceEqual(new[] { "SuspendForGCPrep", "SuspendOther" }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks, scope = "Local reducer checks; real raw trace parsing is tested separately" }, Pretty)); return 0;
    }
}
