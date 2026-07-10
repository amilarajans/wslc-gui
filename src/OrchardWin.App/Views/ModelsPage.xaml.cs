using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// "Local AI & Sandboxes" - the Models tab (this page's own two-pane content) plus a nested
/// Sandboxes tab that navigates <see cref="SandboxesPage"/> into a child Frame the first time
/// it's shown. See the class remarks in ModelsPage.xaml for why the tabs live inside one page
/// rather than as separate NavigationView entries.
public sealed partial class ModelsPage : Page
{
    private ModelsViewModel? _viewModel;
    private AppServices? _services;
    private DispatcherTimer? _refreshTimer;
    private bool _sandboxesLoaded;

    public ModelsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;
        _viewModel = new ModelsViewModel(_services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Silent background poll driving both provider detection (ModelService has no
        // self-scheduled refresh - see its doc comment) and the default network's gateway
        // (for the "from containers" URL), mirroring the Dashboard's disk-refresh timer.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.RefreshQuietAsync();
        };
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    // MARK: - Tab switching

    private void ModelsTabButton_Click(object sender, RoutedEventArgs e)
    {
        ModelsTabButton.IsChecked = true;
        SandboxesTabButton.IsChecked = false;
        ModelsContent.Visibility = Visibility.Visible;
        SandboxesHostFrame.Visibility = Visibility.Collapsed;
    }

    private void SandboxesTabButton_Click(object sender, RoutedEventArgs e)
    {
        SandboxesTabButton.IsChecked = true;
        ModelsTabButton.IsChecked = false;
        ModelsContent.Visibility = Visibility.Collapsed;
        SandboxesHostFrame.Visibility = Visibility.Visible;

        if (!_sandboxesLoaded && _services is not null)
        {
            _sandboxesLoaded = true;
            SandboxesHostFrame.Navigate(typeof(SandboxesPage), _services);
        }
    }

    // MARK: - List selection

    private void ServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelRow row)
        {
            ProvidersList.SelectedItem = null;
            _viewModel.SelectedId = row.Id;
        }
        else if (ProvidersList.SelectedItem is null)
        {
            _viewModel.SelectedId = null;
        }
    }

    private void ProvidersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelRow row)
        {
            ServersList.SelectedItem = null;
            _viewModel.SelectedId = row.Id;
        }
        else if (ServersList.SelectedItem is null)
        {
            _viewModel.SelectedId = null;
        }
    }

    // MARK: - Actions

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new CreateModelServerDialog(_viewModel.Services.ModelServerService) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        string name;
        ushort port;
        ModelApiStyle api;
        string model;

        if (_viewModel.SelectedServer is { } server)
        {
            name = server.Model;
            port = server.Port;
            api = server.Api;
            model = server.Model;
        }
        else if (_viewModel.SelectedProvider is { } provider)
        {
            name = provider.Kind.DisplayName();
            port = provider.Port;
            api = provider.Api;
            model = provider.Models.FirstOrDefault() ?? "";
        }
        else
        {
            return;
        }

        var dialog = new TestModelPromptDialog(_viewModel.Services.ModelService, name, port, api, model) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _viewModel?.StopSelectedCommand.Execute(null);

    private void ShowLogButton_Click(object sender, RoutedEventArgs e) => _viewModel?.ShowLogCommand.Execute(null);

    // MARK: - Render

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        AddButton.IsEnabled = _viewModel.EngineAvailable;
        ToolTipService.SetToolTip(AddButton, _viewModel.EngineAvailable
            ? "Start a new model server"
            : "ollama is not installed");

        var isEmpty = _viewModel.ServerRows.Count == 0 && _viewModel.DetectedRows.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ListScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EngineHintPanel.Visibility = _viewModel.EngineAvailable ? Visibility.Collapsed : Visibility.Visible;

        ManagedSection.Visibility = _viewModel.ServerRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetectedSection.Visibility = _viewModel.DetectedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServersList.ItemsSource = _viewModel.ServerRows;
        ProvidersList.ItemsSource = _viewModel.DetectedRows;

        ApplyDetail();
    }

    private void ApplyDetail()
    {
        var vm = _viewModel!;
        var hasSelection = vm.SelectedServer is not null || vm.SelectedProvider is not null;
        DetailEmptyText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailScrollViewer.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSelection) return;

        if (vm.SelectedServer is { } server)
        {
            StatusDot.Fill = new SolidColorBrush(server.Status == ManagedModelServerStatus.Running ? Colors.Green : Colors.Red);
            DetailTitleText.Text = server.Model;
            DetailPortText.Text = $"port {server.Port}";
            OnMacUrlText.Text = $"http://{server.Host}:{server.Port}";

            if (server.ReachableFromContainers)
            {
                var gateway = vm.DefaultGateway;
                if (gateway is not null)
                {
                    ContainerUrlRow.Visibility = Visibility.Visible;
                    ContainerUrlText.Text = ModelBridge.ContainerBaseUrl(gateway, server.Port, server.Api);
                    ContainerUrlCaptionText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ContainerUrlRow.Visibility = Visibility.Collapsed;
                    ContainerUrlCaptionText.Text = "Default network gateway unavailable.";
                    ContainerUrlCaptionText.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ContainerUrlRow.Visibility = Visibility.Collapsed;
                ContainerUrlCaptionText.Text = "Loopback-only - bound to 127.0.0.1, so containers can't reach it.";
                ContainerUrlCaptionText.Visibility = Visibility.Visible;
            }

            ModelsListSection.Visibility = Visibility.Collapsed;
            NoModelsText.Visibility = Visibility.Collapsed;

            ChatButton.Visibility = server.Status == ManagedModelServerStatus.Running ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            ShowLogButton.Visibility = Visibility.Visible;
            FailedText.Visibility = server.Status == ManagedModelServerStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (vm.SelectedProvider is { } provider)
        {
            StatusDot.Fill = new SolidColorBrush(Colors.Gray);
            DetailTitleText.Text = provider.Kind.DisplayName();
            DetailPortText.Text = $"port {provider.Port}";
            OnMacUrlText.Text = provider.HostBaseUrl;

            var gateway = vm.DefaultGateway;
            if (gateway is not null)
            {
                ContainerUrlRow.Visibility = Visibility.Visible;
                ContainerUrlText.Text = ModelBridge.ContainerBaseUrl(gateway, provider.Port, provider.Api);
                ContainerUrlCaptionText.Text = "Reachable from containers only if this server is bound to 0.0.0.0 (some default to 127.0.0.1).";
                ContainerUrlCaptionText.Visibility = Visibility.Visible;
            }
            else
            {
                ContainerUrlRow.Visibility = Visibility.Collapsed;
                ContainerUrlCaptionText.Visibility = Visibility.Collapsed;
            }

            if (provider.Models.Count == 0)
            {
                ModelsListSection.Visibility = Visibility.Collapsed;
                NoModelsText.Visibility = Visibility.Visible;
            }
            else
            {
                ModelsListSection.Visibility = Visibility.Visible;
                NoModelsText.Visibility = Visibility.Collapsed;
                ModelsHeaderText.Text = $"Models ({provider.Models.Count})";
                ModelsListView.ItemsSource = provider.Models;
            }

            ChatButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
            ShowLogButton.Visibility = Visibility.Collapsed;
            FailedText.Visibility = Visibility.Collapsed;
        }
    }
}
