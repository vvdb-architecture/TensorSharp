// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.Json;
using TensorAgent.Maui.Hosting;

namespace TensorAgent.Maui;

/// <summary>
/// The chat page: a WebView filling the page with the loopback-served Web UI,
/// under a one-line native bar that shows which backend was picked and whether
/// the native engine link is alive.
/// </summary>
public sealed class MainPage : ContentPage
{
    private static readonly Color BarBackground = Color.FromArgb("#0b1220");
    private static readonly Color BarText = Color.FromArgb("#c9d3ea");
    private static readonly Color BarError = Color.FromArgb("#ff8a8a");

    private readonly LoopbackWebHost _host;
    private readonly Label _status;
    private readonly WebView _webView;
    private Platforms.iOS.Dictation? _dictation;

    /// <summary>
    /// True once the page has told us it finished loading, and false again from the
    /// moment it is reloaded. It is what makes "the page did not answer" mean something:
    /// before the first `ready`, silence is normal.
    /// </summary>
    private bool _pageReady;

    public MainPage(LoopbackWebHost host)
    {
        _host = host;
        Title = "TensorAgent";
        BackgroundColor = BarBackground;
        Shell.SetNavBarIsVisible(this, false);
        // Keep the status bar and the home indicator out of the WebView; the
        // page's own background paints the insets in the Web UI's dark colour.
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.All;

        _status = new Label
        {
            FontSize = 12,
            TextColor = BarText,
            BackgroundColor = BarBackground,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
            Text = "Starting the engine…",
        };

        _webView = new WebView
        {
            BackgroundColor = BarBackground,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };

        var grid = new Grid
        {
            BackgroundColor = BarBackground,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };
        // No native row above the page. It used to carry the status and chips for
        // Chats / Models / Settings, which is a second row of chrome stacked on the
        // page's own -- the one thing a small screen cannot afford. The page has a
        // menu that asks for the same routes through OnPageEvent.
        grid.Add(new ContentView { IsVisible = false, HeightRequest = 0 }, 0, 0);
        grid.Add(_webView, 0, 1);
        grid.Add(BuildAttachmentBar(), 0, 2);
        Content = grid;

        // A file the model's code produced is reached by a link the page renders.
        // A WebView cannot save one: the route serves it as an attachment (it is
        // program-written content and must never render inline), and a WKWebView
        // with no download delegate simply does nothing when you tap it. Hand it to
        // the share sheet instead, which is what "save it to my phone" actually
        // means -- Save to Files, Mail, AirDrop.
        _webView.Navigating += OnNavigatingShareArtifact;
#if DEBUG
        _webView.Navigated += OnNavigatedSendDemoPrompt;
#endif
        StartHost();
    }

    /// <summary>
    /// Intercept a tap on a generated file and share it rather than navigating.
    ///
    /// <para>
    /// The link is <c>/api/code/artifacts/{runId}/{path}</c>. Resolving it through the
    /// store rather than fetching the URL keeps the confinement check in one place --
    /// the path segment was chosen by a program a model wrote -- and avoids a loopback
    /// round trip for a file already on disk.
    /// </para>
    /// </summary>
    private async void OnNavigatingShareArtifact(object? sender, WebNavigatingEventArgs e)
    {
        const string prefix = "/api/code/artifacts/";
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? uri) ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        string rest = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            return;   // a run listing, not a file: let it navigate

        e.Cancel = true;
        string runId = rest[..slash];
        string relative = rest[(slash + 1)..];

        try
        {
            if (!_host.App.Artifacts.TryResolve(runId, relative, out string? full, out string? error))
            {
                await DisplayAlert("Cannot open", error ?? "That file is no longer available.", "OK");
                return;
            }

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = Path.GetFileName(relative),
                File = new ShareFile(full!),
            });
        }
        catch (Exception ex)
        {
            // A share sheet that fails must not take the chat down with it.
            Console.WriteLine($"TensorAgent: share of {relative} failed: {ex.Message}");
            await DisplayAlert("Cannot share", ex.Message, "OK");
        }
    }

#if DEBUG
    /// <summary>
    /// Device E2E hook (Debug builds only): start downloading the catalog entry named
    /// by TENSORAGENT_DOWNLOAD and log what the transfer does, then stop it after
    /// TENSORAGENT_DOWNLOAD_SECONDS (60 by default).
    ///
    /// <para>
    /// The half of a download that only a real device can answer is what happens when
    /// the user leaves the app: <see cref="Platforms.iOS.BackgroundDownloads"/> takes a
    /// background-task assertion, and an assertion begun twice or never ended is a
    /// termination rather than a warning. Neither a simulator nor a unit test is ever
    /// suspended, so the only way to see it is to start a transfer on the phone,
    /// foreground something else, and read this log. It is bounded on purpose — the
    /// point is the first megabytes and the state changes around them, not five
    /// gigabytes of someone's data allowance.
    /// </para>
    /// </summary>
    private void DownloadIfAsked()
    {
        string? id = Environment.GetEnvironmentVariable("TENSORAGENT_DOWNLOAD");
        if (string.IsNullOrWhiteSpace(id))
            return;
        if (Core.Catalog.ModelCatalog.Find(id) is not { } model)
        {
            Console.WriteLine($"TensorAgent: download FAIL no catalog entry called '{id}'");
            return;
        }

        int seconds = int.TryParse(Environment.GetEnvironmentVariable("TENSORAGENT_DOWNLOAD_SECONDS"), out int s) ? s : 60;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReported = -1;
        _host.App.Downloads.BusyChanged += busy =>
            Console.WriteLine($"TensorAgent: download busy={busy} at {clock.Elapsed.TotalSeconds:0.0}s");
        _host.App.Downloads.Changed += status =>
        {
            if (!string.Equals(status.ModelId, model.Id, StringComparison.Ordinal))
                return;
            // One line a megabyte: enough to see it moving while the app is in the
            // background, few enough to read.
            long megabytes = status.Progress.BytesReceived / (1024 * 1024);
            if (status.IsRunning && megabytes == lastReported)
                return;
            lastReported = megabytes;
            Console.WriteLine(
                $"TensorAgent: download {status.State} {megabytes} MB of "
                + $"{status.Progress.TotalBytes / (1024 * 1024)} MB at {clock.Elapsed.TotalSeconds:0.0}s"
                + (status.Error is { Length: > 0 } error ? " · " + error : string.Empty));
        };

        _host.App.Downloads.Start(model, Core.Downloads.ModelDownloadManager.OptionalRolesFor(false));
        Console.WriteLine($"TensorAgent: download started {model.Id}, stopping after {seconds}s");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Console.WriteLine("TensorAgent: download cancelling after the budget");
            _host.App.Downloads.Cancel(model.Id);
        });
    }

    /// <summary>
    /// Device E2E hook (Debug builds only, on by default): post a large multipart body
    /// to this app's own <c>/api/upload</c> and report whether the file came out the
    /// other end.
    ///
    /// <para>
    /// It is here rather than in the unit suite because the failure it guards was
    /// invisible everywhere else. The hand-written multipart parser dropped a whole
    /// buffer when a single read returned exactly its 65536 bytes, and which read sizes
    /// a body arrives in is decided by the HTTP client and the socket: the managed
    /// listener hands back the leftover of its own 8 kB header read first, so a browser's
    /// <c>fetch</c> upload never reached the fault, while iOS's NSURLSession — which
    /// writes the whole body as one <c>NSData</c> — reached it every time. Every photo
    /// picked on the phone was answered "no file was uploaded"; nothing on a development
    /// machine reproduced it. So the probe runs the real client, on the real device,
    /// against the real route, and says so in the launch log.
    /// </para>
    /// </summary>
    private async Task RunUploadProbeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TENSORAGENT_SKIP_UPLOAD_CHECK"), "1", StringComparison.Ordinal))
            return;
        try
        {
            // Comfortably past the parser's 65536-byte buffer, so at least one read has
            // to fill it completely.
            byte[] png = SolidPng(700, 700);
            using var form = new MultipartFormDataContent();
            using var part = new ByteArrayContent(png);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            // No extension, exactly as the photo picker hands one over.
            form.Add(part, "file", "IMG_PROBE");

            using var client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
            client.DefaultRequestHeaders.Add("Cookie", $"tensoragent_token={_host.Token}");
            using HttpResponseMessage response = await client.PostAsync("/api/upload", form);
            string payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"TensorAgent: uploadcheck FAIL {png.Length} bytes refused: {payload}");
                return;
            }

            using JsonDocument answer = JsonDocument.Parse(payload);
            string? file = answer.RootElement.TryGetProperty("file", out JsonElement f) ? f.GetString() : null;
            string? media = answer.RootElement.TryGetProperty("mediaType", out JsonElement m) ? m.GetString() : null;
            Console.WriteLine($"TensorAgent: uploadcheck ok {png.Length} bytes -> {file} ({media})");
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: uploadcheck FAIL " + ex.Message);
        }
    }

    /// <summary>A valid PNG of a given size, big enough that its IDAT cannot be tiny.</summary>
    private static byte[] SolidPng(int width, int height)
    {
        // Written by hand rather than through a platform encoder, so the probe tests the
        // upload path and not ImageIO. Noise rather than a flat colour: a solid image
        // deflates to almost nothing, and the point here is the number of bytes.
        var raw = new MemoryStream();
        var random = new Random(20260903);
        var row = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            random.NextBytes(row);
            raw.Write(row);
        }

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new MemoryStream();
        header.Write(BigEndian(width));
        header.Write(BigEndian(height));
        header.Write(new byte[] { 8, 2, 0, 0, 0 });
        WriteChunk(png, "IHDR"u8.ToArray(), header.ToArray());

        var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw.ToArray());
        WriteChunk(png, "IDAT"u8.ToArray(), deflated.ToArray());
        WriteChunk(png, "IEND"u8.ToArray(), Array.Empty<byte>());
        return png.ToArray();

        static byte[] BigEndian(int value) =>
            new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        static void WriteChunk(Stream target, byte[] type, byte[] data)
        {
            target.Write(BigEndian(data.Length));
            target.Write(type);
            target.Write(data);
            var crc = new MemoryStream();
            crc.Write(type);
            crc.Write(data);
            target.Write(BigEndian(unchecked((int)Crc32(crc.ToArray()))));
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
            }
            return crc ^ 0xFFFFFFFF;
        }
    }

    /// <summary>
    /// Device E2E hook (Debug builds only): turn the network switch on the way the
    /// Settings page turns it on, run <c>curl</c> the way the model runs it, and report
    /// what the shell said — then put the switch back where it was.
    ///
    /// <para>
    /// This is the reported bug end to end and it can only be answered here: the switch
    /// and the shell agree perfectly in a unit test, and the thing that was broken was
    /// that nothing carried the change from the settings file into the objects a launch
    /// had already built. Off by default because it reaches the internet.
    /// </para>
    /// </summary>
    private void RunNetworkProbe()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TENSORAGENT_NETWORK_CHECK"), "1", StringComparison.Ordinal))
            return;

        _ = Task.Run(() =>
        {
            Core.Settings.AppSettings original = _host.App.Settings.Load();
            try
            {
                // BOTH directions, in one launch, because half of it proves nothing. A
                // container whose switch was already on would pass a check that only
                // turned it on, while the bug -- a change that reaches the settings file
                // and nothing else -- would be untouched.
                Console.WriteLine($"TensorAgent: netcheck starting from allowNetwork={original.AllowNetwork}");

                Set(false);
                Console.WriteLine("TensorAgent: netcheck off · " + RunShell(
                    "curl -s https://example.com", Core.Sandbox.ExecutionPolicy.NetworkDisabledMessage,
                    expectFailure: true));

                Set(true);
                Console.WriteLine("TensorAgent: netcheck on · " + RunShell(
                    "curl -s https://example.com", "Example Domain"));
                Console.WriteLine("TensorAgent: netcheck on python · " + RunShell(
                    "python3 -c \"import urllib.request as u; print('got', len(u.urlopen('https://example.com').read()))\"",
                    "got "));
            }
            catch (Exception ex)
            {
                Console.WriteLine("TensorAgent: netcheck FAIL " + ex.Message);
            }
            finally
            {
                _host.App.Settings.Save(original);
                _host.App.ApplySettings(original);
                Console.WriteLine($"TensorAgent: netcheck restored to allowNetwork={original.AllowNetwork} · {_host.App.DescribeEngine()}");
            }
        });

        void Set(bool allow)
        {
            Core.Settings.AppSettings settings = _host.App.Settings.Load();
            settings.AllowNetwork = allow;
            _host.App.Settings.Save(settings);
            // Exactly what the Settings page does, and the line that used to be missing.
            _host.App.ApplySettings(settings);
            Console.WriteLine($"TensorAgent: netcheck switch -> {allow} · {_host.App.DescribeEngine()}");
        }
    }

    /// <summary>
    /// Run one command through the app's REAL execution terms — the ones the model's
    /// shell tool is launched with — and describe what came back in one line.
    /// </summary>
    private string RunShell(string command, string? expected, bool expectFailure = false)
    {
        string root = Path.Combine(_host.App.Paths.ScratchDirectory, "probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            TensorSharp.AgentHost.CodeExec.ConfinedResult result =
                ((TensorSharp.AgentHost.CodeExec.IShellBackend)_host.App.Backend).Run(
                    new TensorSharp.AgentHost.CodeExec.ShellLaunch
                    {
                        // Argv rather than Command: a Command launch is a conversation's
                        // shell and is required to carry the session whose directory and
                        // exports it restores. A probe has neither.
                        Argv = new[] { "sh", "-c", command },
                        WorkingDirectory = root,
                        WriteDirectory = root,
                        ReadOnlyDirectory = root,
                        // The point of the probe: whatever the switch currently says.
                        AllowNetwork = _host.App.CodeExec.AllowNetwork,
                        Timeout = TimeSpan.FromSeconds(30),
                    });

            string output = (result.Stdout + result.Stderr).Trim();
            // With the network off, being refused IS the right answer, and saying the
            // refusal is what the model reads. Both halves have to be checked or the
            // probe passes on a build where nothing is enforced at all.
            bool said = expected is null || output.Contains(expected, StringComparison.Ordinal);
            bool ok = expectFailure ? !result.Ok && said : result.Ok && said;
            string detail = output.Length > 200 ? output[..200] + "…" : output;
            return $"{(ok ? "ok" : "FAIL")} `{command.Split('\n')[0]}` exit={result.ExitCode} · {detail.Replace('\n', ' ')}";
        }
        catch (Exception ex)
        {
            return "FAIL " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { /* scratch */ }
        }
    }

    /// <summary>
    /// Simulator E2E hook (Debug builds only): when the app is launched with
    /// TENSORAGENT_DEMO_PROMPT set (simctl passes it as
    /// SIMCTL_CHILD_TENSORAGENT_DEMO_PROMPT), type that prompt into the Web UI
    /// and send it once the page has loaded. simctl has no way to tap or type
    /// into the WebView, so this is how scripts/run-sim.sh drives a visible
    /// round trip for a screenshot without modifying index.html.
    /// </summary>
    private async void OnNavigatedSendDemoPrompt(object? sender, WebNavigatedEventArgs e)
    {
        _webView.Navigated -= OnNavigatedSendDemoPrompt;
        if (e.Result != WebNavigationResult.Success)
            return;

        await RunUiCheckAsync();

        // Screenshot hook: neither simctl nor devicectl can tap, so the menu -- the one
        // surface all of this app's navigation lives on -- can otherwise never appear in
        // a picture of the running app.
        if (string.Equals(Environment.GetEnvironmentVariable("TENSORAGENT_OPEN_MENU"), "1", StringComparison.Ordinal))
            await Tell("openMenu");

        string? prompt = Environment.GetEnvironmentVariable("TENSORAGENT_DEMO_PROMPT");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            // sendMessage() refuses while the page's currentLoadedModel is null, so this
            // has to wait for a model rather than for a fixed delay. It used to sleep
            // 1500 ms, which was enough when a model was already loaded at startup and
            // not when one is being loaded concurrently -- the prompt fired first and
            // was silently refused, which is exactly the bug being tested for, produced
            // by the harness instead of by the product. Polling refreshModel() is both
            // the correct wait and a direct exercise of the fix.
            bool ready = false;
            for (int i = 0; i < 60 && !ready; i++)
            {
                await Task.Delay(1000);
                await _webView.EvaluateJavaScriptAsync(
                    "window.TensorAgent && window.TensorAgent.refreshModel ? window.TensorAgent.refreshModel() : 0");
                // hasModel() is the synchronous half: refreshModel starts the fetch and
                // this reports what the page ended up believing.
                string? answer = await _webView.EvaluateJavaScriptAsync(
                    "window.TensorAgent && window.TensorAgent.hasModel ? window.TensorAgent.hasModel() : false");
                ready = answer is not null && answer.Contains("true", StringComparison.OrdinalIgnoreCase);
            }
            Console.WriteLine($"TensorAgent: demo prompt sees a loaded model = {ready}");
            if (!ready)
                return;

            // Through the page's own bridge, not through the desktop page's globals.
            // This used to write into #message-input and call sendMessage(), which are
            // TensorSharp.Server's page; the app has had its own since, so the hook was
            // typing into an element that does not exist and the "demo prompt sent"
            // line was printed for a prompt nobody received.
            string js =
                "document.getElementById('text').value = " + JsonSerializer.Serialize(prompt) + ";"
                + "window.TensorAgent.send(); true";
            await _webView.EvaluateJavaScriptAsync(js);
            Console.WriteLine("TensorAgent: demo prompt sent through the Web UI: " + prompt);

            // And, if asked, walk away from it while it is being answered.
            await RunNavigationProbeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: demo prompt failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Device E2E hook (Debug builds only): send a prompt, LEAVE the chat while the
    /// model is answering, come back, and report whether the answer carried on.
    ///
    /// <para>
    /// This is the reported bug, and the only place it can be answered. It is not a
    /// question about the server — a unit test can drop a reader and watch the frames
    /// keep coming — but about what iOS does to a WKWebView whose view leaves the
    /// window, which is to suspend its content process mid-stream. Nothing off-device
    /// reproduces that. So the probe does what the user did: opens the model list for
    /// twelve seconds while the model is working, and prints the turn's frame count
    /// before and after, then the length of the answer the page ends up showing.
    /// </para>
    /// <para>
    /// The frame count rising while the chat was off-screen is the whole claim. The
    /// answer being on the page afterwards is the other half: that the page found its
    /// way back to the turn rather than showing the half sentence it walked away from.
    /// </para>
    /// </summary>
    private async Task RunNavigationProbeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TENSORAGENT_NAV_CHECK"), "1", StringComparison.Ordinal))
            return;

        Core.Sessions.ChatTurnManager turns = _host.App.Turns;
        int away = int.TryParse(Environment.GetEnvironmentVariable("TENSORAGENT_NAV_SECONDS"), out int seconds) ? seconds : 15;
        try
        {
            // Not merely "a turn exists" but "it is producing". A prompt of any length
            // spends its first stretch in prefill, which is minutes on a simulator with
            // no Metal, and leaving during that would compare nothing to nothing.
            if (!await Until(() => turns.TotalFrames > 0, TimeSpan.FromMinutes(6)))
            {
                Console.WriteLine($"TensorAgent: navcheck FAIL nothing was generated · {turns.Describe()}");
                return;
            }

            int framesBefore = turns.TotalFrames;
            Console.WriteLine($"TensorAgent: navcheck leaving the chat with a turn running · {turns.Describe()}");

            await AppShell.OpenAsync("models");
            await Task.Delay(TimeSpan.FromSeconds(away));
            int framesAway = turns.TotalFrames;
            bool stillRunning = turns.IsBusy;
            Console.WriteLine(
                $"TensorAgent: navcheck {(framesAway > framesBefore ? "ok" : "FAIL")} away for {away}s: "
                + $"frames {framesBefore} -> {framesAway}, still generating={stillRunning} · {turns.Describe()}");

            await AppShell.BackToChatAsync();
            // The page has to be asked to resume, ask the host what is running, open a
            // stream and replay it. Generous, because none of that is instant on a
            // WebView whose content process has just been woken up.
            int shown = 0;
            await Until(async () => (shown = await AnswerLengthAsync()) > 0, TimeSpan.FromSeconds(30));
            Console.WriteLine(
                $"TensorAgent: navcheck {(shown > 0 ? "ok" : "FAIL")} back in the chat, "
                + $"the answer on screen is {shown} characters · {turns.Describe()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: navcheck FAIL " + ex.Message);
        }
    }

    /// <summary>How many characters the page is showing in the last assistant bubble.</summary>
    private async Task<int> AnswerLengthAsync()
    {
        string? shown = await _webView.EvaluateJavaScriptAsync(
            "(function(){var b=document.querySelectorAll('.turn.bot .bubble');"
            + "return b.length ? String(b[b.length-1].textContent.length) : '0';})()");
        return int.TryParse((shown ?? string.Empty).Trim('"'), out int length) ? length : 0;
    }

    /// <summary>Poll until it is true, or give up. False means it never became true.</summary>
    private static Task<bool> Until(Func<bool> condition, TimeSpan budget)
        => Until(() => Task.FromResult(condition()), budget);

    private static async Task<bool> Until(Func<Task<bool>> condition, TimeSpan budget)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < budget)
        {
            if (await condition())
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Drive the composer's own gestures inside the real WebView and report what
    /// happened, one <c>uicheck</c> line per assertion.
    ///
    /// <para>
    /// The page's behaviour is the half of this app no unit test can reach: it is
    /// JavaScript in WKWebView, and everything interesting about it — whether a long
    /// press on the message box really turns the composer into the hold-to-talk button,
    /// whether the button that comes back really returns you to typing, whether the
    /// live-progress panel is styled at all — is a question about layout and events on
    /// a real engine. <c>simctl</c> cannot tap, so the events are synthesised here and
    /// the answers go to stdout beside the interpreter self-test, where
    /// <c>verify-sim.sh</c> asserts on them.
    /// </para>
    /// </summary>
    private async Task RunUiCheckAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TENSORAGENT_UI_CHECK"), "1", StringComparison.Ordinal))
            return;

        try
        {
            await _webView.EvaluateJavaScriptAsync(UiCheckScript);
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                string? raw = await _webView.EvaluateJavaScriptAsync("window.__uicheck || ''");
                string result = (raw ?? string.Empty).Trim('"').Replace("\\\"", "\"");
                if (result.Length == 0)
                    continue;
                foreach (string check in result.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine("TensorAgent: uicheck " + check.Replace("=", " ").Trim());
                return;
            }
            Console.WriteLine("TensorAgent: uicheck FAIL the page never reported (the script did not finish)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: uicheck FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// The check itself. Written as one self-contained expression because that is what
    /// <c>evaluateJavaScript</c> takes, and it parks its answer on
    /// <c>window.__uicheck</c> because that call cannot await a promise and every
    /// gesture here needs real time to pass.
    /// </summary>
    private const string UiCheckScript =
        // ONE LINE, and no `//` comments inside it. MAUI's EvaluateJavaScriptAsync
        // flattens the script before handing it to WKWebView, so a line comment
        // swallows everything after it and the page answers "SyntaxError: Unexpected
        // EOF" -- which is what it did the first time this was written as a block.
        "(function(){var out=[];"
        + "function add(n,ok,d){out.push(n+'='+(ok?'ok':'FAIL '+(d||'')));}"
        + "function shown(e){return !!e&&getComputedStyle(e).display!=='none';}"
        + "var text=document.getElementById('text'),hold=document.getElementById('hold'),"
        + "back=document.getElementById('abc'),wrap=document.getElementById('textwrap');"
        + "function press(t){text.dispatchEvent(new PointerEvent(t,{bubbles:true,cancelable:true,"
        + "clientX:100,clientY:100,pointerId:1,pointerType:'touch'}));}"
        // NOT calling nativeReady() here, deliberately. It used to, and that is exactly
        // why this check passed on a phone where holding the box answered "Voice input
        // is only available in the app": the check handed the page the fact it was
        // supposed to be testing. Now the hold only switches if the app really told the
        // page it was the app, so this fails when that handshake is broken.

        + "add('voice-switch-gone',document.getElementById('voice')===null,'the composer still carries a Voice switch');"
        + "add('reasoning-is-a-setting',document.getElementById('think')===null,'the composer still carries a Reasoning switch');"
        + "add('skills-moved-to-the-menu',!document.getElementById('skills-btn')"
        + "&&!!document.querySelector('#nav-sheet [data-sheet=\"skills-sheet\"]'),'no Skills item in the menu');"
        + "var strip=document.getElementById('activity'),row=document.getElementById('row');"
        + "add('activity-above-the-box',!!strip&&!!row&&strip.parentNode===row.parentNode"
        + "&&(strip.compareDocumentPosition(row)&Node.DOCUMENT_POSITION_FOLLOWING)!==0"
        + "&&getComputedStyle(strip).display==='none',"
        + "'the activity strip is missing, is not above the message box, or is showing with nothing to say');"
        + "press('pointerdown');setTimeout(function(){press('pointerup');setTimeout(function(){"
        + "add('a-tap-still-types',!document.body.classList.contains('voice'),'a short press switched to voice mode');"
        + "press('pointerdown');setTimeout(function(){"
        + "add('holding-the-box-gives-hold-to-talk',"
        + "document.body.classList.contains('voice')&&shown(hold)&&!shown(wrap),"
        + "'voice='+document.body.classList.contains('voice')+' hold='+shown(hold)+' box='+shown(wrap));"
        + "back.click();setTimeout(function(){"
        + "add('the-keyboard-button-returns',!document.body.classList.contains('voice')&&shown(wrap),'still in voice mode');"
        // The main menu, measured rather than asserted about. "Opens from the left" is a
        // rectangle -- flush with the left edge, narrower than the window, as tall as it
        // -- and a bottom sheet that had merely been renamed would pass every check that
        // only looked at class names.
        + "document.getElementById('menu').click();setTimeout(function(){"
        + "var menu=document.getElementById('nav-sheet'),box=menu.getBoundingClientRect();"
        + "add('the-menu-comes-from-the-left',menu.classList.contains('drawer')&&box.left<1"
        + "&&box.width<window.innerWidth*0.95&&box.height>window.innerHeight*0.9,"
        + "'left='+Math.round(box.left)+' width='+Math.round(box.width)+' of '+window.innerWidth"
        + "+' height='+Math.round(box.height)+' of '+window.innerHeight);"
        + "var chats=document.getElementById('nav-chats');"
        + "add('the-menu-lists-the-saved-chats',!!chats&&menu.contains(chats)"
        + "&&(chats.querySelectorAll('.navchat').length>0||!!chats.querySelector('.navempty')),"
        + "'the menu has no chat list');"
        + "add('a-turn-can-be-taken-back-up',typeof window.TensorAgent.resumeTurn==='function'"
        + "&&typeof window.TensorAgent.openConversation==='function','the bridge cannot resume a turn');"
        + "var master=document.getElementById('skills-master');"
        + "add('skills-have-a-master-switch',!!master&&master.type==='checkbox'"
        + "&&typeof window.TensorAgent.skillsEnabled==='function',"
        + "'the skills sheet has no on/off switch above its list');"
        + "document.getElementById('sheet-bg').click();"
        + "window.__uicheck=out.join('|');},420);},120);},700);},140);},140);})(); true";
#endif

    /// <summary>
    /// Re-sync the page's idea of which model is loaded, every time the chat is shown.
    ///
    /// <para>
    /// The Web UI reads the loaded model ONCE, at page load, into its own
    /// `currentLoadedModel`. That is correct for the desktop server, where the model
    /// cannot change while the page is open. In the app it can: the user picks one in
    /// the Models list. Without this the header still said "No model configured" after
    /// a model had been chosen AND loaded, and the page's own send guard refused to
    /// send anything -- so the prompt never reached a server that was ready to answer
    /// it. Reported from a phone, twice: repointing the engine was necessary and not
    /// sufficient, because the half the user actually looks at had not been told.
    /// </para>
    /// <para>
    /// Done on appearing rather than as a message from the Models page, so it is right
    /// however the chat is reached -- the flyout, the back gesture, or the automatic
    /// return after choosing a model. It costs one request to a loopback server.
    /// </para>
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            // First, because everything after it is pointless if the answer is no. iOS
            // suspends a WKWebView's content process the moment its view leaves the
            // window -- which every other screen in this app does -- and a suspended
            // process holding a phone with a multi-gigabyte model resident is the first
            // thing the system reclaims under pressure. When that happens the WebView
            // comes back BLANK and stays blank: MAUI does not implement
            // webViewWebContentProcessDidTerminate, so nothing reloads it. Asking the
            // page whether it is still there costs one round trip and is the difference
            // between a chat and a black rectangle.
            if (_pageReady && !await PageIsAliveAsync())
            {
                Console.WriteLine("TensorAgent: the page stopped answering; reloading it");
                _pageReady = false;
                _webView.Source = new UrlWebViewSource { Url = _host.EntryUrl };
                return;
            }

            // Guarded in JS as well: on the very first appearance the page may not have
            // loaded yet, and there is nothing to refresh until it has.
            await Tell("nativeReady");
            await Tell("refreshModel");
            // Settings is a native page and this one outlives it, so a choice made
            // there — "Show reasoning by default", the dictation language — has to be
            // re-read on the way back. It used to be a switch the user could see under
            // the composer; now the only place it lives is the settings file.
            await Tell("refreshSettings");
            // A chat may have been renamed or deleted on the Chats page while this one
            // was covered, and the menu lists them.
            await Tell("refreshChats");
            // And the answer the model went on writing while this page was not being
            // shown -- and therefore not being read. See ChatTurnManager.
            await Tell("resumeTurn");
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: refresh on appearing failed: " + ex.Message);
        }
    }

    /// <summary>Call one method on the page's bridge, if the page has one yet.</summary>
    private Task<string?> Tell(string method) => _webView.EvaluateJavaScriptAsync(
        $"window.TensorAgent && window.TensorAgent.{method} ? window.TensorAgent.{method}() : false");

    /// <summary>
    /// Whether the page is still running.
    ///
    /// <para>
    /// Only ever asked of a page that has ALREADY said it was ready, because a page
    /// which has not finished loading answers exactly the same "no" as one whose content
    /// process was killed — and reloading on the first answer would make a cold launch
    /// reload itself forever.
    /// </para>
    /// </summary>
    private async Task<bool> PageIsAliveAsync()
    {
        try
        {
            string? answer = await _webView.EvaluateJavaScriptAsync("window.TensorAgent ? 'yes' : 'no'");
            return answer is not null && answer.Contains("yes", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Nothing, and deliberately so.
    ///
    /// <para>
    /// Two rows of native chrome used to live around this WebView: a status line with
    /// Chats / Models / Settings chips above it, and a row of Photo / Camera / Video /
    /// File / Speak chips below. Both said what the page already says — it has a menu
    /// and it has a "+" — while spending the one thing a 6.9-inch screen cannot spare.
    /// The page asks for all of it through <see cref="OnPageEvent"/> now.
    /// </para>
    /// </summary>
    private static View BuildAttachmentBar() => new ContentView { IsVisible = false, HeightRequest = 0 };

    private enum MediaSource { Library, Camera, Video, File }

    /// <summary>
    /// Pick something and give it to the chat service, then tell the page it now has an
    /// attachment.
    ///
    /// <para>
    /// Handed to the service DIRECTLY rather than posted to <c>/api/upload</c>, which is
    /// what this used to do and what produced "IMG_0004.jpeg could not be attached: no
    /// file was uploaded" for every photo. The loop went managed stream -> multipart
    /// body -> iOS's own NSURLSession HTTP stack -> a loopback socket -> a hand-written
    /// multipart parser, to move a file this process already had, to a server inside
    /// this same process; a body that arrives empty anywhere along that path produces
    /// exactly that message and names none of the five places it could have happened.
    /// The route still exists for the page's own paperclip, which is a browser and has
    /// no other way in. Native code does not need one.
    /// </para>
    /// <para>
    /// The pick is spooled to a file first, because a length is not optional — the
    /// service is given one, and the picker's stream is not always able to say. It is
    /// also where an empty pick becomes a sentence about an empty pick instead of a
    /// zero-byte upload the model is later asked to look at.
    /// </para>
    /// </summary>
    private async Task AttachAsync(MediaSource source)
    {
        FileResult? picked = source switch
        {
            MediaSource.Library => await MediaPicker.Default.PickPhotoAsync(),
            MediaSource.Camera when MediaPicker.Default.IsCaptureSupported => await MediaPicker.Default.CapturePhotoAsync(),
            MediaSource.Camera => throw new NotSupportedException("This device has no camera available to the app."),
            MediaSource.Video => await MediaPicker.Default.PickVideoAsync(),
            MediaSource.File => await FilePicker.Default.PickAsync(),
            _ => null,
        };
        if (picked is null)
            return;

        string shown = string.IsNullOrWhiteSpace(picked.FileName) ? "That file" : picked.FileName;
        string spooled = Path.Combine(Path.GetTempPath(), "attach-" + Guid.NewGuid().ToString("N"));
        try
        {
            long length = await SpoolAsync(picked, spooled);
            Console.WriteLine(
                $"TensorAgent: attach {source} name='{picked.FileName}' type='{picked.ContentType}' bytes={length}");
            if (length == 0)
            {
                await Notice($"{shown} could not be attached: it came back empty from the picker.");
                return;
            }

            // The name the service classifies by, resolved from what the pick actually
            // has: iOS's photo picker strips the extension, and the service decides what
            // an upload IS from that extension alone. See UploadNaming.
            string name = Core.Hosting.UploadNaming.ResolveFileName(picked.FileName, picked.ContentType, spooled);
            await using FileStream content = File.OpenRead(spooled);
            object result = await _host.App.Chat.UploadAsync(content, name, length, CancellationToken.None);

            // The page's own addAttachment takes exactly what /api/upload returns, so the
            // service's answer is forwarded verbatim rather than reshaped here.
            string payload = JsonSerializer.Serialize(result, Core.Hosting.SseFraming.JsonOptions);
            await _webView.EvaluateJavaScriptAsync("window.TensorAgent.addAttachment(" + payload + ")");
        }
        catch (TensorSharp.Chat.WebUiRequestRejectedException rejected)
        {
            // Every refusal carries a sentence written for a person, and that sentence is
            // the whole value of showing the failure at all.
            await Notice($"{shown} could not be attached: {ReasonOf(rejected)}");
        }
        catch (Exception ex)
        {
            await Notice($"{shown} could not be attached: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(spooled)) File.Delete(spooled); } catch (IOException) { /* temp */ }
        }
    }

    /// <summary>
    /// Copy a pick to <paramref name="destination"/> and say how many bytes it was.
    ///
    /// <para>
    /// Two ways in, because the picker has two. <see cref="FileResult.OpenReadAsync"/>
    /// is the supported one; its full path is the fallback, used only when the stream
    /// produced nothing, because a photo library asset can hand back a stream that is
    /// already at its end while the file behind it is perfectly readable.
    /// </para>
    /// </summary>
    private static async Task<long> SpoolAsync(FileResult picked, string destination)
    {
        long copied = await CopyAsync(async () => await picked.OpenReadAsync(), destination);
        if (copied > 0 || string.IsNullOrEmpty(picked.FullPath) || !File.Exists(picked.FullPath))
            return copied;

        Console.WriteLine("TensorAgent: attach the picker's stream was empty; reading " + picked.FullPath);
        return await CopyAsync(() => Task.FromResult<Stream>(File.OpenRead(picked.FullPath)), destination);
    }

    private static async Task<long> CopyAsync(Func<Task<Stream>> open, string destination)
    {
        try
        {
            await using Stream source = await open();
            await using FileStream target = File.Create(destination);
            await source.CopyToAsync(target);
            return target.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine("TensorAgent: attach could not read the pick: " + ex.Message);
            return 0;
        }
    }

    /// <summary>The sentence inside a refusal payload, or the exception's own message.</summary>
    private static string ReasonOf(TensorSharp.Chat.WebUiRequestRejectedException rejected)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                JsonSerializer.Serialize(rejected.Payload, Core.Hosting.SseFraming.JsonOptions));
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.GetString() is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException) { /* fall through to the exception's own words */ }
        return rejected.Message;
    }

    /// <summary>
    /// Dictation, as a toggle. Speech recognition on iOS is a live session rather than
    /// a request, so the button starts it and the second tap ends it; partial results
    /// are ignored and only the final transcription reaches the composer, because
    /// appending partials would rewrite the user's text as they spoke.
    /// </summary>
    /// <summary>
    /// Start listening, because the user is holding the button down.
    ///
    /// <para>
    /// Hold-to-talk rather than a toggle: holding the message box turns the composer
    /// into one large button, and a press that lasts exactly as long as the speech is
    /// what a phone user expects from it. The session ends in
    /// <see cref="StopDictation"/> when the finger lifts, so nothing here waits for a
    /// result -- the transcription is delivered to the composer whenever it arrives.
    /// </para>
    /// </summary>
    private async Task StartDictationAsync()
    {
        if (_dictation is not null)
            return;
        // Cleared here, at the very start, because the finger that asked for this may
        // lift before the session exists.
        _dictationStopRequested = false;

        if (!Platforms.iOS.Dictation.IsSupported)
        {
            await Notice("Speech recognition is not available on this device.");
            await _webView.EvaluateJavaScriptAsync("window.TensorAgent.dictationEnded()");
            return;
        }
        if (await Platforms.iOS.Dictation.RequestPermissionsAsync() is { } refused)
        {
            // A permission iOS has already stored a "no" for cannot be asked for
            // again, so telling the user to try harder is useless: the only way back
            // is Settings, and the app can open it for them.
            bool permanent = refused.Contains(Platforms.iOS.Dictation.DeniedMarker, StringComparison.Ordinal);
            string message = refused.Replace(Platforms.iOS.Dictation.DeniedMarker, string.Empty).Trim();
            if (permanent)
                await NoticeWithSettings(message);
            else
                await Notice(message);
            await _webView.EvaluateJavaScriptAsync("window.TensorAgent.dictationEnded()");
            return;
        }

        _dictation = new Platforms.iOS.Dictation(_host.App.Settings.Load().SpeechLanguage);
        // The finger is very often already gone. On the first ever hold, iOS puts two
        // permission dialogs in front of the user, and tapping Allow means letting go of
        // the message box -- so `dictate-stop` arrives while this method is still inside
        // the await above, at which point StopDictation has nothing to stop. Starting
        // anyway would open the microphone and leave it open, because the release that
        // would have closed it has already happened.
        if (_dictationStopRequested)
        {
            _dictation.Dispose();
            _dictation = null;
            await _webView.EvaluateJavaScriptAsync("window.TensorAgent.dictationEnded()");
            return;
        }
        try
        {
            string text = await _dictation.ListenAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await _webView.EvaluateJavaScriptAsync(
                    "window.TensorAgent.insertText(" + System.Text.Json.JsonSerializer.Serialize(text) + ")");
            }
        }
        catch (Exception ex)
        {
            await Notice("Dictation failed: " + ex.Message);
        }
        finally
        {
            _dictation?.Dispose();
            _dictation = null;
            await _webView.EvaluateJavaScriptAsync("window.TensorAgent.dictationEnded()");
        }
    }

    /// <summary>End the session the finger was holding open, or refuse the one that has
    /// not started yet.</summary>
    private void StopDictation()
    {
        _dictationStopRequested = true;
        try { _dictation?.Stop(); }
        catch (Exception ex) { Console.WriteLine("TensorAgent: stop dictation failed: " + ex.Message); }
    }

    /// <summary>Set when a release arrived before there was a session to release.</summary>
    private bool _dictationStopRequested;

    /// <summary>
    /// Show a message inside the page rather than as a native alert, so it looks the
    /// same as everything else the chat says.
    /// </summary>
    private async Task Notice(string text) => await _webView.EvaluateJavaScriptAsync(
        "window.TensorAgent.notice(" + System.Text.Json.JsonSerializer.Serialize(text) + ", 'error')");

    /// <summary>
    /// The same notice, with a button that opens this app's page in iOS Settings.
    /// Used for a permission the user has already refused, where nothing the app does
    /// can ask again.
    /// </summary>
    private async Task NoticeWithSettings(string text) => await _webView.EvaluateJavaScriptAsync(
        "window.TensorAgent.noticeWithSettings(" + System.Text.Json.JsonSerializer.Serialize(text) + ")");

    /// <summary>Open Settings › TensorAgent, which is the only place these grants live.</summary>
    private static void OpenAppSettings()
    {
        try
        {
            var url = new Foundation.NSUrl(UIKit.UIApplication.OpenSettingsUrlString);
            UIKit.UIApplication.SharedApplication.OpenUrl(url, new UIKit.UIApplicationOpenUrlOptions(), null);
        }
        catch (Exception ex) { Console.WriteLine("TensorAgent: open settings failed: " + ex.Message); }
    }

    /// <summary>
    /// Point the page at a saved conversation, or at a brand new one.
    ///
    /// <para>
    /// A call into the page, NOT a navigation. It used to reload the WebView with the
    /// conversation in the query string, on the reasoning that the Web UI builds its
    /// whole state at load and reusing a loaded page would leave the previous chat's
    /// session and attachments behind. The page now rebuilds exactly those three things
    /// itself, and the reload was paying for that tidiness with everything else in the
    /// page: every request in flight, which on a phone includes the answer the model is
    /// halfway through writing.
    /// </para>
    /// </summary>
    public void OpenConversation(string? conversationId)
    {
        string argument = conversationId is { Length: > 0 } id ? JsonSerializer.Serialize(id) : "null";
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await _webView.EvaluateJavaScriptAsync(
                    "window.TensorAgent && window.TensorAgent.openConversation ? "
                    + "window.TensorAgent.openConversation(" + argument + ") : false");
            }
            catch (Exception ex) { Console.WriteLine("TensorAgent: open conversation failed: " + ex.Message); }
        });
    }

    /// <summary>
    /// What the page asks the app to do.
    ///
    /// <para>
    /// The page owns the chrome now, so the things only native code can do -- the
    /// camera, the photo library, the document picker, dictation, and leaving the
    /// chat for another route -- are reached by the page ASKING for them over the
    /// transport that already exists, rather than by a second row of native buttons
    /// duplicating the page's own "+". A WKWebView message handler would be the
    /// platform way; this keeps the client free of any iOS-specific API and is
    /// testable over plain HTTP.
    /// </para>
    /// </summary>
    private void OnPageEvent(string kind, System.Text.Json.JsonElement message)
    {
        switch (kind)
        {
            // The page has finished loading and is asking for nothing. This is where it
            // is told it is inside the app, and it has to be HERE rather than in
            // OnAppearing: on a cold launch the page appears before it exists, so the
            // call landed on nothing and `state.native` stayed false — which is a
            // composer whose long press answers "Voice input is only available in the
            // app" while running in the app, and a "+" that opens a browser file input
            // instead of the photo picker. The same hole reopened every time the WebView
            // was navigated to a saved conversation. The page emits this on every load,
            // so answering it is the one place that cannot be too early or too late.
            case "ready":
                _pageReady = true;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { await Tell("nativeReady"); }
                    catch (Exception ex) { Console.WriteLine("TensorAgent: nativeReady failed: " + ex.Message); }
                });
                return;

            case "open-models":
            case "open-route":
                string route = message.TryGetProperty("route", out System.Text.Json.JsonElement r)
                    ? r.GetString() ?? "models" : "models";
                MainThread.BeginInvokeOnMainThread(async () => await AppShell.OpenAsync(route));
                return;

            case "pick":
                string what = message.TryGetProperty("what", out System.Text.Json.JsonElement w)
                    ? w.GetString() ?? string.Empty : string.Empty;
                MediaSource? source = what switch
                {
                    "photo" => MediaSource.Library,
                    "camera" => MediaSource.Camera,
                    "video" => MediaSource.Video,
                    "file" => MediaSource.File,
                    _ => null,
                };
                if (source is { } picked)
                    MainThread.BeginInvokeOnMainThread(async () => await AttachAsync(picked));
                return;

            case "dictate-start":
                MainThread.BeginInvokeOnMainThread(async () => await StartDictationAsync());
                return;

            case "dictate-stop":
                MainThread.BeginInvokeOnMainThread(StopDictation);
                return;

            case "open-settings":
                MainThread.BeginInvokeOnMainThread(OpenAppSettings);
                return;
        }
    }

    private void StartHost()
    {
        try
        {
            _host.Start();
            EngineProbeResult probe = EngineProbe.Run();
            Console.WriteLine("TensorAgent: engine probe " + JsonSerializer.Serialize(probe, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
#if DEBUG
            // Debug only: lets the simulator E2E harness curl the API from the Mac
            // (X-TensorAgent-Token). A release build keeps the token in-process.
            Console.WriteLine($"TensorAgent: entry URL {_host.EntryUrl}");
#endif
            _status.Text =
                $"{probe.Backend} · GgmlOps {(probe.MainProgramHandleResolved ? "linked" : "NOT linked")}" +
                $" · {probe.GpuName ?? "no Metal device"} · :{_host.Port}";
            // The page says when the model starts and stops working; the display is
            // held awake for exactly that stretch, because on iOS the screen sleeping
            // suspends the app and stops the generation partway.
            _host.App.PageEvent += OnPageEvent;
            _webView.Source = new UrlWebViewSource { Url = _host.EntryUrl };
#if DEBUG
            // What the launch log cannot tell you otherwise: whether the interpreters
            // that linked can actually run, and whether the sandbox refuses what it
            // must. Debug only, off the UI thread, into a throwaway directory.
            _ = Task.Run(() =>
            {
                foreach (Core.Hosting.SelfTestResult check in _host.App.SelfTest())
                    Console.WriteLine("TensorAgent: selftest " + check);
            });
            _ = RunUploadProbeAsync();
            RunNetworkProbe();
            DownloadIfAsked();
#endif
        }
        catch (Exception ex)
        {
            // Say so once, in the UI and on stdout; there is no useful fallback
            // page without the loopback host.
            Console.WriteLine("TensorAgent: startup failed: " + ex);
            _status.TextColor = BarError;
            _status.LineBreakMode = LineBreakMode.WordWrap;
            _status.Text = "Startup failed: " + ex.Message;
        }
    }
}
