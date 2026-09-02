// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Maui;

/// <summary>
/// The four places the app has: the chat itself, the saved chats, the model list
/// and the settings.
///
/// <para>
/// Chat is a page rather than a tab item so that everything else navigates back to
/// it by route: opening a saved conversation, and finishing a download that selects
/// a model, both end with the user looking at the chat again.
/// </para>
/// </summary>
public sealed class AppShell : Shell
{
    public AppShell(MainPage chat, Pages.SessionsPage sessions, Pages.ModelsPage models, Pages.SettingsPage settings)
    {
        Title = "TensorAgent";
        FlyoutBehavior = FlyoutBehavior.Flyout;
        BackgroundColor = Pages.Theme.Background;
        FlyoutBackgroundColor = Pages.Theme.Background;

        Items.Add(new ShellContent { Title = "Chat", Route = "main", Content = chat });
        Items.Add(new ShellContent { Title = "Chats", Route = "sessions", Content = sessions });
        Items.Add(new ShellContent { Title = "Models", Route = "models", Content = models });
        Items.Add(new ShellContent { Title = "Settings", Route = "settings", Content = settings });

#if DEBUG
        // The simulator harness cannot tap: simctl has no way to touch the screen, so
        // without this only the first page could ever be screenshotted. Launching with
        // TENSORAGENT_START_PAGE=models opens that route instead, which is how
        // scripts/run-sim.sh captures the model list and the settings.
        string? start = Environment.GetEnvironmentVariable("TENSORAGENT_START_PAGE");
        if (!string.IsNullOrWhiteSpace(start))
        {
            Dispatcher.Dispatch(async () =>
            {
                try { await GoToAsync("//" + start.Trim()); }
                catch (Exception ex) { Console.WriteLine("TensorAgent: start page failed: " + ex.Message); }
            });
        }
#endif
    }
}
