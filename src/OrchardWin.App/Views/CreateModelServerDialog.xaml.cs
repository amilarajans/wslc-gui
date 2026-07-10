using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// Starts a new managed model server via <see cref="ModelServerService.Start"/>. Ported from
/// Orchard's `CreateModelServerView` - a model id, a port, and the bind-address control that
/// decides whether containers can reach it. On failure the service already surfaces an
/// AlertCenter error; this dialog just stays open (mirrors the Swift original's `if start()
/// { dismiss() }` - only close on success).
public sealed partial class CreateModelServerDialog : ContentDialog
{
    private readonly ModelServerService _modelServerService;

    public CreateModelServerDialog(ModelServerService modelServerService)
    {
        _modelServerService = modelServerService;
        InitializeComponent();

        EngineWarningBar.IsOpen = !_modelServerService.EngineAvailable;
        IsPrimaryButtonEnabled = false;
        UpdateBindHint();
        Loaded += (_, _) => Revalidate();
    }

    private void OnFieldChanged(object sender, object e)
    {
        UpdateBindHint();
        Revalidate();
    }

    private void UpdateBindHint()
    {
        BindHintText.Text = AllowContainersCheck.IsChecked == true
            ? "Bound to all interfaces, so containers can reach it over their network gateway."
            : "Bound to 127.0.0.1 - reachable only from this PC, not from containers.";
    }

    private void Revalidate()
    {
        var modelOk = !string.IsNullOrWhiteSpace(ModelBox.Text);
        var portOk = ushort.TryParse(PortBox.Text, out _);
        IsPrimaryButtonEnabled = modelOk && portOk && _modelServerService.EngineAvailable;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ushort.TryParse(PortBox.Text, out var port))
        {
            args.Cancel = true;
            return;
        }

        var host = AllowContainersCheck.IsChecked == true ? "0.0.0.0" : "127.0.0.1";
        var started = _modelServerService.Start(ModelBox.Text.Trim(), host, port);
        if (!started)
        {
            // The service already routed the failure to AlertCenter - keep the dialog open
            // so the user can adjust and retry, matching the Swift original.
            args.Cancel = true;
        }
    }
}
