using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// Ported from Orchard's `RunContainerView` + `ContainerConfigForm` (run mode only - this
/// dialog only ever creates a container, so the Swift form's "edit mode" branches have no
/// counterpart here). A tabbed form (Basic/Ports/Volumes/Environment/Advanced) building a
/// <see cref="ContainerRunConfig"/>, submitted via <see cref="ContainerListService.RunContainerAsync"/>.
public sealed partial class RunContainerDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly List<PortRow> _portRows = [];
    private readonly List<VolumeRow> _volumeRows = [];
    private readonly List<EnvRow> _envRows = [];

    public RunContainerDialog(AppServices services, string imageName)
    {
        _services = services;
        InitializeComponent();

        ImageBox.Text = imageName;
        NameBox.Text = DefaultNameFrom(imageName);

        Loaded += async (_, _) => await LoadPickersAsync();
    }

    private static string DefaultNameFrom(string imageName)
    {
        var cleaned = imageName
            .Replace("docker.io/library/", "")
            .Replace("docker.io/", "");
        var withoutTag = cleaned.Split(':')[0];
        return string.IsNullOrEmpty(withoutTag) ? "container" : withoutTag;
    }

    private async Task LoadPickersAsync()
    {
        await _services.NetworkService.LoadAsync(showLoading: false);
        await _services.DnsService.LoadAsync(showLoading: false);
        await _services.ModelService.LoadAsync(showLoading: false);

        DnsDomainCombo.Items.Clear();
        DnsDomainCombo.Items.Add(new DnsDomain { Domain = "None" });
        foreach (var domain in _services.DnsService.DnsDomains) DnsDomainCombo.Items.Add(domain);
        var defaultDomain = _services.DnsService.DnsDomains.FirstOrDefault(d => d.IsDefault);
        DnsDomainCombo.SelectedItem = defaultDomain is not null
            ? DnsDomainCombo.Items.OfType<DnsDomain>().First(d => d.Domain == defaultDomain.Domain)
            : DnsDomainCombo.Items[0];

        NetworkCombo.Items.Clear();
        NetworkCombo.Items.Add("Default");
        foreach (var network in _services.NetworkService.Networks) NetworkCombo.Items.Add(network.Id);
        NetworkCombo.SelectedIndex = 0;

        BridgeProviderCombo.Items.Clear();
        BridgeProviderCombo.Items.Add("None");
        foreach (var provider in _services.ModelService.Providers)
        {
            BridgeProviderCombo.Items.Add(provider);
        }
        BridgeProviderCombo.SelectedIndex = 0;
        BridgeSection.Visibility = _services.ModelService.Providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ValidateName();
        UpdateCommandHint();
    }

    // MARK: - Name validation

    private void OnNameChanged(object sender, TextChangedEventArgs e) => ValidateName();

    private void ValidateName()
    {
        var name = NameBox.Text.Trim();
        string? error = null;
        if (!string.IsNullOrEmpty(name))
        {
            if (!InputValidation.IsValidContainerName(name))
            {
                error = "Container name can only contain letters, numbers, underscores, periods and dashes, and must start with a letter or number.";
            }
            else if (name.Length > 63)
            {
                error = "Container name must be 63 characters or less.";
            }
            else if (_services.ContainerListService.Containers.Any(c => c.Configuration.Id == name))
            {
                error = "A container with this name already exists.";
            }
        }

        NameErrorText.Text = error ?? "";
        NameErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = error is null && !string.IsNullOrEmpty(name) && !string.IsNullOrWhiteSpace(ImageBox.Text);
    }

    // MARK: - Ports

    private sealed class PortRow
    {
        public required Grid Root;
        public required TextBox HostPort;
        public required TextBox ContainerPort;
        public required ComboBox Protocol;
    }

    private void OnAddPortClick(object sender, RoutedEventArgs e)
    {
        var hostPort = new TextBox { PlaceholderText = "Host Port", Width = 100 };
        var containerPort = new TextBox { PlaceholderText = "Container Port", Width = 120 };
        var protocol = new ComboBox { Width = 80 };
        protocol.Items.Add("tcp");
        protocol.Items.Add("udp");
        protocol.SelectedIndex = 0;

        var row = new PortRow { Root = null!, HostPort = hostPort, ContainerPort = containerPort, Protocol = protocol };
        var grid = BuildRemovableRow([hostPort, Arrow(), containerPort, protocol], () =>
        {
            _portRows.Remove(row);
        });
        row.Root = grid;
        _portRows.Add(row);
        PortsPanel.Children.Add(grid);
    }

    // MARK: - Volumes

    private sealed class VolumeRow
    {
        public required Grid Root;
        public required TextBox HostPath;
        public required TextBox ContainerPath;
        public required CheckBox ReadOnly;
    }

    private void OnAddVolumeClick(object sender, RoutedEventArgs e)
    {
        var hostPath = new TextBox { PlaceholderText = "Host Path", MinWidth = 180 };
        var containerPath = new TextBox { PlaceholderText = "Container Path", MinWidth = 180 };
        var readOnly = new CheckBox { Content = "Read-only" };

        var row = new VolumeRow { Root = null!, HostPath = hostPath, ContainerPath = containerPath, ReadOnly = readOnly };

        // The remove callback must take out the whole wrapper (grid + read-only checkbox),
        // not just the inner grid BuildRemovableRow detaches - otherwise deleting a volume
        // row leaves an orphaned checkbox behind in the panel. `wrapper` is declared before
        // the callback so the closure can capture it.
        Border wrapper = null!;
        var inner = BuildRemovableRow([hostPath, Arrow(), containerPath], () =>
        {
            _volumeRows.Remove(row);
            VolumesPanel.Children.Remove(wrapper);
        });

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(inner);
        stack.Children.Add(readOnly);
        wrapper = new Border { Child = stack, Padding = new Thickness(4) };

        row.Root = inner;
        _volumeRows.Add(row);
        VolumesPanel.Children.Add(wrapper);
    }

    // MARK: - Environment

    private sealed class EnvRow
    {
        public required TextBox Key;
        public required TextBox Value;
    }

    private void OnAddEnvClick(object sender, RoutedEventArgs e) => AddEnvRow("", "");

    private void AddEnvRow(string key, string value)
    {
        var keyBox = new TextBox { PlaceholderText = "KEY", Text = key, MinWidth = 160 };
        var valueBox = new TextBox { PlaceholderText = "value", Text = value, MinWidth = 220 };
        var row = new EnvRow { Key = keyBox, Value = valueBox };

        var grid = BuildRemovableRow([keyBox, new TextBlock { Text = "=", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 }, valueBox],
            () => _envRows.Remove(row));
        _envRows.Add(row);
        EnvPanel.Children.Add(grid);
    }

    // MARK: - Model bridge

    private void OnBridgeProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        BridgePreviewText.Visibility = Visibility.Collapsed;
        InjectBridgeButton.Visibility = Visibility.Collapsed;

        if (BridgeProviderCombo.SelectedItem is not ModelProvider provider) return;

        var network = TargetNetwork();
        if (network is null || string.IsNullOrEmpty(network.Status.Gateway))
        {
            BridgePreviewText.Text = "The selected network has no gateway, so a container can't reach the host. Pick a different network on the Basic tab.";
            BridgePreviewText.Visibility = Visibility.Visible;
            return;
        }

        var baseUrl = ModelBridge.ContainerBaseUrl(network.Status.Gateway!, provider.Port, provider.Api);
        BridgePreviewText.Text = $"Container reaches it at {baseUrl}";
        BridgePreviewText.Visibility = Visibility.Visible;
        InjectBridgeButton.Visibility = Visibility.Visible;
    }

    private void OnInjectBridgeClick(object sender, RoutedEventArgs e)
    {
        if (BridgeProviderCombo.SelectedItem is not ModelProvider provider) return;
        var network = TargetNetwork();
        if (network is null) return;

        var env = _services.ModelService.BridgeEnvironment(provider, network);
        if (env is null) return;

        foreach (var (key, value) in env)
        {
            var existing = _envRows.FirstOrDefault(r => r.Key.Text == key);
            if (existing is not null)
            {
                existing.Value.Text = value;
            }
            else
            {
                AddEnvRow(key, value);
            }
        }
    }

    private ContainerNetwork? TargetNetwork()
    {
        var wanted = NetworkCombo.SelectedItem as string;
        wanted = string.IsNullOrEmpty(wanted) || wanted == "Default" ? "default" : wanted;
        return _services.NetworkService.Networks.FirstOrDefault(n => n.Id == wanted);
    }

    // MARK: - Row-building helper

    private static FontIcon Arrow() => new() { Glyph = "", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };

    private static Grid BuildRemovableRow(IReadOnlyList<FrameworkElement> content, Action onRemove)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < content.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < content.Count; i++)
        {
            Grid.SetColumn(content[i], i);
            grid.Children.Add(content[i]);
        }

        var deleteButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        Grid.SetColumn(deleteButton, content.Count + 1);
        deleteButton.Click += (_, _) =>
        {
            onRemove();
            (grid.Parent as Panel)?.Children.Remove(grid);
        };
        grid.Children.Add(deleteButton);

        return grid;
    }

    // MARK: - Lifetime hints

    private void OnKeepRunningChanged(object sender, RoutedEventArgs e) => UpdateCommandHint();

    private void OnCommandOverrideChanged(object sender, TextChangedEventArgs e) => UpdateCommandHint();

    private void UpdateCommandHint()
    {
        var keep = KeepRunningCheck.IsChecked == true;
        var hasCmd = !string.IsNullOrWhiteSpace(CommandOverrideBox.Text);
        if (hasCmd)
        {
            CommandHint.Text = "Using your command. The container stays up only while that process runs.";
        }
        else if (keep)
        {
            CommandHint.Text =
                "Keep-running is on: shell-only images (alpine, etc.) will use sleep infinity so " +
                "the container stays up until you press Stop. Server images keep their normal entrypoint.";
        }
        else
        {
            CommandHint.Text =
                "Keep-running is off: the image's default process is used. If it exits, the container stops.";
        }
    }

    // MARK: - Submit

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var config = new ContainerRunConfig
            {
                Name = NameBox.Text.Trim(),
                Image = ImageBox.Text.Trim(),
                Detached = DetachedCheck.IsChecked == true,
                RemoveAfterStop = RemoveAfterStopCheck.IsChecked == true,
                KeepRunningUntilStopped = KeepRunningCheck.IsChecked == true,
                WorkingDirectory = WorkingDirBox.Text.Trim(),
                CommandOverride = CommandOverrideBox.Text.Trim(),
                DnsDomain = DnsDomainCombo.SelectedItem is DnsDomain { Domain: "None" } or null
                    ? ""
                    : ((DnsDomain)DnsDomainCombo.SelectedItem).Domain,
                Network = NetworkCombo.SelectedItem as string == "Default" ? "" : (NetworkCombo.SelectedItem as string ?? ""),
            };

            foreach (var row in _portRows)
            {
                if (string.IsNullOrWhiteSpace(row.HostPort.Text) || string.IsNullOrWhiteSpace(row.ContainerPort.Text)) continue;
                config.PortMappings.Add(new ContainerRunConfig.PortMapping
                {
                    HostPort = row.HostPort.Text.Trim(),
                    ContainerPort = row.ContainerPort.Text.Trim(),
                    TransportProtocol = row.Protocol.SelectedItem as string ?? "tcp",
                });
            }

            foreach (var row in _volumeRows)
            {
                if (string.IsNullOrWhiteSpace(row.HostPath.Text) || string.IsNullOrWhiteSpace(row.ContainerPath.Text)) continue;
                config.VolumeMappings.Add(new ContainerRunConfig.VolumeMapping
                {
                    HostPath = row.HostPath.Text.Trim(),
                    ContainerPath = row.ContainerPath.Text.Trim(),
                    ReadOnly = row.ReadOnly.IsChecked == true,
                });
            }

            foreach (var row in _envRows)
            {
                if (string.IsNullOrEmpty(row.Key.Text)) continue;
                config.EnvironmentVariables.Add(new ContainerRunConfig.EnvironmentVariable
                {
                    Key = row.Key.Text,
                    Value = row.Value.Text,
                });
            }

            var started = await _services.ContainerListService.RunContainerAsync(config);
            if (!started)
            {
                args.Cancel = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
