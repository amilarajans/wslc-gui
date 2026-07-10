using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns container network state and lifecycle, backed by `IContainerBackend`. Ported 1:1
/// from Orchard's `NetworkService.swift`.
public sealed partial class NetworkService : ObservableObject
{
    private readonly IContainerBackend _backend;
    private readonly AlertCenter _alertCenter;

    [ObservableProperty]
    private ObservableCollection<ContainerNetwork> _networks = [];

    [ObservableProperty]
    private bool _isNetworksLoading;

    public NetworkService(IContainerBackend backend, AlertCenter alertCenter)
    {
        _backend = backend;
        _alertCenter = alertCenter;
    }

    /// ContainerNetwork/NetworkConfig carry a Dictionary<string,string> (Labels);
    /// record-synthesized equality compares that by reference, not value, so a JSON
    /// round-trip is the simplest reliable structural comparison.
    private static bool NetworksEqual(IReadOnlyList<ContainerNetwork> old, IReadOnlyList<ContainerNetwork> updated)
    {
        if (old.Count != updated.Count) return false;
        return JsonSerializer.Serialize(old) == JsonSerializer.Serialize(updated);
    }

    public async Task LoadAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsNetworksLoading = true;
            _alertCenter.Dismiss();
        }

        try
        {
            var networks = await _backend.ListNetworksAsync(ct);
            if (!NetworksEqual(Networks, networks))
            {
                Networks = new ObservableCollection<ContainerNetwork>(networks);
            }
            IsNetworksLoading = false;
        }
        catch (Exception error)
        {
            if (showLoading)
            {
                _alertCenter.Error($"Failed to load networks: {error.Message}");
            }
            IsNetworksLoading = false;
        }
    }

    public async Task<bool> CreateAsync(string name, string? subnet = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        try
        {
            var labelDict = new Dictionary<string, string>();
            foreach (var label in labels ?? [])
            {
                var parts = label.Split('=', 2);
                if (parts.Length == 2)
                {
                    labelDict[parts[0]] = parts[1];
                }
                else
                {
                    labelDict[label] = "";
                }
            }

            await _backend.CreateNetworkAsync(name, subnet, labelDict, ct);
            await LoadAsync(ct: ct);
            return true;
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to create network: {error.Message}");
            return false;
        }
    }

    public async Task DeleteAsync(string networkId, CancellationToken ct = default)
    {
        try
        {
            await _backend.DeleteNetworkAsync(networkId, ct);
            await LoadAsync(ct: ct);
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to delete network: {error.Message}");
        }
    }
}
