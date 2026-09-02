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
            Padding = new Thickness(12, 6),
            LineBreakMode = LineBreakMode.TailTruncation,
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
            },
        };
        grid.Add(_status, 0, 0);
        grid.Add(_webView, 0, 1);
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
