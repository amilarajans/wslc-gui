using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// Two-pane Machines page (list | detail), porting ListMachines.swift/DetailMachine.swift/
/// MachineDetailHeader.swift/EditMachine.swift. Follows DashboardPage's pattern: the
/// ViewModel's PropertyChanged fans out to a single ApplyState() that repaints named
/// elements directly, rather than XAML data-binding the detail pane (Machine is a plain
/// Core record with no INotifyPropertyChanged of its own, so there is nothing to bind
/// incrementally to - a fresh instance replaces it on every refresh).
public sealed partial class MachinesPage : Page
{
    private AppServices? _services;
    private MachinesViewModel? _viewModel;

    /// Tracks which machine's boot-config fields were last seeded into the edit controls, so
    /// a background refresh (which re-runs ApplyDetailState via the view model's
    /// PropertyChanged) doesn't stomp on an in-progress, unsaved edit for the same machine.
    private string? _seededEditMachineId;

    public MachinesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new MachinesViewModel(_services);
        MachinesListView.ItemsSource = _viewModel.MachineRows;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // MachineRows mutates in place — only reapply chrome/detail, not full rebind storms.
            if (e.PropertyName is null
                or nameof(MachinesViewModel.MachineRows)
                or nameof(MachinesViewModel.SelectedMachineId)
                or nameof(MachinesViewModel.SelectedMachine)
                or nameof(MachinesViewModel.Service))
                DispatcherQueue.RunOnUi(ApplyState);
        };
        ApplyState();

        _ = _viewModel.LoadAsync();
    }

    private void ApplyState()
    {
        if (_viewModel is null) return;
        ApplyListState();
        ApplyDetailState();
    }

    private void ApplyListState()
    {
        var viewModel = _viewModel!;
        var service = viewModel.Service;
        var hasMachines = service.Machines.Count > 0;

        UnavailablePanel.Visibility = service.ApiUnavailable ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = !service.ApiUnavailable && service.IsLoading && !hasMachines
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = !service.ApiUnavailable && !service.IsLoading && !hasMachines
            ? Visibility.Visible : Visibility.Collapsed;
        MachinesListView.Visibility = !service.ApiUnavailable && hasMachines
            ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(MachinesListView.ItemsSource, viewModel.MachineRows))
            MachinesListView.ItemsSource = viewModel.MachineRows;

        // Restore selection by id when the selected row instance was replaced in-place.
        if (viewModel.SelectedMachineId is { } id)
        {
            var row = viewModel.MachineRows.FirstOrDefault(r => r.Id == id);
            if (!ReferenceEquals(MachinesListView.SelectedItem, row))
                MachinesListView.SelectedItem = row;
        }
    }

    private void ApplyDetailState()
    {
        var viewModel = _viewModel!;
        var machine = viewModel.SelectedMachine;

        if (machine is null)
        {
            NoSelectionPanel.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            _seededEditMachineId = null;
            return;
        }

        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        DetailNameText.Text = machine.Id;
        DefaultBadge.Visibility = machine.IsDefault ? Visibility.Visible : Visibility.Collapsed;
        DetailStatusText.Text = Capitalize(machine.Status);
        DetailStatusText.Foreground = new SolidColorBrush(machine.IsRunning ? Colors.Green : Colors.Gray);

        BootButton.Visibility = machine.IsStopped ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = machine.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        SetDefaultButton.IsEnabled = !machine.IsDefault;

        DetailImageText.Text = machine.ImageReference;
        DetailPlatformText.Text = $"{machine.Platform.Os}/{machine.Platform.Architecture}";
        DetailCpusText.Text = machine.Cpus.ToString();
        DetailMemoryText.Text = ByteFormat.Memory(machine.MemoryBytes);
        DetailDiskText.Text = machine.DiskSizeBytes is { } disk ? ByteFormat.String(disk) : "—";
        DetailHomeMountText.Text = machine.HomeMount;
        DetailVirtText.Text = machine.Virtualization ? "Enabled" : "Disabled";
        DetailIpText.Text = machine.IpAddress ?? "—";
        DetailUserText.Text = machine.UserSetup is { } user
            ? $"{user.Username} (uid {user.Uid}, gid {user.Gid})"
            : "—";
        DetailContainerIdText.Text = machine.ContainerId ?? "—";
        DetailInitializedText.Text = machine.Initialized ? "Yes" : "No";
        DetailCreatedText.Text = machine.CreatedDate is { } created ? created.LocalDateTime.ToString("g") : "—";
        DetailStartedText.Text = machine.StartedDate is { } started ? started.LocalDateTime.ToString("g") : "—";

        RestartNowCheck.IsEnabled = machine.IsRunning;

        if (_seededEditMachineId != machine.Id)
        {
            _seededEditMachineId = machine.Id;

            var memoryGiB = Math.Max(1, (int)(machine.MemoryBytes / 1_073_741_824));
            EditCpusBox.Maximum = Math.Max(Environment.ProcessorCount, machine.Cpus);
            EditCpusBox.Value = machine.Cpus;
            EditMemoryBox.Maximum = Math.Max(EstimateHostMemoryGiB(), memoryGiB);
            EditMemoryBox.Value = memoryGiB;

            EditHomeMountRoRadio.IsChecked = machine.HomeMount == "ro";
            EditHomeMountNoneRadio.IsChecked = machine.HomeMount == "none";
            EditHomeMountRwRadio.IsChecked = machine.HomeMount != "ro" && machine.HomeMount != "none";

            EditVirtualizationCheck.IsChecked = machine.Virtualization;
            RestartNowCheck.IsChecked = false;
            ApplyErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void MachinesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;

        // Only track a *real* selection. The ListView's SelectedItem also goes briefly null
        // whenever MachineRows is rebuilt on a background refresh (a fresh collection
        // instance replaces ItemsSource - see ApplyListState); ignore that transient
        // deselection rather than clearing the detail pane on every poll tick.
        if (MachinesListView.SelectedItem is MachineRow row)
        {
            _viewModel.SelectedMachineId = row.Id;
        }
    }

    private async void CreateMachineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var dialog = new CreateMachineDialog(_services) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void BootButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.BootSelectedAsync();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.StopSelectedAsync();
    }

    private async void SetDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SetDefaultSelectedAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMachine is not { } machine) return;

        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Delete Machine",
            Content = $"Are you sure you want to delete '{machine.Id}'? This permanently removes the machine and its persistent storage.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.DeleteSelectedAsync();
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        var cpus = (int)EditCpusBox.Value;
        var memoryGiB = (int)EditMemoryBox.Value;

        if (cpus < 1)
        {
            ShowApplyError("CPUs must be a positive number.");
            return;
        }
        if (memoryGiB < 1)
        {
            ShowApplyError("Memory must be at least 1 GB.");
            return;
        }

        var homeMount = EditHomeMountRoRadio.IsChecked == true ? "ro"
            : EditHomeMountNoneRadio.IsChecked == true ? "none"
            : "rw";

        var config = new MachineConfigSpec
        {
            Cpus = cpus,
            MemoryGiB = memoryGiB,
            HomeMount = homeMount,
            Virtualization = EditVirtualizationCheck.IsChecked == true,
        };

        ApplyErrorText.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = false;
        try
        {
            var restartNow = RestartNowCheck.IsChecked == true;
            var ok = await _viewModel.ApplyConfigAsync(config, restartNow);
            if (!ok) ShowApplyError("Failed to update machine. See the alert for details.");
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private void ShowApplyError(string message)
    {
        ApplyErrorText.Text = message;
        ApplyErrorText.Visibility = Visibility.Visible;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static int EstimateHostMemoryGiB()
    {
        // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is a rough proxy for total host RAM -
        // portable .NET has no direct equivalent to Swift's ProcessInfo.physicalMemory. Good
        // enough as an upper bound for a memory stepper, not an exact reading.
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Max(1, (int)(bytes / 1_073_741_824));
    }
}
