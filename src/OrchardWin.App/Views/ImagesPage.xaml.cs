using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace OrchardWin.App.Views;

/// Orchard-style Images: list (search / filter / pull) + detail with Overview, Technical
/// Details, Configuration (from inspect), and Used By Containers table.
public sealed partial class ImagesPage : Page
{
    private ImagesViewModel? _viewModel;
    private DispatcherTimer? _refreshTimer;
    private bool _isSyncingSelection;
    private bool _listBound;
    private string? _detailReference;

    private static readonly Color AccentLink = Color.FromArgb(255, 64, 156, 255);

    public ImagesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var args = NavigationArgs.From(e.Parameter);
        _viewModel = new ImagesViewModel(args.Services);
        ImagesListView.ItemsSource = _viewModel.Rows;
        _listBound = true;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SortDirectionGlyph.Glyph = "\uE74A";
        ApplyViewModelState();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.LoadAsync();
        DispatcherQueue.TryEnqueue(ApplyViewModelState);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null)
                await _viewModel.Services.ImageService.LoadAsync(showLoading: false);
        };
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.RunOnUi(() =>
        {
            // Full rebind only when selection / inspection / list membership changes.
            if (e.PropertyName is nameof(ImagesViewModel.IsImagesLoading))
            {
                ImagesLoadingRing.IsActive = _viewModel?.IsImagesLoading ?? false;
                return;
            }

            ApplyViewModelState();
        });
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isSyncingSelection = true;
        try
        {
            if (!_listBound || !ReferenceEquals(ImagesListView.ItemsSource, _viewModel.Rows))
            {
                ImagesListView.ItemsSource = _viewModel.Rows;
                _listBound = true;
            }

            if (!ReferenceEquals(ImagesListView.SelectedItem, _viewModel.SelectedRow))
                ImagesListView.SelectedItem = _viewModel.SelectedRow;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        ImagesLoadingRing.IsActive = _viewModel.IsImagesLoading;
        UpdateDetailPane();
    }

    private void UpdateDetailPane()
    {
        var row = _viewModel?.SelectedRow;
        if (_viewModel is null || row is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            DetailRoot.Visibility = Visibility.Collapsed;
            _detailReference = null;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        DetailRoot.Visibility = Visibility.Visible;

        DetailNameText.Text = row.Name;
        RunButton.IsEnabled = true;
        // Orchard disables Delete when any container uses the image.
        DeleteButton.IsEnabled = !_viewModel.SelectedImageInUse;

        InspectionErrorBar.IsOpen = _viewModel.InspectionError is not null;
        InspectionErrorBar.Message = _viewModel.InspectionError ?? "";

        var inspecting = _viewModel.IsInspecting;
        InspectingPanel.Visibility = inspecting ? Visibility.Visible : Visibility.Collapsed;
        InspectingRing.IsActive = inspecting;

        var selectionChanged = !string.Equals(_detailReference, row.Reference, StringComparison.Ordinal);
        _detailReference = row.Reference;

        BuildOverview(row);
        BuildTechnical(row, _viewModel.Inspection);
        if (selectionChanged || _viewModel.Inspection is not null || !inspecting)
            BuildConfiguration(_viewModel.Inspection);
        BuildUsersTable();
    }

    private void BuildOverview(ImageRow row)
    {
        OverviewHost.Children.Clear();
        OverviewHost.Children.Add(SectionTitle("Overview"));
        OverviewHost.Children.Add(CopyableRow("Reference", row.Reference));
        OverviewHost.Children.Add(InfoRow("Name", row.Name));
        OverviewHost.Children.Add(InfoRow("Tag", row.Tag));
        OverviewHost.Children.Add(InfoRow("Size", row.SizeText));
    }

    private void BuildTechnical(ImageRow row, ImageInspection? inspection)
    {
        TechnicalHost.Children.Clear();
        TechnicalHost.Children.Add(SectionTitle("Technical Details"));

        var mediaType = inspection?.MediaType
            ?? row.Image.Descriptor.MediaType
            ?? "—";
        if (string.IsNullOrEmpty(mediaType)) mediaType = "—";

        var digestRaw = inspection?.Digest ?? row.Image.Descriptor.Digest ?? "";
        var digest = digestRaw.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        if (digest.Length > 12) digest = digest[..12];
        if (string.IsNullOrEmpty(digest)) digest = "—";

        var sizeBytes = inspection?.Size ?? row.Image.Descriptor.Size;

        TechnicalHost.Children.Add(InfoRow("Media Type", mediaType));
        TechnicalHost.Children.Add(CopyableRow("Digest", digest));
        TechnicalHost.Children.Add(InfoRow("Size (bytes)", sizeBytes.ToString("N0")));
    }

    private void BuildConfiguration(ImageInspection? inspection)
    {
        ConfigurationHost.Children.Clear();
        if (inspection is null || inspection.Variants.Count == 0) return;

        foreach (var variant in inspection.Variants)
        {
            var card = new Border
            {
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush",
                    new SolidColorBrush(Color.FromArgb(20, 128, 128, 128))),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush",
                    new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
            };

            var body = new StackPanel { Spacing = 8 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = SectionTitle("Configuration");
            var platform = new TextBlock
            {
                Text = $"({variant.Platform})",
                FontSize = 13,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            var size = new TextBlock
            {
                Text = ByteFormat.String(variant.Size),
                FontSize = 12,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            };
            Grid.SetColumn(platform, 1);
            Grid.SetColumn(size, 3);
            header.Children.Add(title);
            header.Children.Add(platform);
            header.Children.Add(size);
            body.Children.Add(header);

            if (variant.Entrypoint is { Count: > 0 } ep)
                body.Children.Add(InfoRow("Entrypoint", string.Join(' ', ep)));
            if (variant.Cmd is { Count: > 0 } cmd)
                body.Children.Add(InfoRow("Cmd", string.Join(' ', cmd)));
            if (!string.IsNullOrEmpty(variant.WorkingDir))
                body.Children.Add(InfoRow("Working Dir", variant.WorkingDir!));
            if (!string.IsNullOrEmpty(variant.User))
                body.Children.Add(InfoRow("User", variant.User!));
            if (variant.ExposedPorts is { Count: > 0 } ports)
                body.Children.Add(InfoRow("Exposed Ports", string.Join(", ", ports)));
            if (variant.Volumes is { Count: > 0 } vols)
                body.Children.Add(InfoRow("Volumes", string.Join(", ", vols)));

            if (variant.Env is { Count: > 0 } env)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Environment",
                    FontSize = 12,
                    Opacity = 0.55,
                    Margin = new Thickness(0, 4, 0, 0),
                });

                var envBox = new Border
                {
                    Background = ThemeBrush("CardBackgroundFillColorSecondaryBrush",
                        new SolidColorBrush(Color.FromArgb(16, 128, 128, 128))),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    MaxHeight = 200,
                };
                var envScroll = new ScrollViewer { MaxHeight = 184 };
                var envStack = new StackPanel { Spacing = 4 };
                foreach (var entry in env)
                {
                    var eq = entry.IndexOf('=');
                    var key = eq >= 0 ? entry[..eq] : entry;
                    var val = eq >= 0 ? entry[(eq + 1)..] : "";
                    var line = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(val) ? key : $"{key} = {val}",
                        FontSize = 11,
                        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    };
                    envStack.Children.Add(line);
                }
                envScroll.Content = envStack;
                envBox.Child = envScroll;
                body.Children.Add(envBox);
            }

            card.Child = body;
            ConfigurationHost.Children.Add(card);
        }
    }

    private void BuildUsersTable()
    {
        UsersHost.Children.Clear();
        if (_viewModel is null) return;

        var users = _viewModel.ContainersUsingSelected;
        UsersEmptyText.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsersHeader.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var u in users)
        {
            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 8, 4, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nameBtn = new HyperlinkButton
            {
                Content = BuildContainerLinkContent(u),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = u.ContainerId,
            };
            nameBtn.Click += OnUserContainerClick;

            var addr = new TextBlock
            {
                Text = u.Address,
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                Foreground = u.Address is "N/A" or ""
                    ? ThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Colors.Gray))
                    : new SolidColorBrush(AccentLink),
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = true,
            };
            var host = new TextBlock
            {
                Text = u.Hostname,
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                Foreground = u.Hostname is "N/A" or ""
                    ? ThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Colors.Gray))
                    : new SolidColorBrush(AccentLink),
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = true,
            };

            Grid.SetColumn(addr, 1);
            Grid.SetColumn(host, 2);
            grid.Children.Add(nameBtn);
            grid.Children.Add(addr);
            grid.Children.Add(host);
            UsersHost.Children.Add(grid);

            UsersHost.Children.Add(new Border
            {
                Height = 1,
                Background = ThemeBrush("DividerStrokeColorDefaultBrush",
                    new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))),
                Margin = new Thickness(4, 0, 4, 0),
            });
        }
    }

    private static UIElement BuildContainerLinkContent(ImageUserRow u)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE7B8",
            FontSize = 12,
            Foreground = new SolidColorBrush(u.IsRunning
                ? Color.FromArgb(255, 107, 203, 119)
                : Color.FromArgb(255, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = u.DisplayName,
            FontSize = 13,
            Foreground = new SolidColorBrush(AccentLink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private void OnUserContainerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        App.MainWindow.NavigateTo("containers", selectContainerId: id);
    }

    // MARK: - List chrome handlers

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
    }

    private void InUseFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.ShowOnlyInUse = InUseFilterButton.IsChecked == true;
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SortAscending = !_viewModel.SortAscending;
        SortDirectionGlyph.Glyph = _viewModel.SortAscending ? "\uE74A" : "\uE74B";
    }

    private async void SearchDockerHubButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new ImageSearchDialog(_viewModel.Services) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void ImagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is null) return;
        _viewModel.SelectedRow = ImagesListView.SelectedItem as ImageRow;
    }

    private void OnCopyReferenceClick(object sender, RoutedEventArgs e)
    {
        var row = ImagesListView.SelectedItem as ImageRow ?? _viewModel?.SelectedRow;
        if (row is null) return;
        var package = new DataPackage();
        package.SetText(row.Reference);
        Clipboard.SetContent(package);
    }

    private void OnRemoveImageClick(object sender, RoutedEventArgs e)
    {
        var row = ImagesListView.SelectedItem as ImageRow ?? _viewModel?.SelectedRow;
        if (row is null) return;
        _viewModel?.DeleteCommand.Execute(row);
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRow is not { } row) return;
        var dialog = new RunContainerDialog(_viewModel.Services, row.Reference) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRow is not { } row) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete Image?",
            Content = $"Are you sure you want to delete '{row.Name}'? This action cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
            _viewModel.DeleteCommand.Execute(row);
    }

    // MARK: - Shared row builders

    private static TextBlock SectionTitle(string title) => new()
    {
        Text = title,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static UIElement InfoRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }

    private static UIElement CopyableRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new HyperlinkButton
        {
            Content = "Copy",
            FontSize = 11,
            Padding = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += (_, _) =>
        {
            var pkg = new DataPackage();
            pkg.SetText(value);
            Clipboard.SetContent(pkg);
        };
        Grid.SetColumn(v, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(l);
        grid.Children.Add(v);
        grid.Children.Add(copy);
        return grid;
    }

    private static Brush ThemeBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
                return brush;
        }
        catch
        {
            // ignore
        }
        return fallback;
    }
}
