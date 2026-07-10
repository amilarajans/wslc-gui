using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OrchardWin.App.Views;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel? _viewModel;
    private bool _isSyncing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var services = (AppServices)e.Parameter;
        _viewModel = new SettingsViewModel(services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isSyncing = true;
        try
        {
            TerminalCombo.ItemsSource = _viewModel.InstalledTerminals;
            TerminalCombo.SelectedItem = _viewModel.PreferredTerminal;

            DnsDomainCombo.ItemsSource = _viewModel.DnsDomains;
            DnsDomainCombo.SelectedItem = _viewModel.DnsDomains.FirstOrDefault(d => d.IsDefault);
        }
        finally
        {
            _isSyncing = false;
        }

        BinaryPathText.Text = _viewModel.ContainerBinaryPath;
        ResetBinaryButton.Visibility = _viewModel.IsUsingCustomBinary ? Visibility.Visible : Visibility.Collapsed;

        WslVersionText.Text = _viewModel.KernelInfo.WslVersion ?? "Unknown";
        KernelVersionText.Text = _viewModel.KernelInfo.KernelVersion ?? "Unknown";
        WindowsVersionText.Text = _viewModel.KernelInfo.WindowsVersion ?? "Unknown";

        SystemPropertiesList.ItemsSource = _viewModel.SystemProperties;
        NoPropertiesText.Visibility = _viewModel.SystemProperties.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isSyncing) return;
        if (TerminalCombo.SelectedItem is TerminalApp app) _viewModel.SetPreferredTerminal(app);
    }

    private async void OnDnsDomainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isSyncing) return;
        if (DnsDomainCombo.SelectedItem is DnsDomain domain) await _viewModel.SetDefaultDomainAsync(domain.Domain);
    }

    private async void OnChooseBinaryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");

        // FileOpenPicker needs a window handle in an unpackaged/WinUI3-desktop app - there is
        // no ambient "current window" the picker can infer, unlike UWP.
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (!_viewModel.ChooseBinaryPath(file.Path))
        {
            _viewModel.Services.AlertCenter.Error($"Selected file is not usable as wslc.exe: {file.Path}");
        }
    }

    private void OnResetBinaryClick(object sender, RoutedEventArgs e) => _viewModel?.ResetBinaryPath();
}
