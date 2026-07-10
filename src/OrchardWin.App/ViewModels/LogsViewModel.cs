using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

public enum LogTargetKind { Container, Machine }

public sealed record LogTargetItem(LogTargetKind Kind, string Id, string DisplayText)
{
    // Equality by kind+id so selection survives rebuilds that mint new display strings.
    public bool SameAs(LogTargetItem? other) =>
        other is not null && Kind == other.Kind
        && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
}

/// Single log pane: target picker + filtered lines. Collections are mutated in place so the
/// 2s poll does not thrash ComboBox/ListView ItemsSource.
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<string> _rawLines = [];
    private bool _isFetching;
    private string? _pendingSelectId;

    public ObservableCollection<LogTargetItem> Targets { get; } = [];
    public ObservableCollection<string> DisplayLines { get; } = [];

    [ObservableProperty]
    private LogTargetItem? _selectedTarget;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _matchCountText;

    [ObservableProperty]
    private string? _statusMessage;

    /// Bumped when log text content changes so the page can scroll without rebinding always.
    [ObservableProperty]
    private int _linesRevision;

    public LogsViewModel(AppServices services)
    {
        _services = services;
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
                RebuildTargets();
        };
        _services.MachineService.Machines.CollectionChanged += (_, _) => RebuildTargets();
        RebuildTargets();
    }

    /// Prefer this container when opening Logs from Containers detail.
    public void PreferContainer(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return;
        _pendingSelectId = containerId;
        TryApplyPendingSelection();
        _ = RefreshAsync();
    }

    partial void OnSelectedTargetChanged(LogTargetItem? value)
    {
        _rawLines = [];
        DisplayLines.Clear();
        MatchCountText = null;
        StatusMessage = null;
        LinesRevision++;
        _ = RefreshAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void RebuildTargets()
    {
        var previous = SelectedTarget;
        var items = new List<LogTargetItem>();

        foreach (var container in _services.ContainerListService.Containers)
        {
            var id = container.Configuration.Id;
            var name = !string.IsNullOrWhiteSpace(container.Configuration.Hostname)
                ? container.Configuration.Hostname!
                : (id.Length > 12 ? id[..12] : id);
            var status = container.Status;
            var label = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name} ({status})";
            // Prefer name for the CLI when available (wslc accepts either).
            var cliId = !string.IsNullOrWhiteSpace(container.Configuration.Hostname)
                ? container.Configuration.Hostname!
                : id;
            items.Add(new LogTargetItem(LogTargetKind.Container, cliId, label));
        }

        foreach (var machine in _services.MachineService.Machines)
            items.Add(new LogTargetItem(LogTargetKind.Machine, machine.Id, $"{machine.Id} (machine)"));

        if (ObservableCollectionSync.Sync(Targets, items, (a, b) => a.SameAs(b) && a.DisplayText == b.DisplayText))
            OnPropertyChanged(nameof(Targets));

        if (TryApplyPendingSelection())
            return;

        if (previous is not null)
        {
            var stillThere = items.FirstOrDefault(i => i.SameAs(previous));
            if (stillThere is not null)
            {
                if (SelectedTarget is null || !SelectedTarget.SameAs(stillThere))
                    SelectedTarget = stillThere;
                return;
            }
        }

        if (SelectedTarget is null && items.Count > 0)
        {
            var running = items.FirstOrDefault(i =>
                i.Kind == LogTargetKind.Container && !i.DisplayText.Contains('(', StringComparison.Ordinal));
            SelectedTarget = running ?? items[0];
        }
    }

    private bool TryApplyPendingSelection()
    {
        if (string.IsNullOrEmpty(_pendingSelectId)) return false;
        var match = Targets.FirstOrDefault(t =>
            string.Equals(t.Id, _pendingSelectId, StringComparison.OrdinalIgnoreCase)
            || t.DisplayText.StartsWith(_pendingSelectId, StringComparison.OrdinalIgnoreCase)
            || t.DisplayText.Contains(_pendingSelectId, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        _pendingSelectId = null;
        if (!SelectedTarget.SameAs(match))
            SelectedTarget = match;
        return true;
    }

    public async Task RefreshAsync()
    {
        if (IsPaused || _isFetching) return;
        var target = SelectedTarget;
        if (target is null)
        {
            StatusMessage = "Select a container or machine above";
            return;
        }

        _isFetching = true;
        if (_rawLines.Count == 0)
        {
            IsLoading = true;
            StatusMessage = "Loading logs...";
        }

        try
        {
            IReadOnlyList<string> lines;
            if (target.Kind == LogTargetKind.Machine)
            {
                lines = await _services.MachineService.FetchLogsAsync(target.Id);
            }
            else
            {
                lines = await _services.ContainerListService.FetchContainerLogsAsync(target.Id);
            }

            if (!SelectedTarget.SameAs(target)) return;

            // Skip UI work when the tail is unchanged.
            if (_rawLines.Count == lines.Count
                && _rawLines.Count > 0
                && _rawLines[^1] == lines[^1]
                && _rawLines[0] == lines[0])
            {
                StatusMessage = null;
                return;
            }

            _rawLines = [.. lines];
            ApplyFilter();
            StatusMessage = lines.Count == 0
                ? "No log output yet (container may not have written anything)."
                : null;
        }
        catch (Exception ex)
        {
            if (!SelectedTarget.SameAs(target)) return;
            Log.Ui.Error($"logs fetch failed for {target.Id}: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
            if (_rawLines.Count == 0)
            {
                _rawLines = [$"Error loading logs for '{target.DisplayText}':", ex.Message];
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

        if (ObservableCollectionSync.Sync(DisplayLines, filtered, (a, b) => a == b))
        {
            OnPropertyChanged(nameof(DisplayLines));
            LinesRevision++;
        }
        else if (filtered.Count == 0 && DisplayLines.Count == 0)
        {
            LinesRevision++; // still notify empty state
        }
    }
}
