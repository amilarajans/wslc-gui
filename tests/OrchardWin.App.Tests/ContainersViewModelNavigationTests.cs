using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;
using OrchardWin.Core.Tests.Fakes;

namespace OrchardWin.App.Tests;

public sealed class ContainersViewModelNavigationTests
{
    [Fact]
    public async Task NewViewModel_HydratesRows_FromAlreadyLoadedService()
    {
        var backend = new FakeContainerBackend
        {
            Containers =
            [
                FakeContainerBackend.MakeContainer("abc", "web", "running", "nginx:latest"),
                FakeContainerBackend.MakeContainer("def", "db", "exited", "postgres:16"),
            ],
        };
        var services = new AppServices(backend: backend);

        var first = new ContainersViewModel(services);
        await first.LoadAsync();
        Assert.Equal(2, first.ContainerRows.Count);

        var second = new ContainersViewModel(services);
        Assert.Equal(2, second.ContainerRows.Count);

        await second.LoadAsync();
        Assert.Equal(2, second.ContainerRows.Count);
        Assert.Contains(second.ContainerRows, r => r.PrimaryText == "web");
        Assert.Contains(second.ContainerRows, r => r.PrimaryText == "db");
    }
}
