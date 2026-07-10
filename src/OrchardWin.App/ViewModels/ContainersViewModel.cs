using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the container list: the raw container plus the pre-computed display fields the
/// page's ListItemRow template binds to directly (no value converters needed in XAML),
/// mirroring how DashboardViewModel's `UtilisationRow` pre-computes its row's display fields
/// rather than converting in XAML.
public sealed record ContainerRowVm(
    Container Container,
    string PrimaryText,
    string SecondaryLeftText,
    string? SecondaryRightText,
    Color IconColor,
    bool ShowSandboxBadge)
{
    public string Id => Container.Configuration.Id;
}

/// Thin glue for ContainersPage: selection/filter/sort UI state and command wiring over
/// AppServices.ContainerListService (which already owns the container list and its
/// start/stop/remove lifecycle) and AppServices.StatsService (which already owns the
/// sampling/history for the selected container's resource sparklines). Mirrors Orchard's
/// `ContainersListView` filtering/sorting behaviour (ListContainers.swift).
public sealed partial class ContainersViewModel : ObservableObject
{
    private readonly AppServices _services;

    public AppServices Services => _services;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showOnlyRunning;

    [ObservableProperty]
    private ContainerSortOption _sortOption = ContainerSortOption.Name;

    [ObservableProperty]
    private ObservableCollection<Container> _filteredContainers = [];

    [ObservableProperty]
    private ObservableCollection<ContainerRowVm> _containerRows = [];

    [ObservableProperty]
    private Container? _selectedContainer;

    /// Ids the list view currently has multi-selected - the context menu's "target set" when
    /// more than one row is selected, mirroring Swift's `selectedContainers: Set<String>`.
    /// Owned by the page (which drives ListView.SelectedItems); the ViewModel only reads it
    /// when building the target list for a bulk action.
    public HashSet<string> SelectedIds { get; } = [];

    public ContainersViewModel(AppServices services)
    {
        _services = services;
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainerListService.Containers))
            {
                Refresh();
                ReconcileSelectedContainer();
            }
        };
        _services.StatsService.PropertyChanged += (_, _) => RaiseStatsChanged();
    }

    public Task LoadAsync() => _services.ContainerListService.LoadAsync(showLoading: true);

    /// Quiet background poll - mirrors the Swift view's onAppear refresh loop.
    /// ContainerListService has no self-timer of its own (unlike StatsService), so the page
    /// drives this on a DispatcherTimer while visible.
    public Task PollAsync() => _services.ContainerListService.LoadAsync(showLoading: false);

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnShowOnlyRunningChanged(bool value) => Refresh();
    partial void OnSortOptionChanged(ContainerSortOption value) => Refresh();
    partial void OnSelectedContainerChanged(Container? value) => RaiseStatsChanged();

    public void Refresh()
    {
        IEnumerable<Container> containers = _services.ContainerListService.Containers;

        if (ShowOnlyRunning)
        {
            containers = containers.Where(IsRunning);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            containers = containers.Where(c =>
                c.Configuration.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.Status.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (Hostname(c)?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        List<Container> sorted = SortOption switch
        {
            ContainerSortOption.Name => [.. containers.OrderBy(c => c.Configuration.Id, StringComparer.OrdinalIgnoreCase)],
            ContainerSortOption.Status => [.. containers.OrderBy(c => c.Status, StringComparer.OrdinalIgnoreCase)],
            ContainerSortOption.Image => [.. containers.OrderBy(c => c.Configuration.Image.Reference, StringComparer.OrdinalIgnoreCase)],
            _ => [.. containers],
        };

        // Stable "running first" partition, matching Orchard's default `runningFirst` toggle.
        var running = sorted.Where(IsRunning).ToList();
        var notRunning = sorted.Where(c => !IsRunning(c)).ToList();
        var ordered = new List<Container>(running.Count + notRunning.Count);
        ordered.AddRange(running);
        ordered.AddRange(notRunning);

        FilteredContainers = new ObservableCollection<Container>(ordered);
        ContainerRows = new ObservableCollection<ContainerRowVm>(ordered.Select(BuildRow));
    }

    private static ContainerRowVm BuildRow(Container c) => new(
        Container: c,
        PrimaryText: c.Configuration.Id,
        SecondaryLeftText: NetworkAddress(c) ?? "-",
        SecondaryRightText: Hostname(c),
        IconColor: IsRunning(c) ? Colors.LimeGreen : Colors.Gray,
        ShowSandboxBadge: c.IsSandbox());

    /// After a reload, re-point SelectedContainer at the fresh instance with the same id (the
    /// service always publishes new `Container` records) so the detail pane keeps showing the
    /// same container instead of silently going blank.
    private void ReconcileSelectedContainer()
    {
        if (SelectedContainer is null) return;
        var id = SelectedContainer.Configuration.Id;
        SelectedContainer = _services.ContainerListService.Containers.FirstOrDefault(c => c.Configuration.Id == id);
    }

    private void RaiseStatsChanged()
    {
        OnPropertyChanged(nameof(SelectedCpuPercentText));
        OnPropertyChanged(nameof(SelectedMemoryPercentText));
        OnPropertyChanged(nameof(SelectedCpuHistory));
        OnPropertyChanged(nameof(SelectedMemoryHistory));
    }

    public static bool IsRunning(Container c) => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase);

    public static string? NetworkAddress(Container c) =>
        c.Networks.Count > 0 ? c.Networks[0].Address.StrippingCidrSuffix() : null;

    public static string? Hostname(Container c)
    {
        if (c.Networks.Count == 0) return null;
        var hostname = c.Networks[0].Hostname;
        return hostname.EndsWith('.') ? hostname[..^1] : hostname;
    }

    // MARK: - Selected-container live stats (drives the detail pane's resource sparklines)

    private StatsSample? SelectedSample =>
        SelectedContainer is null ? null : _services.StatsService.LatestSamples.GetValueOrDefault(SelectedContainer.Configuration.Id);

    public string SelectedCpuPercentText => SelectedSample is { } s ? $"{s.CpuPercent:F0}%" : "--";
    public string SelectedMemoryPercentText => SelectedSample is { } s ? $"{s.MemoryPercent:F0}%" : "--";

    public IReadOnlyList<double> SelectedCpuHistory => RecentHistory().Select(s => s.CpuPercent).ToList();
    public IReadOnlyList<double> SelectedMemoryHistory => RecentHistory().Select(s => s.MemoryPercent).ToList();

    private IReadOnlyList<StatsSample> RecentHistory()
    {
        if (SelectedContainer is null) return [];
        var history = _services.StatsService.History.Samples(new StatsKey(SelectedContainer.Configuration.Id));
        return history.Count > 60 ? history.TakeLast(60).ToList() : history;
    }

    // MARK: - Lifecycle actions on the selected container

    private bool HasSelection => SelectedContainer is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task StartSelectedAsync() =>
        SelectedContainer is null ? Task.CompletedTask : _services.ContainerListService.StartContainerAsync(SelectedContainer.Configuration.Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task StopSelectedAsync() =>
        SelectedContainer is null ? Task.CompletedTask : _services.ContainerListService.StopContainerAsync(SelectedContainer.Configuration.Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ForceStopSelectedAsync() =>
        SelectedContainer is null ? Task.CompletedTask : _services.ContainerListService.ForceStopContainerAsync(SelectedContainer.Configuration.Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RemoveSelectedAsync() =>
        SelectedContainer is null ? Task.CompletedTask : _services.ContainerListService.RemoveContainerAsync(SelectedContainer.Configuration.Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenTerminalSelectedAsync() =>
        SelectedContainer is null ? Task.CompletedTask : _services.TerminalLauncher.OpenShellAsync(ShellTargetKind.Container, SelectedContainer.Configuration.Id);

    // MARK: - Bulk actions (right-click context menu, possibly multi-selected)

    public Task StartManyAsync(IReadOnlyList<string> ids) => RunForEachAsync(ids, id => _services.ContainerListService.StartContainerAsync(id));

    public Task StopManyAsync(IReadOnlyList<string> ids) => RunForEachAsync(ids, id => _services.ContainerListService.StopContainerAsync(id));

    public Task ForceStopManyAsync(IReadOnlyList<string> ids) => RunForEachAsync(ids, id => _services.ContainerListService.ForceStopContainerAsync(id));

    public Task RemoveManyAsync(IReadOnlyList<string> ids) => _services.ContainerListService.RemoveContainersAsync(ids);

    private static async Task RunForEachAsync(IReadOnlyList<string> ids, Func<string, Task> action)
    {
        foreach (var id in ids)
        {
            await action(id);
        }
    }
}
