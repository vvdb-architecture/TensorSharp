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
/// One place, and four screens that sit on top of it.
///
/// <para>
/// The chat is the shell's ONLY content. The saved chats, the model list, the settings
/// and the about page are pushed over it and popped off again, rather than being
/// sibling items the shell switches between — and that is not a matter of taste.
/// Switching top-level items tears the page down: the shell renderer disposes the
/// outgoing item's renderer, which disconnects the page's handler and removes its view
/// controller. Pushing does none of that; the chat stays in the stack with its handler
/// intact, so it comes back instantly and with its state.
/// </para>
/// <para>
/// What pushing does NOT buy, and no navigation shape here can, is keeping the WebView
/// running. UIKit takes a covered view controller's view out of the window, WebKit drops
/// the foreground assertion for a WKWebView whose window is nil, and the content process
/// is suspended — mid-answer. That is why the generation cannot belong to the page at
/// all: it belongs to <see cref="Core.Sessions.ChatTurnManager"/>, and the page attaches
/// to it again on the way back. This class only makes sure there is a page to come back
/// to.
/// </para>
/// <para>
/// The flyout is gone with it. The page has its own menu now — a drawer from the left
/// edge that also lists the saved chats — and two menus for the same five destinations
/// is one more than a phone screen can justify.
/// </para>
/// </summary>
public sealed class AppShell : Shell
{
    private readonly Dictionary<string, Page> _pages;

    public AppShell(MainPage chat, Pages.SessionsPage sessions, Pages.ModelsPage models, Pages.SettingsPage settings, Pages.AboutPage about)
    {
        Title = "TensorAgent";
        FlyoutBehavior = FlyoutBehavior.Disabled;
        BackgroundColor = Pages.Theme.Background;

        Items.Add(new ShellContent { Title = "Chat", Route = "main", Content = chat });
        _pages = new Dictionary<string, Page>(StringComparer.Ordinal)
        {
            ["sessions"] = sessions,
            ["models"] = models,
            ["settings"] = settings,
            ["about"] = about,
        };

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
                    await OpenAsync("main");
                }
                catch (Exception ex) { Console.WriteLine("TensorAgent: debug model use failed: " + ex.Message); }
            });
        }

        string? start = Environment.GetEnvironmentVariable("TENSORAGENT_START_PAGE");
        if (!string.IsNullOrWhiteSpace(start))
            Dispatcher.Dispatch(async () => await OpenAsync(start.Trim()));
#endif
    }

    /// <summary>
    /// Show one of the app's other screens over the chat, or come back to the chat.
    ///
    /// <para>
    /// The stack is never deeper than one: whatever is already on top comes off first,
    /// so "Models" from a page reached through "Settings" leaves one page over the chat
    /// rather than three, and the back arrow always means "back to the chat". A route
    /// this shell does not have is ignored rather than guessed at, which is what stops a
    /// page from navigating the app somewhere it has no screen.
    /// </para>
    /// </summary>
    public static async Task OpenAsync(string? route)
    {
        if (Current is not AppShell shell)
            return;
        // Decided BEFORE anything is popped, so a route this shell does not have leaves
        // the app exactly where it was rather than quietly closing the page the user is
        // looking at.
        Page? page = null;
        bool chat = route is null or "main";
        if (!chat && !shell._pages.TryGetValue(route!, out page))
            return;

        try
        {
            INavigation navigation = shell.Navigation;
            // Shell's stack carries a null placeholder for the shell content itself, so
            // anything past the first entry is a page pushed over the chat. Bounded
            // rather than `while`: a pop that does not shorten the stack would otherwise
            // spin forever on the UI thread, and the stack is never deeper than one.
            for (int i = 0; i < 8 && navigation.NavigationStack.Count > 1; i++)
                await navigation.PopAsync(false);

            if (!chat)
                await navigation.PushAsync(page!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TensorAgent: open {route} failed: " + ex.Message);
        }
    }

    /// <summary>Come back to the chat, from a page that is done.</summary>
    public static Task BackToChatAsync() => OpenAsync("main");
}
