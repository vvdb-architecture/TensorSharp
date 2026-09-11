// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Diagnostics;
using System.Text;
using TensorAgent.Core.Hosting;
using TensorAgent.Sharing;

namespace TensorAgent.Tests;

/// <summary>
/// What a share COSTS, measured rather than assumed.
///
/// <para>
/// Two costs matter and they are different kinds. The first is wall-clock: the gap
/// between tapping "Ask" in another app and seeing the message in TensorAgent's
/// composer, which is entirely this code and must be invisible next to the model load
/// it lands in front of. The second is TOKENS: how much of a phone's context one share
/// spends, which is the thing that decides whether the answer arrives in forty seconds
/// or gets the process killed by jetsam.
/// </para>
/// <para>
/// The floors are deliberately loose. They run on whatever machine the suite runs on
/// and exist to catch a collapse — an accidental O(n²) in the HTML scanner, a budget
/// that stopped being applied — not to measure the hardware. Every one prints its
/// number, so a regression is visible in the log even when the assertion passes.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ShareBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-sharebench-" + Guid.NewGuid().ToString("N"));
    private AgentAppHost? _host;

    private string Inbox => Path.Combine(_root, "group", ShareContainer.InboxDirectoryName);

    public void Dispose()
    {
        _host?.Dispose();
        TensorSharp.AgentHost.CodeExec.CodeEnvironment.Reset();
        try { Directory.Delete(_root, true); } catch (Exception) { /* scratch */ }
    }

    private AgentAppHost Start()
    {
        Directory.CreateDirectory(Inbox);
        _host = new AgentAppHost(new AgentPaths(
            Path.Combine(_root, "data"), Path.Combine(_root, "cache"))
        {
            SharedInboxDirectory = Inbox,
        });
        return _host;
    }

    /// <summary>A news article's worth of real-shaped HTML: navigation, a cookie
    /// banner, the article, a related-stories rail and a footer.</summary>
    private static string Article(int paragraphs)
    {
        var html = new StringBuilder(
            "<html><head><title>Small models are winning</title>"
            + "<style>body{margin:0}.nav a{color:#333}" + new string('x', 4000) + "</style>"
            + "<script>var tracking = " + new string('9', 4000) + ";</script></head><body>"
            + "<nav><a href=\"/a\">Home</a><a href=\"/b\">World</a><a href=\"/c\">Tech</a></nav>"
            + "<div class=\"cookie\">We and our 812 partners store cookies.</div><article>");
        for (int i = 0; i < paragraphs; i++)
        {
            html.Append("<p>Paragraph ").Append(i)
                .Append(" of the article, with an &amp; and an &rsquo; in it, saying something ")
                .Append("that a reader might reasonably want summarised later on.</p>");
        }
        html.Append("</article><aside><h3>Related</h3><ul>");
        for (int i = 0; i < 40; i++)
            html.Append("<li><a href=\"/r").Append(i).Append("\">Another story ").Append(i).Append("</a></li>");
        return html.Append("</ul></aside><footer>© 2026</footer></body></html>").ToString();
    }

    // =====================================================================================
    // wall clock
    // =====================================================================================

    [Fact]
    public void ExtractingAWebPageIsFastEnoughToBeInvisible()
    {
        string html = Article(600);
        // One pass so the first-call JIT is not what is measured.
        _ = ShareText.FromHtml(html);

        var clock = Stopwatch.StartNew();
        string text = ShareText.FromHtml(html);
        clock.Stop();

        double perSecond = html.Length / clock.Elapsed.TotalSeconds / (1024 * 1024);
        Console.WriteLine(
            $"share: HTML→text {html.Length / 1024.0:0.#} kB in {clock.Elapsed.TotalMilliseconds:0.#} ms "
            + $"({perSecond:0.#} MB/s), {text.Length / 1024.0:0.#} kB of text out");

        // A scanner, not a parser: anything below single-digit MB/s means something has
        // become quadratic. The extension runs this inside a process with about 120 MB
        // and a share sheet the user is looking at.
        Assert.True(perSecond > 1.0, $"HTML extraction ran at {perSecond:0.##} MB/s");
        Assert.True(clock.Elapsed.TotalMilliseconds < 2000, $"one page took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void ComposingAMessageFromALongPageIsFastEnoughToBeInvisible()
    {
        string body = ShareText.FromHtml(Article(2000));
        var payload = new SharePayload
        {
            Id = ShareIds.New(),
            Prompt = "Summarize this.",
            Items = { ShareItem.ForPage("https://example.com/a", "Small models are winning", body, "") },
        };
        _ = ShareComposer.Compose(payload);

        var clock = Stopwatch.StartNew();
        ShareComposition composed = ShareComposer.Compose(payload);
        clock.Stop();

        Console.WriteLine(
            $"share: composed {composed.Message.Length / 1024.0:0.#} kB from a {body.Length / 1024.0:0.#} kB page "
            + $"in {clock.Elapsed.TotalMilliseconds:0.##} ms");
        Assert.True(clock.Elapsed.TotalMilliseconds < 500, $"composing took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task AShareWithFilesReachesTheComposerWithoutTheUserWaiting()
    {
        AgentAppHost host = Start();
        var store = new ShareEnvelopeStore(Inbox);
        ShareEnvelopeWriter writer = store.BeginWrite();

        // A page, four photos and a document — the heaviest share anyone makes by hand,
        // and enough bytes that the file copy is actually part of what is measured
        // rather than lost in the noise of the rest.
        var items = new List<ShareItem>
        {
            ShareItem.ForPage("https://example.com/a", "A page", ShareText.FromHtml(Article(400)), ""),
        };
        long bytes = 0;
        for (int i = 0; i < 4; i++)
        {
            (string absolute, string relative) = writer.ReserveFile($"IMG_000{i}.png");
            byte[] png = MediaFixtures.Jpeg(Noise(1200 * 900 * 3, i), 1200, 900);
            await File.WriteAllBytesAsync(absolute, png);
            bytes += png.Length;
            items.Add(ShareItem.ForFile(relative, $"IMG_000{i}.jpg", png.Length, "public.jpeg", "image/jpeg"));
        }
        writer.Commit(new SharePayload { Prompt = "What is in these?", Items = items });

        var clock = Stopwatch.StartNew();
        int imported = await host.DrainSharedInboxAsync();
        clock.Stop();

        Assert.Equal(1, imported);
        Console.WriteLine(
            $"share: imported a page and 4 photos ({bytes / 1024.0 / 1024:0.##} MB) "
            + $"in {clock.Elapsed.TotalMilliseconds:0} ms");

        // The gap between tapping Ask and seeing the composer fill. It lands in front of
        // a model load that takes twenty seconds, so anything under a second is
        // invisible; the floor is set where a REGRESSION would show, not where the
        // experience would suffer.
        Assert.True(clock.Elapsed.TotalSeconds < 10,
            $"importing one share took {clock.Elapsed.TotalSeconds:0.#} s");
        Assert.Equal(4, host.Shares.Peek()!.Attachments.Count);
    }

    /// <summary>Incompressible pixels, so a fixture's size is the size it says.</summary>
    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        // A fixed linear congruential sequence: deterministic, so a rerun measures the
        // same work, and unpatterned, so the JPEG encoder cannot make it disappear.
        uint state = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }

    // =====================================================================================
    // tokens, which is the cost that actually matters on a phone
    // =====================================================================================

    [Fact]
    public void ASharedArticleCostsALimitedAmountOfContextHoweverLongItIs()
    {
        // The number that matters. A phone prefills in 1024-token chunks because a
        // bigger chunk got the process killed by jetsam, and a shared page is routinely
        // ten times anything a person would type. Whatever the page, the message has to
        // land inside a budget the device can actually prefill.
        foreach (int paragraphs in new[] { 50, 500, 5000, 20000 })
        {
            string body = ShareText.FromHtml(Article(paragraphs));
            var payload = new SharePayload
            {
                Id = ShareIds.New(),
                Prompt = "Summarize this.",
                Items = { ShareItem.ForPage("https://example.com/a", "A page", body, "") },
            };

            ShareComposition composed = ShareComposer.Compose(payload);
            int budget = ShareCompositionOptions.Default.MaxTotalChars;
            // A rough four characters per token, which is what an English tokenizer
            // averages; the point is the order of magnitude, not the exact count.
            Console.WriteLine(
                $"share: a {body.Length / 1024.0:0} kB page → {composed.Message.Length / 1024.0:0.#} kB "
                + $"(~{composed.Message.Length / 4:N0} tokens)");

            Assert.True(composed.Message.Length <= budget + 4096,
                $"a {body.Length}-character page produced a {composed.Message.Length}-character message, "
                + $"past the {budget}-character budget");
        }
    }

    [Fact]
    public void AShortShareIsNotPaddedOut()
    {
        // The other half of the same contract: a two-line note must not turn into a
        // kilobyte of scaffolding. Every character of framing is a token spent on
        // saying what the model is looking at rather than on the thing itself.
        var payload = new SharePayload
        {
            Id = ShareIds.New(),
            Prompt = "What does this mean?",
            Items = { ShareItem.ForText("The meeting is moved to Thursday at 4.") },
        };

        ShareComposition composed = ShareComposer.Compose(payload);
        int overhead = composed.Message.Length - payload.Prompt.Length - payload.Items[0].Text.Length;

        Console.WriteLine($"share: framing costs {overhead} characters around a short note");
        Assert.True(overhead < 120, $"the framing added {overhead} characters to a one-line share");
    }

    [Fact]
    public void ExtractionDropsWhatCostsTokensAndKeepsWhatDoesNot()
    {
        // The reason FromHtml exists. A rendered page carries a stylesheet, a tracking
        // script and a wall of tags, and every character of that is a token the phone
        // spends before reaching a sentence anyone wrote.
        //
        // The assertion is on WHAT is dropped rather than on a percentage: the ratio
        // depends entirely on how tag-dense the page is, and a fixture's ratio would
        // pin the fixture rather than the behaviour. The 8 kB of style and script in
        // this one being gone is a fact about the code.
        string html = Article(300);
        string text = ShareText.FromHtml(html);

        Console.WriteLine(
            $"share: extraction turned {html.Length / 1024.0:0.#} kB of HTML into "
            + $"{text.Length / 1024.0:0.#} kB of text ({100.0 * text.Length / html.Length:0.#}% kept)");

        Assert.True(text.Length < html.Length, "extraction did not shrink the page at all");
        // The stylesheet and the tracking script, neither of which is text.
        Assert.DoesNotContain("tracking", text, StringComparison.Ordinal);
        Assert.DoesNotContain("margin:0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
        // ...and the article, which is.
        Assert.Contains("Paragraph 0 of the article", text, StringComparison.Ordinal);
        // Entities decoded, so the model reads a character rather than its name.
        Assert.DoesNotContain("&amp;", text, StringComparison.Ordinal);
    }
}
