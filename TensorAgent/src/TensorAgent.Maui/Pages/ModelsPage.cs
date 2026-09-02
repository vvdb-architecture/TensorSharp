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
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;
using TensorAgent.Maui.Hosting;

namespace TensorAgent.Maui.Pages;

/// <summary>
/// The built-in model list: what this device can run, what is already on it, and
/// what a download would cost.
///
/// <para>
/// This page has no counterpart in the desktop Web UI, and it cannot have one. The
/// server's model picker lists files an operator put on a disk; here the app is
/// responsible for getting them, which means telling the user the size before they
/// commit to it, resuming an interrupted download rather than starting again, and
/// making a partly-downloaded model obviously partly downloaded.
/// </para>
/// </summary>
public sealed class ModelsPage : ContentPage
{
    private readonly AgentAppHost _app;
    private readonly ObservableCollection<ModelRow> _rows = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    public ModelsPage(LoopbackWebHost host)
    {
        _app = host.App;
        Title = "Models";
        BackgroundColor = Theme.Background;
        Padding = new Thickness(0);

        var list = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(BuildCell),
            SelectionMode = SelectionMode.None,
            BackgroundColor = Theme.Background,
        };

        Content = new Grid
        {
            BackgroundColor = Theme.Background,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            Children =
            {
                Header(),
                list,
            },
        };
        Grid.SetRow((View)((Grid)Content).Children[1], 1);
    }

    private View Header()
    {
        var label = new Label
        {
            Text = $"Models that fit this device ({_app.Paths.DeviceMemoryGB} GB). "
                 + "Downloads resume if interrupted and are kept for next time.",
            TextColor = Theme.Muted,
            FontSize = 13,
            Padding = new Thickness(16, 12, 16, 8),
        };
        return label;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void Refresh()
    {
        string? selected = _app.Settings.Load().SelectedModelId;
        _rows.Clear();
        foreach (CatalogModel model in _app.Catalog)
            _rows.Add(new ModelRow(model, _app.Models, selected));
    }

    private View BuildCell()
    {
        var title = new Label { FontSize = 16, TextColor = Theme.Text, FontAttributes = FontAttributes.Bold };
        title.SetBinding(Label.TextProperty, nameof(ModelRow.Title));

        var subtitle = new Label { FontSize = 12, TextColor = Theme.Muted, LineBreakMode = LineBreakMode.WordWrap };
        subtitle.SetBinding(Label.TextProperty, nameof(ModelRow.Subtitle));

        var status = new Label { FontSize = 12, TextColor = Theme.Accent };
        status.SetBinding(Label.TextProperty, nameof(ModelRow.Status));

        var progress = new ProgressBar { ProgressColor = Theme.Accent, HeightRequest = 3 };
        progress.SetBinding(ProgressBar.ProgressProperty, nameof(ModelRow.Fraction));
        progress.SetBinding(IsVisibleProperty, nameof(ModelRow.IsBusy));

        var action = new Button
        {
            FontSize = 14,
            Padding = new Thickness(14, 6),
            BackgroundColor = Theme.Accent,
            TextColor = Colors.White,
            CornerRadius = 8,
        };
        action.SetBinding(Button.TextProperty, nameof(ModelRow.ActionLabel));
        action.Clicked += (s, _) => OnAction(((Button)s!).BindingContext as ModelRow);

        var remove = new Button
        {
            Text = "Delete",
            FontSize = 14,
            Padding = new Thickness(14, 6),
            BackgroundColor = Theme.Surface,
            TextColor = Theme.Muted,
            CornerRadius = 8,
        };
        remove.SetBinding(IsVisibleProperty, nameof(ModelRow.CanDelete));
        remove.Clicked += (s, _) => OnDelete(((Button)s!).BindingContext as ModelRow);

        var buttons = new HorizontalStackLayout { Spacing = 8, Children = { action, remove } };

        return new Border
        {
            Margin = new Thickness(12, 6),
            Padding = new Thickness(14),
            BackgroundColor = Theme.Surface,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children = { title, subtitle, status, progress, buttons },
            },
        };
    }

    private async void OnAction(ModelRow? row)
    {
        if (row is null)
            return;

        if (_running.TryGetValue(row.Model.Id, out CancellationTokenSource? running))
        {
            running.Cancel();
            return;
        }

        if (row.IsInstalled)
        {
            Select(row);
            return;
        }

        await DownloadAsync(row);
    }

    private void Select(ModelRow row)
    {
        AppSettings settings = _app.Settings.Load();
        settings.SelectedModelId = row.Model.Id;
        _app.Settings.Save(settings);
        Refresh();
        // The engine reads the selected model when the host is built, so the choice
        // takes effect on the next launch. Saying so is better than a silent no-op.
        DisplayAlert("Model selected", $"{row.Model.DisplayName} will be used when TensorAgent next starts.", "OK");
    }

    private async Task DownloadAsync(ModelRow row)
    {
        var cancel = new CancellationTokenSource();
        _running[row.Model.Id] = cancel;
        row.BeginDownload();

        var progress = new Progress<ModelDownloadProgress>(p => MainThread.BeginInvokeOnMainThread(() => row.Report(p)));
        try
        {
            IReadOnlyCollection<CatalogFileRole>? optional = _app.Settings.Load().DownloadOptionalFiles
                ? new[] { CatalogFileRole.Projector, CatalogFileRole.Lora, CatalogFileRole.TextEncoder, CatalogFileRole.Vae, CatalogFileRole.VisionProjector }
                : null;
            await _app.Models.DownloadAsync(row.Model, progress, cancel.Token, optional);
            row.Finish(_app.Models);
            Select(row);
        }
        catch (OperationCanceledException)
        {
            // The part file is kept on purpose: the next attempt resumes from it.
            row.Cancelled(_app.Models);
        }
        catch (Exception ex)
        {
            row.Failed(ex.Message);
            await DisplayAlert("Download failed", ex.Message, "OK");
        }
        finally
        {
            _running.Remove(row.Model.Id);
        }
    }

    private async void OnDelete(ModelRow? row)
    {
        if (row is null)
            return;
        if (!await DisplayAlert("Delete model",
                $"Remove {row.Model.DisplayName} from this device? It can be downloaded again.", "Delete", "Cancel"))
        {
            return;
        }
        _app.Models.Delete(row.Model);
        Refresh();
    }
}

/// <summary>One row of the model list, and the only place its display state lives.</summary>
public sealed class ModelRow : BindableObject
{
    private string _status;
    private double _fraction;
    private bool _busy;
    private string _actionLabel;

    public ModelRow(CatalogModel model, ModelStore store, string? selectedId)
    {
        Model = model;
        IsInstalled = store.StateOf(model) == InstallState.Installed;
        IsSelected = string.Equals(model.Id, selectedId, StringComparison.Ordinal);
        _status = DescribeState(store);
        _actionLabel = IsInstalled ? (IsSelected ? "Selected" : "Use") : "Download";
    }

    public CatalogModel Model { get; }
    public bool IsInstalled { get; private set; }
    public bool IsSelected { get; }

    public string Title => Model.DisplayName + (IsSelected ? "  ·  in use" : string.Empty);

    public string Subtitle =>
        $"{Model.Parameters} · {Model.Quantization} · {Gb(Model.TotalBytes)} GB"
        + (Model.Kind == CatalogArchitectureKind.MixtureOfExperts ? " · mixture of experts" : string.Empty)
        + (Model.Modalities.HasFlag(CatalogModalities.Image) ? " · images" : string.Empty)
        + (Model.Experimental ? "\nExperimental: " + Model.Notes : string.Empty);

    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public double Fraction { get => _fraction; private set { _fraction = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _busy; private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDelete)); } }
    public string ActionLabel { get => _actionLabel; private set { _actionLabel = value; OnPropertyChanged(); } }
    public bool CanDelete => IsInstalled && !IsBusy;

    public void BeginDownload()
    {
        IsBusy = true;
        ActionLabel = "Stop";
        Status = "Starting…";
    }

    public void Report(ModelDownloadProgress p)
    {
        Fraction = p.Fraction;
        string speed = p.BytesPerSecond > 1 ? $" · {p.BytesPerSecond / 1e6:0.0} MB/s" : string.Empty;
        string eta = p.Eta is { } left ? $" · {Math.Round(left.TotalMinutes)} min left" : string.Empty;
        Status = $"{p.Phase} {p.FileIndex + 1}/{p.FileCount} · {Gb(p.BytesReceived)}/{Gb(p.TotalBytes)} GB{speed}{eta}";
    }

    public void Finish(ModelStore store)
    {
        IsBusy = false;
        IsInstalled = true;
        Fraction = 1;
        ActionLabel = "Use";
        Status = DescribeState(store);
        OnPropertyChanged(nameof(CanDelete));
    }

    public void Cancelled(ModelStore store)
    {
        IsBusy = false;
        ActionLabel = "Resume";
        Status = "Stopped. " + DescribeState(store);
    }

    public void Failed(string message)
    {
        IsBusy = false;
        ActionLabel = "Retry";
        Status = message;
    }

    private string DescribeState(ModelStore store) => store.StateOf(Model) switch
    {
        InstallState.Installed => $"On this device · {Gb(store.InstalledBytes(Model))} GB",
        InstallState.Partial => $"Partly downloaded · {Gb(store.RemainingBytes(Model))} GB still to fetch",
        _ => $"Not downloaded · {Gb(Model.TotalBytes)} GB · {Model.License}",
    };

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.00");
}

/// <summary>The Web UI's own palette, so the native pages do not look bolted on.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb("#0b1220");
    public static readonly Color Surface = Color.FromArgb("#151d31");
    public static readonly Color Text = Color.FromArgb("#e6edf7");
    public static readonly Color Muted = Color.FromArgb("#8b9ab8");
    public static readonly Color Accent = Color.FromArgb("#3b82f6");
    public static readonly Color Danger = Color.FromArgb("#ef4444");
}
