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
    public AppShell(MainPage chat, Pages.SessionsPage sessions, Pages.ModelsPage models, Pages.SettingsPage settings, Pages.AboutPage about)
    {
        Title = "TensorAgent";
        FlyoutBehavior = FlyoutBehavior.Flyout;
        BackgroundColor = Pages.Theme.Background;
        FlyoutBackgroundColor = Pages.Theme.Background;

        Items.Add(new ShellContent { Title = "Chat", Route = "main", Content = chat });
        Items.Add(new ShellContent { Title = "Chats", Route = "sessions", Content = sessions });
        Items.Add(new ShellContent { Title = "Models", Route = "models", Content = models });
        Items.Add(new ShellContent { Title = "Settings", Route = "settings", Content = settings });
        // Last, because it is the one item nobody needs twice -- and it is where the
        // "TensorSharp.ai" banner went when it was taken out of the chat header, which
        // on a phone was a whole row spent on a link read once.
        Items.Add(new ShellContent { Title = "About", Route = "about", Content = about });

#if DEBUG
        // The simulator harness cannot tap: simctl has no way to touch the screen, so
        // without this only the first page could ever be screenshotted. Launching with
        // TENSORAGENT_START_PAGE=models opens that route instead, which is how
        // scripts/run-sim.sh captures the model list and the settings.
        // TENSORAGENT_USE_MODEL=<catalog id> reproduces, without a tap, exactly what a
        // user does in the Models list: choose an installed model, have it loaded, and
        // land back in the chat. It exists because the bug this guards against was
        // reported twice from a phone and could not be reproduced from the outside --
        // devicectl cannot touch the screen, so the one path that mattered was the one
        // path no test could drive. Paired with TENSORAGENT_DEMO_PROMPT it drives the
        // whole reported failure: select a model, then send a prompt, and see whether
        // the page still refuses with "No model is configured".
        string? use = Environment.GetEnvironmentVariable("TENSORAGENT_USE_MODEL");
        if (!string.IsNullOrWhiteSpace(use))
        {
            Dispatcher.Dispatch(async () =>
            {
                try
                {
                    Core.Catalog.CatalogModel? picked = Core.Catalog.ModelCatalog.Find(use.Trim());
                    if (picked is null)
                    {
                        Console.WriteLine($"TensorAgent: TENSORAGENT_USE_MODEL={use} is not a catalog id");
                        return;
                    }
                    string backend = await Task.Run(() => models.Host.UseModel(picked));
                    Console.WriteLine($"TensorAgent: debug hook loaded {picked.Id} on {backend}");
                    await GoToAsync("//main");
                }
                catch (Exception ex) { Console.WriteLine("TensorAgent: debug model use failed: " + ex.Message); }
            });
        }

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
