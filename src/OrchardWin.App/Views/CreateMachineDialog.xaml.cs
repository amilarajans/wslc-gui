using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// Create-machine dialog, porting CreateMachine.swift. Not a top-level Page - constructed
/// directly by MachinesPage (which owns an AppServices reference from its own OnNavigatedTo),
/// so it takes AppServices as a constructor parameter rather than via Frame navigation.
public sealed partial class CreateMachineDialog : ContentDialog
{
    /// Same rule as the runtime and the Swift original: start/end alphanumeric, lowercase
    /// letters/digits/hyphens only in between.
    private static readonly Regex NamePattern = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled);

    private readonly AppServices _services;

    public CreateMachineDialog(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var hostCores = Math.Max(Environment.ProcessorCount, 1);
        var hostMemoryGiB = EstimateHostMemoryGiB();

        CpusBox.Maximum = hostCores;
        CpusBox.Value = Math.Max(hostCores / 2, 1);
        CpusCaption.Text = $"Cores allocated (host has {hostCores}).";

        MemoryBox.Maximum = hostMemoryGiB;
        MemoryBox.Value = Math.Max(hostMemoryGiB / 2, 1);
        MemoryCaption.Text = $"RAM allocated (host has ~{hostMemoryGiB} GB).";
    }

    /// Validates then creates on the dialog's Primary button, using a deferral so the dialog
    /// stays open (showing a validation message, or - if MachineService.CreateAsync fails and
    /// has already alerted via AlertCenter - lets the user adjust and retry) instead of closing
    /// immediately the way ContentDialog does by default.
    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!TryBuildSpec(out var spec, out var error))
            {
                args.Cancel = true;
                ShowValidationError(error!);
                return;
            }

            ValidationText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            var created = await _services.MachineService.CreateAsync(spec);
            if (!created)
            {
                args.Cancel = true;
            }
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private bool TryBuildSpec(out MachineCreateSpec spec, out string? error)
    {
        var image = ImageTextBox.Text.Trim();
        var name = NameTextBox.Text.Trim();

        if (image.Length == 0)
        {
            spec = null!;
            error = "Image is required.";
            return false;
        }

        if (!NamePattern.IsMatch(name))
        {
            spec = null!;
            error = "Invalid name. Use lowercase letters, digits, and hyphens (must start and end alphanumeric).";
            return false;
        }

        var homeMount = HomeMountRoRadio.IsChecked == true ? "ro"
            : HomeMountNoneRadio.IsChecked == true ? "none"
            : "rw";

        spec = new MachineCreateSpec
        {
            Name = name,
            ImageRef = image,
            Cpus = (int)CpusBox.Value,
            MemoryGiB = (int)MemoryBox.Value,
            HomeMount = homeMount,
            Virtualization = VirtualizationCheck.IsChecked == true,
            SetDefault = SetDefaultCheck.IsChecked == true,
            NoBoot = BootAfterCreateCheck.IsChecked != true,
        };
        error = null;
        return true;
    }

    private void ShowValidationError(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private static int EstimateHostMemoryGiB()
    {
        // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is a rough proxy for total host RAM -
        // portable .NET has no direct equivalent to Swift's ProcessInfo.physicalMemory. Good
        // enough as an upper bound for the memory stepper, not an exact reading.
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Max(1, (int)(bytes / 1_073_741_824));
    }
}
