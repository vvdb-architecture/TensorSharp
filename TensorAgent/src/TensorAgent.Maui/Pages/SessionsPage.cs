// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.ObjectModel;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Sessions;
using TensorAgent.Maui.Hosting;

namespace TensorAgent.Maui.Pages;

/// <summary>
/// Saved chats: every conversation the app has kept, newest first, and the way
/// back into any of them.
///
/// <para>
/// Picking one does not reconstruct the model's state — the KV cache did not
/// survive the app being closed, and pretending otherwise would give the user a
/// model that has forgotten a conversation it appears to be in the middle of.
/// What happens instead is what the Web UI already does on a page reload: the
/// transcript is re-rendered and the next turn is sent with the whole history, so
/// the model sees everything even though it is starting fresh.
/// </para>
/// </summary>
public sealed class SessionsPage : ContentPage
{
    private readonly AgentAppHost _app;
    private readonly MainPage _chat;
    private readonly ObservableCollection<ConversationSummary> _rows = new();

    public SessionsPage(LoopbackWebHost host, MainPage chat)
    {
        _app = host.App;
        _chat = chat;
        Title = "Chats";
        BackgroundColor = Theme.Background;

        var list = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(BuildCell),
            SelectionMode = SelectionMode.None,
            BackgroundColor = Theme.Background,
        };

        var newChat = new Button
        {
            Text = "New chat",
            BackgroundColor = Theme.Accent,
            TextColor = Colors.White,
            CornerRadius = 10,
            Margin = new Thickness(12, 12, 12, 4),
        };
        newChat.Clicked += async (_, _) => await OpenAsync(null);

        var grid = new Grid
        {
            BackgroundColor = Theme.Background,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
        };
        grid.Add(newChat, 0, 0);
        grid.Add(list, 0, 1);
        Content = grid;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _rows.Clear();
        foreach (ConversationSummary summary in _app.Conversations.List())
            _rows.Add(summary);
    }

    private View BuildCell()
    {
        var title = new Label { FontSize = 16, TextColor = Theme.Text, LineBreakMode = LineBreakMode.TailTruncation };
        title.SetBinding(Label.TextProperty, nameof(ConversationSummary.Title));

        var meta = new Label { FontSize = 12, TextColor = Theme.Muted };
        meta.SetBinding(Label.TextProperty, new Binding(".", converter: new SummaryLine()));

        var open = new TapGestureRecognizer();
        open.Tapped += async (s, _) =>
        {
            if ((s as View)?.BindingContext is ConversationSummary summary)
                await OpenAsync(summary.Id);
        };

        var remove = new Button
        {
            Text = "Delete",
            FontSize = 13,
            BackgroundColor = Theme.Surface,
            TextColor = Theme.Danger,
            CornerRadius = 8,
            Padding = new Thickness(12, 4),
            HorizontalOptions = LayoutOptions.End,
        };
        remove.Clicked += async (s, _) =>
        {
            if ((s as Button)?.BindingContext is not ConversationSummary summary)
                return;
            if (!await DisplayAlert("Delete chat", $"Delete “{summary.Title}”?", "Delete", "Cancel"))
                return;
            _app.Conversations.Delete(summary.Id);
            _rows.Remove(summary);
        };

        var body = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        body.Add(new VerticalStackLayout { Spacing = 2, Children = { title, meta } }, 0, 0);
        body.Add(remove, 1, 0);
        body.GestureRecognizers.Add(open);

        return new Border
        {
            Margin = new Thickness(12, 4),
            Padding = new Thickness(14, 12),
            BackgroundColor = Theme.Surface,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = body,
        };
    }

    /// <summary>Open the chat page on a conversation, or on a brand new one.</summary>
    private async Task OpenAsync(string? conversationId)
    {
        _chat.OpenConversation(conversationId);
        await Shell.Current.GoToAsync("//main");
    }

    private sealed class SummaryLine : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not ConversationSummary summary)
                return string.Empty;
            string when = summary.UpdatedAt.ToLocalTime().ToString("MMM d, HH:mm");
            string count = summary.MessageCount == 1 ? "1 message" : $"{summary.MessageCount} messages";
            return summary.ModelId is { Length: > 0 } model ? $"{when} · {count} · {model}" : $"{when} · {count}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
