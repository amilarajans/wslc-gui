using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace OrchardWin.App.Views;

/// Flattened, x:Bind-friendly view of one <see cref="ImageInspectionVariant"/> - joins the
/// list-valued fields (Entrypoint/Cmd/Env) into display strings and exposes a Has* bool per
/// optional field so the row template can collapse empty ones, since x:Bind has no
/// string-join/null-check expression syntax of its own.
public sealed class ImageVariantDisplay
{
    public required string Platform { get; init; }
    public required string SizeText { get; init; }
    public string EntrypointText { get; init; } = "";
    public string CmdText { get; init; } = "";
    public string EnvText { get; init; } = "";
    public string WorkingDirText { get; init; } = "";
    public string UserText { get; init; } = "";

    public bool HasEntrypoint => !string.IsNullOrEmpty(EntrypointText);
    public bool HasCmd => !string.IsNullOrEmpty(CmdText);
    public bool HasEnv => !string.IsNullOrEmpty(EnvText);
    public bool HasWorkingDir => !string.IsNullOrEmpty(WorkingDirText);
    public bool HasUser => !string.IsNullOrEmpty(UserText);

    public static ImageVariantDisplay From(ImageInspectionVariant variant) => new()
    {
        Platform = variant.Platform,
        SizeText = ByteFormat.String(variant.Size),
        EntrypointText = variant.Entrypoint is { Count: > 0 } e ? $"Entrypoint: {string.Join(' ', e)}" : "",
        CmdText = variant.Cmd is { Count: > 0 } c ? $"Cmd: {string.Join(' ', c)}" : "",
        EnvText = variant.Env is { Count: > 0 } env ? $"Env: {string.Join(", ", env)}" : "",
        WorkingDirText = string.IsNullOrEmpty(variant.WorkingDir) ? "" : $"Working Dir: {variant.WorkingDir}",
        UserText = string.IsNullOrEmpty(variant.User) ? "" : $"User: {variant.User}",
    };
}

public sealed partial class ImagesPage : Page
{
    private AppServices? _services;
    private ImagesViewModel? _viewModel;
    private DispatcherTimer? _refreshTimer;

    // Guards against ImagesListView_SelectionChanged reacting to the programmatic
    // ItemsSource/SelectedItem sync in ApplyViewModelState - every ViewModel.Refresh() (e.g.
    // the 5s background poll) reassigns Rows to a brand-new collection, which would otherwise
    // momentarily null out then re-set the ListView's selection and fire a redundant
    // SelectedRow round-trip (re-triggering an InspectAsync call) on every single poll tick.
    private bool _isSyncingSelection;

    public ImagesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new ImagesViewModel(_services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);

        SortCombo.SelectedIndex = 0;
        SortDirectionGlyph.Glyph = ""; // ascending

        ApplyViewModelState();
        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ImageService has no self-refresh timer of its own (unlike ContainerListService's
        // polling) - Orchard's Content.swift keeps the image list fresh with its own 5s
        // repeating timer while the images view is visible, so mirror that here rather than
        // leaving the list to go stale until the user manually refreshes.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_services is not null) await _services.ImageService.LoadAsync(showLoading: false);
        };
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isSyncingSelection = true;
        ImagesListView.ItemsSource = _viewModel.Rows;
        ImagesListView.SelectedItem = _viewModel.SelectedRow;
        _isSyncingSelection = false;

        ImagesLoadingRing.IsActive = _services?.ImageService.IsImagesLoading ?? false;

        var row = _viewModel.SelectedRow;
        NoSelectionText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroller.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;

        if (row is null) return;

        DetailNameText.Text = row.Name;
        DetailReferenceText.Text = row.Reference;
        RunButton.IsEnabled = true;
        DeleteButton.IsEnabled = !_viewModel.SelectedImageInUse;

        InspectingRing.IsActive = _viewModel.IsInspecting;

        InspectionErrorBar.IsOpen = _viewModel.InspectionError is not null;
        InspectionErrorBar.Message = _viewModel.InspectionError ?? "";

        var inspection = _viewModel.Inspection;
        InspectionPanel.Visibility = inspection is not null ? Visibility.Visible : Visibility.Collapsed;
        if (inspection is null) return;

        DigestText.Text = inspection.Digest;
        MediaTypeText.Text = inspection.MediaType;
        InspectSizeText.Text = ByteFormat.String(inspection.Size);
        VariantsList.ItemsSource = inspection.Variants.Select(ImageVariantDisplay.From).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<ImageSortOption>(tag, out var option))
        {
            _viewModel.SortBy = option;
        }
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SortAscending = !_viewModel.SortAscending;
        SortDirectionGlyph.Glyph = _viewModel.SortAscending ? "" : "";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel?.RefreshCommand.Execute(null);

    private async void SearchDockerHubButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var dialog = new ImageSearchDialog(_services) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }

    private void ImagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection) return;
        if (_viewModel is not null) _viewModel.SelectedRow = ImagesListView.SelectedItem as ImageRow;
    }

    private void OnCopyReferenceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageRow row) return;
        var package = new DataPackage();
        package.SetText(row.Reference);
        Clipboard.SetContent(package);
    }

    private void OnRemoveImageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageRow row) return;
        _viewModel?.DeleteCommand.Execute(row);
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: ContainersPage currently only accepts a plain AppServices navigation
        // parameter (see MainWindow.Navigate / the page-navigation contract), so there is no
        // channel yet to hand it a pre-filled image reference. If the containers-page agent
        // adds support for an optional (AppServices Services, string ImageReference) tuple
        // parameter, switch this to Frame.Navigate(typeof(ContainersPage), (_services,
        // row.Reference)) and drop this comment. Until then, this just opens Containers and
        // the user pastes/selects the image reference manually in Run Container's image field.
        if (_services is null || _viewModel?.SelectedRow is null) return;
        Frame.Navigate(typeof(ContainersPage), _services);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRow is not { } row) return;

        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Delete Image?",
            Content = $"Are you sure you want to delete '{row.Name}'? This action cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _viewModel.DeleteCommand.Execute(row);
        }
    }
}
