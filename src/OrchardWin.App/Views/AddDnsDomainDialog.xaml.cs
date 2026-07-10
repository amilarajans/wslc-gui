using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// "Add DNS Domain" dialog: a single domain-name field, mirroring `AddDNS.swift`. Validation
/// is basic per the port's brief - non-empty, no internal whitespace, and the same
/// <see cref="InputValidation.IsValidDomainName"/> shape check the Swift original used.
/// Constructed directly by the page (not navigated to), so it takes <see cref="AppServices"/>
/// in its constructor rather than via `OnNavigatedTo`.
public sealed partial class AddDnsDomainDialog : ContentDialog
{
    private readonly DnsService _dnsService;

    public AddDnsDomainDialog(AppServices services)
    {
        _dnsService = services.DnsService;
        InitializeComponent();

        UpdatePrimaryButtonEnabled();
    }

    private void OnDomainChanged(object sender, TextChangedEventArgs e) => UpdatePrimaryButtonEnabled();

    private void UpdatePrimaryButtonEnabled() => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(DomainBox.Text);

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var trimmed = DomainBox.Text.Trim();
            if (trimmed.Length == 0)
            {
                args.Cancel = true;
                return;
            }

            if (trimmed.Any(char.IsWhiteSpace) || !InputValidation.IsValidDomainName(trimmed))
            {
                ShowError("Invalid domain name format.");
                args.Cancel = true;
                return;
            }

            ErrorText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;

            var created = await _dnsService.CreateAsync(trimmed);
            if (!created)
            {
                ShowError("Failed to add domain. See the error banner for details.");
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
