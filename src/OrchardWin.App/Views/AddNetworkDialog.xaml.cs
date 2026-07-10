using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// A single editable key/value row in the "Labels" section, mirroring Orchard's
/// `AddNetworkView.NetworkLabel`. Plain observable state - no persistence of its own, just
/// two-way-bound `TextBox` targets for the dialog's `ItemsControl`.
public sealed partial class NetworkLabelEntry : ObservableObject
{
    [ObservableProperty]
    private string _key = "";

    [ObservableProperty]
    private string _value = "";
}

/// "Add Network" dialog: name (required, single DNS label), subnet (optional CIDR), and an
/// open-ended set of labels - mirrors `AddNetwork.swift` field-for-field. Constructed directly
/// by the page (not navigated to), so it takes <see cref="AppServices"/> in its constructor
/// rather than via `OnNavigatedTo`.
public sealed partial class AddNetworkDialog : ContentDialog
{
    private readonly NetworkService _networkService;
    private readonly ObservableCollection<NetworkLabelEntry> _labels = [];

    public AddNetworkDialog(AppServices services)
    {
        _networkService = services.NetworkService;
        InitializeComponent();

        LabelsList.ItemsSource = _labels;
        UpdateNoLabelsText();
        UpdatePrimaryButtonEnabled();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdatePrimaryButtonEnabled();

    private void UpdatePrimaryButtonEnabled() => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void UpdateNoLabelsText() => NoLabelsText.Visibility = _labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddLabelClick(object sender, RoutedEventArgs e)
    {
        _labels.Add(new NetworkLabelEntry());
        UpdateNoLabelsText();
    }

    private void OnRemoveLabelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NetworkLabelEntry entry })
        {
            _labels.Remove(entry);
            UpdateNoLabelsText();
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var trimmedName = NameBox.Text.Trim();
            if (trimmedName.Length == 0)
            {
                args.Cancel = true;
                return;
            }

            if (!InputValidation.IsValidNetworkName(trimmedName))
            {
                ShowError("Invalid network name. Use only alphanumeric characters and hyphens.");
                args.Cancel = true;
                return;
            }

            var trimmedSubnet = SubnetBox.Text.Trim();
            if (trimmedSubnet.Length > 0 && !InputValidation.IsValidSubnet(trimmedSubnet))
            {
                ShowError("Invalid subnet format. Use CIDR notation (e.g., 192.168.1.0/24).");
                args.Cancel = true;
                return;
            }

            foreach (var label in _labels)
            {
                if (label.Key.Trim().Length == 0 && label.Value.Trim().Length > 0)
                {
                    ShowError("Label key cannot be empty if value is provided.");
                    args.Cancel = true;
                    return;
                }
            }

            ErrorText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;

            var labelStrings = _labels
                .Where(l => l.Key.Trim().Length > 0)
                .Select(l => $"{l.Key.Trim()}={l.Value.Trim()}")
                .ToList();

            var created = await _networkService.CreateAsync(
                trimmedName,
                trimmedSubnet.Length == 0 ? null : trimmedSubnet,
                labelStrings);

            if (!created)
            {
                ShowError("Failed to create network. See the error banner for details.");
                args.Cancel = true;
            }
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
