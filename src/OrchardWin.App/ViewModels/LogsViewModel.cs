using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// What kind of resource a <see cref="LogTargetItem"/> points at - mirrors Swift's `LogTarget`
/// enum, letting one target picker list both containers and machines.
public enum LogTargetKind { Container, Machine }

/// One selectable entry in the Logs page's target picker.
public sealed record LogTargetItem(LogTargetKind Kind, string Id)
{
    // Disambiguates the two kinds in a single flat picker list - this port skips Swift's
    // sectioned "Containers"/"Machines" picker groups (see LogsPage's file header) in favor of
    // one flat list with a suffix.
    public string DisplayText => Kind == LogTargetKind.Machine ? $"{Id} (machine)" : Id;
}

/// Backs LogsPage: a single log pane's target selection, raw/filtered lines, and pause state.
/// Ported at reduced scope from Orchard's `MultiLogView.swift`'s `LogPaneView` - this port
/// shows one pane only (no split-view windowing; see LogsPage's file header) but keeps that
/// pane's own behavior 1:1: default to the first running container, poll while unpaused,
/// filter client-side, and drop a fetch result if the selection moved on while it was in
/// flight. The page's DispatcherTimer drives the ~2s poll by calling <see cref="RefreshAsync"/>;
/// this class holds no timer of its own (mirrors the rest of this app's pages, e.g.
/// DashboardPage owning its own poll timer rather than the ViewModel).
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<string> _rawLines = [];
    private bool _isFetching;

    [ObservableProperty]
    private ObservableCollection<LogTargetItem> _targets = [];

    [ObservableProperty]
    private LogTargetItem? _selectedTarget;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<string> _displayLines = [];

    /// "<n> matches" while a filter is active, else null - drives the filter bar's match-count
    /// label, matching Swift's `Text("\(displayLines.count) matches")`.
    [ObservableProperty]
    private string? _matchCountText;

    public LogsViewModel(AppServices services)
    {
        _services = services;

        // Rebuild the picker whenever either source list changes. ContainerListService
        // replaces its Containers collection wholesale on every load (a property-changed
        // signal); MachineService mutates its Machines collection in place (a
        // collection-changed signal) - see each service's own file for why.
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainerListService.Containers)) RebuildTargets();
        };
        _services.MachineService.Machines.CollectionChanged += (_, _) => RebuildTargets();

        RebuildTargets();
    }

    partial void OnSelectedTargetChanged(LogTargetItem? value)
    {
        _rawLines = [];
        DisplayLines = [];
        MatchCountText = null;
        _ = RefreshAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void RebuildTargets()
    {
        var previous = SelectedTarget;

        var items = new List<LogTargetItem>();
        foreach (var container in _services.ContainerListService.Containers)
            items.Add(new LogTargetItem(LogTargetKind.Container, container.Configuration.Id));
        foreach (var machine in _services.MachineService.Machines)
            items.Add(new LogTargetItem(LogTargetKind.Machine, machine.Id));
        Targets = new ObservableCollection<LogTargetItem>(items);

        if (previous is not null)
        {
            // Keep the same logical selection alive across a rebuild (record value-equality,
            // not reference - RebuildTargets always mints new item instances).
            var stillThere = items.FirstOrDefault(i => i == previous);
            if (stillThere is not null)
            {
                SelectedTarget = stillThere;
                return;
            }
        }

        if (SelectedTarget is null && items.Count > 0)
        {
            // Default to the first running container, else just the first entry - mirrors
            // MultiLogView's onAppear default.
            var runningContainer = _services.ContainerListService.Containers
                .FirstOrDefault(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase));
            SelectedTarget = runningContainer is not null
                ? items.FirstOrDefault(i => i.Kind == LogTargetKind.Container && i.Id == runningContainer.Configuration.Id)
                : items[0];
        }
    }

    /// Fetches the selected target's logs unless paused. Safe to call on every timer tick -
    /// re-entrant calls are dropped, and a result for a target the user has since switched away
    /// from is discarded, mirroring MultiLogView's post-await "did the selection change under
    /// me" guard.
    public async Task RefreshAsync()
    {
        if (IsPaused || _isFetching) return;
        var target = SelectedTarget;
        if (target is null) return;

        _isFetching = true;
        if (_rawLines.Count == 0) IsLoading = true;

        try
        {
            var lines = target.Kind == LogTargetKind.Machine
                ? await _services.MachineService.FetchLogsAsync(target.Id)
                : await _services.ContainerListService.FetchContainerLogsAsync(target.Id);

            if (target != SelectedTarget) return; // selection moved on mid-fetch

            _rawLines = [.. lines];
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (target != SelectedTarget) return;
            if (_rawLines.Count == 0)
            {
                _rawLines = [$"Error: {ex.Message}"];
                ApplyFilter();
            }
        }
        finally
        {
            IsLoading = false;
            _isFetching = false;
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText;
        List<string> filtered;
        if (string.IsNullOrEmpty(filter))
        {
            filtered = _rawLines;
            MatchCountText = null;
        }
        else
        {
            filtered = _rawLines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            MatchCountText = $"{filtered.Count} matches";
        }
        DisplayLines = new ObservableCollection<string>(filtered);
    }
}
