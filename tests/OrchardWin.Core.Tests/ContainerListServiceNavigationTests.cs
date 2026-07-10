using OrchardWin.Core.Services;
using OrchardWin.Core.Tests.Fakes;

namespace OrchardWin.Core.Tests;

public sealed class ContainerListServiceNavigationTests
{
    [Fact]
    public async Task LoadAsync_PopulatesContainers_OnFirstCall()
    {
        var backend = new FakeContainerBackend
        {
            Containers =
            [
                FakeContainerBackend.MakeContainer("id1", "alpha", "running"),
                FakeContainerBackend.MakeContainer("id2", "beta", "exited"),
            ],
        };
        var service = new ContainerListService(backend, new AlertCenter());

        await service.LoadAsync(showLoading: true);

        Assert.Equal(2, service.Containers.Count);
        Assert.False(service.IsContainersLoading);
    }

    [Fact]
    public async Task LoadAsync_KeepsContainers_WhenListUnchanged_OnSecondLoad()
    {
        var backend = new FakeContainerBackend
        {
            Containers = [FakeContainerBackend.MakeContainer("id1", "alpha")],
        };
        var service = new ContainerListService(backend, new AlertCenter());

        await service.LoadAsync(showLoading: true);
        var first = service.Containers;

        await service.LoadAsync(showLoading: true);

        Assert.Single(service.Containers);
        Assert.Same(first, service.Containers);
        Assert.Equal(2, backend.ListContainersCallCount);
    }
}
