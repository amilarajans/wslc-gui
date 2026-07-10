using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// A workload recognised as a sandbox (a container wired to a local model), listed and
/// detailed here. Ported from Orchard's `SandboxesListView`/`SandboxDetailView`. Reachable
/// either as a top-level navigation (its own `AppServices` parameter, per the standard
/// contract) or nested inside <see cref="ModelsPage"/>'s "Sandboxes" tab, which navigates
/// into it the same way.
public sealed partial class SandboxesPage : Page
{
    private SandboxesViewModel? _viewModel;
    private DispatcherTimer? _refreshTimer;

    public SandboxesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new SandboxesViewModel(services);
        ManagedList.ItemsSource = _viewModel.ManagedRows;
        DetectedList.ItemsSource = _viewModel.DetectedRows;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(SandboxesViewModel.ManagedRows)
                or nameof(SandboxesViewModel.DetectedRows)
                or nameof(SandboxesViewModel.SelectedId)
                or nameof(SandboxesViewModel.SelectedSandbox))
                DispatcherQueue.RunOnUi(ApplyViewModelState);
        };
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Sandboxes are derived from the container list, which isn't guaranteed to be kept
        // fresh by any other always-on poller (unlike StatsService/ModelServerService), so
        // this page polls it directly while visible - mirroring the Dashboard's own
        // disk-refresh timer pattern.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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

    // MARK: - List selection

    private void ManagedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SandboxRow row)
        {
            DetectedList.SelectedItem = null;
            _viewModel.SelectedId = row.Id;
        }
        else if (DetectedList.SelectedItem is null)
        {
            _viewModel.SelectedId = null;
        }
    }

    private void DetectedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SandboxRow row)
        {
            ManagedList.SelectedItem = null;
            _viewModel.SelectedId = row.Id;
        }
        else if (ManagedList.SelectedItem is null)
        {
            _viewModel.SelectedId = null;
        }
    }

    // MARK: - Actions

    private async void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedSandbox is not { } sandbox) return;
        if (ChatTargetFor(sandbox) is not { } target) return;

        var dialog = new TestModelPromptDialog(_viewModel.Services.ModelService, sandbox.Name, target.Port, target.Api, "")
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e) => _viewModel?.OpenTerminalCommand.Execute(null);

    private void StopButton_Click(object sender, RoutedEventArgs e) => _viewModel?.StopSelectedCommand.Execute(null);

    /// Only offer chat for endpoints the tester speaks (OpenAI/Ollama) with a parseable port -
    /// mirrors Swift's `chatTargetFor`. The host portion of the endpoint is display-only:
    /// `ModelService.CompleteAsync` (like the Swift original) always talks to the provider on
    /// 127.0.0.1, since the chat tester runs on the host machine itself, not inside a
    /// container - only the port matters for the actual call.
    private static (ushort Port, ModelApiStyle Api)? ChatTargetFor(Sandbox sandbox)
    {
        if (sandbox.ModelEndpoint is not { } endpoint || sandbox.ChatApi is not { } api) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return null;
        var port = uri.Port;
        if (port <= 0 || port > 65535) return null;
        return ((ushort)port, api);
    }

    // MARK: - Render

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        var isEmpty = _viewModel.ManagedRows.Count == 0 && _viewModel.DetectedRows.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ListScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        ManagedSection.Visibility = _viewModel.ManagedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetectedSection.Visibility = _viewModel.DetectedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(ManagedList.ItemsSource, _viewModel.ManagedRows))
            ManagedList.ItemsSource = _viewModel.ManagedRows;
        if (!ReferenceEquals(DetectedList.ItemsSource, _viewModel.DetectedRows))
            DetectedList.ItemsSource = _viewModel.DetectedRows;

        ApplyDetail();
    }

    private void ApplyDetail()
    {
        var sandbox = _viewModel?.SelectedSandbox;
        var hasSelection = sandbox is not null;
        DetailEmptyText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailScrollViewer.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (sandbox is null) return;

        StatusDot.Fill = new SolidColorBrush(sandbox.IsRunning ? Colors.Green : Colors.Gray);
        KindIcon.Glyph = sandbox.Kind == SandboxKind.Container ? "" : "";
        DetailTitleText.Text = sandbox.Name;

        if (sandbox.IsIsolated)
        {
            // Literal brush, not a Resources["..."] indexer lookup: this page's own
            // Page.Resources dictionary is empty (no <Page.Resources> block declared), and
            // Fluent theme resources like SystemFillColorSuccessBrush only live in the
            // XamlControlsResources dictionary merged in App.xaml - a different
            // ResourceDictionary instance a plain code-behind indexer never walks up to.
            // Matches ContainersPage.xaml.cs's Well() precedent for the same pitfall.
            IsolationBadge.Background = new SolidColorBrush(Colors.SeaGreen);
            IsolationIcon.Glyph = "";
            IsolationText.Text = "Isolated";
        }
        else
        {
            IsolationBadge.Background = new SolidColorBrush(Colors.DarkOrange);
            IsolationIcon.Glyph = "";
            IsolationText.Text = "Egress open";
        }

        if (sandbox.ModelEndpoint is { } endpoint)
        {
            EndpointRow.Visibility = Visibility.Visible;
            EndpointText.Text = endpoint;
        }
        else
        {
            EndpointRow.Visibility = Visibility.Collapsed;
        }

        SourceText.Text = sandbox.Source == SandboxSource.Managed ? "Wired by Orchard-Win" : "Detected (env var)";

        ActionsRow.Visibility = sandbox.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        ChatButton.Visibility = sandbox.IsRunning && ChatTargetFor(sandbox) is not null ? Visibility.Visible : Visibility.Collapsed;

        IsolationExplanationText.Text = sandbox.IsIsolated
            ? "This container is on a host-only network: it can reach the model but has no internet access."
            : "This container's network allows internet access. For a no-egress sandbox, use a host-only network.";
    }
}
