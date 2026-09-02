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
    private Button? _dictate;
    private Platforms.iOS.Dictation? _dictation;

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
        grid.Add(BuildTopBar(), 0, 0);
        grid.Add(_webView, 0, 1);
        grid.Add(BuildAttachmentBar(), 0, 2);
        Content = grid;

#if DEBUG
        _webView.Navigated += OnNavigatedSendDemoPrompt;
#endif
        StartHost();
    }

#if DEBUG
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
        string? prompt = Environment.GetEnvironmentVariable("TENSORAGENT_DEMO_PROMPT");
        if (string.IsNullOrWhiteSpace(prompt) || e.Result != WebNavigationResult.Success)
        {
            return;
        }

        try
        {
            // fetchServerState() runs at page load and must have populated
            // currentLoadedModel before sendMessage() accepts anything; a short
            // wait is the simplest way to sequence after that fetch.
            await Task.Delay(1500);
            string js = "document.getElementById('message-input').value = " + JsonSerializer.Serialize(prompt) + "; sendMessage();";
            await _webView.EvaluateJavaScriptAsync(js);
            Console.WriteLine("TensorAgent: demo prompt sent through the Web UI: " + prompt);
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: demo prompt failed: " + ex.Message);
        }
    }
#endif

    /// <summary>
    /// The row of things a phone can do that a browser cannot: the camera, the photo
    /// library, a file, and the microphone.
    ///
    /// <para>
    /// Each one ends in the same place the Web UI's own paperclip ends — a POST to
    /// <c>/api/upload</c> whose response is handed to the page's attachment list — so
    /// an attachment picked natively and one picked in the page are the same thing by
    /// the time a message is sent. Dictation is different in kind: it produces text,
    /// so it goes into the composer rather than the attachment list, which is what a
    /// user expects from a microphone button next to a text box.
    /// </para>
    /// </summary>
    /// <summary>
    /// The one row of native chrome above the page: where the app is, and how to get
    /// to the three things the Web UI has no concept of.
    ///
    /// <para>
    /// The chat page hides the shell's navigation bar so the WebView reaches the top
    /// of the screen, which is what makes it feel like an app rather than a browser.
    /// That leaves nothing to open the flyout with, so the routes are here instead —
    /// and being visible rather than behind a gesture matters, because a user who
    /// has not downloaded a model yet needs to find the model list on their first
    /// launch.
    /// </para>
    /// </summary>
    private View BuildTopBar()
    {
        var bar = new Grid
        {
            BackgroundColor = BarBackground,
            Padding = new Thickness(12, 6, 8, 6),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        bar.Add(_status, 0, 0);
        bar.Add(new HorizontalStackLayout
        {
            Spacing = 4,
            Children =
            {
                NavChip("Chats", "//sessions"),
                NavChip("Models", "//models"),
                NavChip("Settings", "//settings"),
            },
        }, 1, 0);
        return bar;
    }

    private Button NavChip(string text, string route)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(10, 2),
            CornerRadius = 12,
            BackgroundColor = Color.FromArgb("#1c2438"),
            TextColor = BarText,
        };
        button.Clicked += async (_, _) => await Shell.Current.GoToAsync(route);
        return button;
    }

    private View BuildAttachmentBar()
    {
        _dictate = Chip("Speak", OnDictate);
        var bar = new HorizontalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(12, 8),
            BackgroundColor = BarBackground,
            Children =
            {
                Chip("Photo", () => AttachAsync(MediaSource.Library)),
                Chip("Camera", () => AttachAsync(MediaSource.Camera)),
                Chip("Video", () => AttachAsync(MediaSource.Video)),
                Chip("File", () => AttachAsync(MediaSource.File)),
                _dictate,
            },
        };
        return new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = bar, BackgroundColor = BarBackground };
    }

    private Button Chip(string text, Func<Task> onTap)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(12, 4),
            CornerRadius = 14,
            BackgroundColor = Color.FromArgb("#1c2438"),
            TextColor = BarText,
        };
        button.Clicked += async (_, _) =>
        {
            try { await onTap(); }
            catch (Exception ex) { await Notice(ex.Message); }
        };
        return button;
    }

    private Button Chip(string text, Action onTap) => Chip(text, () => { onTap(); return Task.CompletedTask; });

    private enum MediaSource { Library, Camera, Video, File }

    /// <summary>
    /// Pick something and upload it through the route the page already uses, then tell
    /// the page it now has an attachment.
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

        await using Stream content = await picked.OpenReadAsync();
        using var form = new MultipartFormDataContent();
        using var part = new StreamContent(content);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(picked.ContentType) ? "application/octet-stream" : picked.ContentType);
        form.Add(part, "file", picked.FileName);

        using var client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
        client.DefaultRequestHeaders.Add("Cookie", $"tensoragent_token={_host.Token}");
        HttpResponseMessage response = await client.PostAsync("/api/upload", form);
        string payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            await Notice($"Upload failed ({(int)response.StatusCode}).");
            return;
        }

        // The page's own addAttachment takes exactly what /api/upload returned, so the
        // payload is forwarded verbatim rather than reshaped here.
        await _webView.EvaluateJavaScriptAsync("window.TensorAgent.addAttachment(" + payload + ")");
    }

    /// <summary>
    /// Dictation, as a toggle. Speech recognition on iOS is a live session rather than
    /// a request, so the button starts it and the second tap ends it; partial results
    /// are ignored and only the final transcription reaches the composer, because
    /// appending partials would rewrite the user's text as they spoke.
    /// </summary>
    private async Task OnDictate()
    {
        if (_dictation is not null)
        {
            _dictation.Stop();
            return;
        }

        if (!Platforms.iOS.Dictation.IsSupported)
        {
            await Notice("Speech recognition is not available on this device.");
            return;
        }
        if (await Platforms.iOS.Dictation.RequestPermissionsAsync() is { } refused)
        {
            await Notice(refused);
            return;
        }

        _dictation = new Platforms.iOS.Dictation();
        _dictate!.Text = "Stop";
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
            _dictation.Dispose();
            _dictation = null;
            _dictate!.Text = "Speak";
        }
    }

    /// <summary>
    /// Show a message inside the page rather than as a native alert, so it looks the
    /// same as everything else the chat says.
    /// </summary>
    private async Task Notice(string text) => await _webView.EvaluateJavaScriptAsync(
        "window.TensorAgent.notice(" + System.Text.Json.JsonSerializer.Serialize(text) + ", 'error')");

    /// <summary>
    /// Point the page at a saved conversation, or at a brand new one.
    ///
    /// <para>
    /// It is a navigation rather than a call into the page because the Web UI builds
    /// its whole state at load: reusing a loaded page would leave the previous chat's
    /// engine session, pending attachments and scroll position behind. Reloading with
    /// the conversation in the query string is what the injected script is written
    /// against.
    /// </para>
    /// </summary>
    public void OpenConversation(string? conversationId)
    {
        string target = conversationId is { Length: > 0 } id
            ? $"{_host.EntryUrl}&conversation={Uri.EscapeDataString(id)}"
            : $"{_host.EntryUrl}&conversation=new";
        MainThread.BeginInvokeOnMainThread(() => _webView.Source = new UrlWebViewSource { Url = target });
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
