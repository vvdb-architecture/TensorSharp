// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;
using TensorAgent.Maui.Hosting;

namespace TensorAgent.Maui.Pages;

/// <summary>
/// The sandbox switches, and everything else the user gets to decide.
///
/// <para>
/// Two of these are the security surface of the whole app, so they are presented
/// as what they actually control rather than as feature names. "Run code" decides
/// whether the model may execute anything at all; with it off there is no shell,
/// no interpreter and no skill script, and the model is told so rather than
/// discovering it. "Allow network access" decides whether anything that runs may
/// open a socket. Both default to the safe answer — code on, because an agent that
/// cannot act is not an agent, and network off, because a model that can reach the
/// internet from inside a sandbox is a different risk entirely — and neither is
/// ever changed except from here.
/// </para>
/// <para>
/// A change takes effect when the app next starts, because the code runner and its
/// policy are built once at startup. That is stated on the page: a switch that
/// silently does nothing until an invisible event is worse than one that says when
/// it will apply.
/// </para>
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private readonly AgentAppHost _app;
    private readonly VerticalStackLayout _body;

    public SettingsPage(LoopbackWebHost host)
    {
        _app = host.App;
        Title = "Settings";
        BackgroundColor = Theme.Background;

        _body = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(0, 8, 0, 24) };
        Content = new ScrollView { Content = _body, BackgroundColor = Theme.Background };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Build();
    }

    private void Build()
    {
        AppSettings settings = _app.Settings.Load();
        _body.Clear();

        _body.Add(Section("Sandbox"));
        _body.Add(Switch(
            "Run code",
            "Let the model run shell commands and scripts. Everything runs inside the app, "
            + "confined to this chat's own folder; it can never write elsewhere on the device.",
            settings.AllowCodeExecution,
            on => { AppSettings s = _app.Settings.Load(); s.AllowCodeExecution = on; _app.Settings.Save(s); }));

        _body.Add(Switch(
            "Allow network access",
            "Let code the model runs reach the internet, and let it install packages. "
            + "Off by default: with it off, every attempt is refused and the model is told why.",
            settings.AllowNetwork,
            on => { AppSettings s = _app.Settings.Load(); s.AllowNetwork = on; _app.Settings.Save(s); }));

        _body.Add(Note("Sandbox changes apply the next time TensorAgent starts."));
        _body.Add(Note("Now: " + _app.DescribeEngine()));

        _body.Add(Section("Generation"));
        _body.Add(Stepper("Reply length limit", "Maximum tokens in one reply.",
            settings.MaxTokens, 256, 8192, 256,
            v => { AppSettings s = _app.Settings.Load(); s.MaxTokens = v; _app.Settings.Save(s); }));
        _body.Add(Stepper("Tool timeout", "Seconds before a command is stopped.",
            settings.ToolTimeoutSeconds, 10, 600, 10,
            v => { AppSettings s = _app.Settings.Load(); s.ToolTimeoutSeconds = v; _app.Settings.Save(s); }));
        _body.Add(Switch("Show reasoning by default",
            "Start each chat with the model's thinking visible.",
            settings.ThinkByDefault,
            on => { AppSettings s = _app.Settings.Load(); s.ThinkByDefault = on; _app.Settings.Save(s); }));

        _body.Add(Section("Downloads"));
        _body.Add(Switch("Download over cellular",
            "Model files are several gigabytes. Off by default so a download waits for Wi-Fi.",
            settings.AllowCellularDownloads,
            on => { AppSettings s = _app.Settings.Load(); s.AllowCellularDownloads = on; _app.Settings.Save(s); }));
        _body.Add(Switch("Include optional files",
            "The image projector and the step-distilled adapter. Larger downloads, but "
            + "without them a model cannot see pictures and image generation is many times slower.",
            settings.DownloadOptionalFiles,
            on => { AppSettings s = _app.Settings.Load(); s.DownloadOptionalFiles = on; _app.Settings.Save(s); }));

        _body.Add(Section("Storage"));
        _body.Add(Note($"Models: {Gb(DirectorySize(_app.Paths.ModelsDirectory))} GB"));
        _body.Add(Note($"Chats: {_app.Conversations.List().Count}"));
        _body.Add(Note($"Skills: {_app.Skills.Skills.Count}"));

        var clear = new Button
        {
            Text = "Delete all chats",
            BackgroundColor = Theme.Surface,
            TextColor = Theme.Danger,
            CornerRadius = 10,
            Margin = new Thickness(16, 12, 16, 0),
        };
        clear.Clicked += async (_, _) =>
        {
            if (!await DisplayAlert("Delete all chats", "This cannot be undone.", "Delete", "Cancel"))
                return;
            // includeEmpty: "delete all chats" has to mean all of them, including the
            // untouched one the current session is sitting in.
            foreach (var summary in _app.Conversations.List(includeEmpty: true))
                _app.Conversations.Delete(summary.Id);
            Build();
        };
        _body.Add(clear);
    }

    private static View Section(string text) => new Label
    {
        Text = text.ToUpperInvariant(),
        FontSize = 12,
        TextColor = Theme.Muted,
        FontAttributes = FontAttributes.Bold,
        Padding = new Thickness(16, 20, 16, 6),
    };

    private static View Note(string text) => new Label
    {
        Text = text,
        FontSize = 12,
        TextColor = Theme.Muted,
        Padding = new Thickness(16, 2),
    };

    private static View Switch(string title, string detail, bool value, Action<bool> onChanged)
    {
        var toggle = new Microsoft.Maui.Controls.Switch { IsToggled = value, OnColor = Theme.Accent, VerticalOptions = LayoutOptions.Center };
        toggle.Toggled += (_, e) => onChanged(e.Value);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Padding = new Thickness(16, 10),
        };
        grid.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = title, FontSize = 16, TextColor = Theme.Text },
                new Label { Text = detail, FontSize = 12, TextColor = Theme.Muted },
            },
        }, 0, 0);
        grid.Add(toggle, 1, 0);
        return grid;
    }

    private static View Stepper(string title, string detail, int value, int min, int max, int step, Action<int> onChanged)
    {
        var current = new Label { Text = value.ToString(), FontSize = 15, TextColor = Theme.Accent, VerticalOptions = LayoutOptions.Center };
        var stepper = new Stepper(min, max, Math.Clamp(value, min, max), step) { VerticalOptions = LayoutOptions.Center };
        stepper.ValueChanged += (_, e) =>
        {
            int v = (int)e.NewValue;
            current.Text = v.ToString();
            onChanged(v);
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(16, 10),
            ColumnSpacing = 10,
        };
        grid.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = title, FontSize = 16, TextColor = Theme.Text },
                new Label { Text = detail, FontSize = 12, TextColor = Theme.Muted },
            },
        }, 0, 0);
        grid.Add(current, 1, 0);
        grid.Add(stepper, 2, 0);
        return grid;
    }

    private static long DirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.00");
}
