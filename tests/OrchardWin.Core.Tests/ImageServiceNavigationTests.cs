using OrchardWin.Core.Services;
using OrchardWin.Core.Tests.Fakes;

namespace OrchardWin.Core.Tests;

/// Regression: tab re-entry must not "lose" images when the backend returns the same list.
/// ImageService short-circuits PropertyChanged when lists are equal — ViewModels must still
/// re-project from the existing collection (covered here at the service layer + call counts).
public sealed class ImageServiceNavigationTests
{
    [Fact]
    public async Task LoadAsync_PopulatesImages_OnFirstCall()
    {
        var backend = new FakeContainerBackend
        {
            Images =
            [
                FakeContainerBackend.MakeImage("alpine:latest"),
                FakeContainerBackend.MakeImage("postgres:16"),
            ],
        };
        var service = new ImageService(backend, new AlertCenter());

        await service.LoadAsync(showLoading: true);

        Assert.Equal(2, service.Images.Count);
        Assert.Contains(service.Images, i => i.Reference == "alpine:latest");
        Assert.Equal(1, backend.ListImagesCallCount);
        Assert.False(service.IsImagesLoading);
    }

    [Fact]
    public async Task LoadAsync_KeepsImages_WhenListUnchanged_OnSecondLoad()
    {
        var backend = new FakeContainerBackend
        {
            Images =
            [
                FakeContainerBackend.MakeImage("alpine:latest"),
                FakeContainerBackend.MakeImage("postgres:16"),
            ],
        };
        var service = new ImageService(backend, new AlertCenter());

        await service.LoadAsync(showLoading: true);
        var firstInstance = service.Images;

        // Simulate tab leave/return: Load again with identical backend data.
        await service.LoadAsync(showLoading: true);

        Assert.Equal(2, service.Images.Count);
        Assert.Equal(2, backend.ListImagesCallCount);
        // Equality short-circuit keeps the same collection instance (no flicker / no clear).
        Assert.Same(firstInstance, service.Images);
        Assert.False(service.IsImagesLoading);
    }

    [Fact]
    public async Task LoadAsync_DoesNotClearImages_WhenBackendReturnsSameDataAfterUserRefresh()
    {
        var backend = new FakeContainerBackend
        {
            Images = [FakeContainerBackend.MakeImage("milemate-app:latest", 40_000_000)],
        };
        var service = new ImageService(backend, new AlertCenter());

        await service.LoadAsync(showLoading: true);
        Assert.Single(service.Images);

        // User hits Refresh while still on the page (or after navigation).
        await service.LoadAsync(showLoading: true);

        Assert.Single(service.Images);
        Assert.Equal("milemate-app:latest", service.Images[0].Reference);
    }

    [Fact]
    public async Task LoadAsync_ReplacesImages_WhenBackendListChanges()
    {
        var backend = new FakeContainerBackend
        {
            Images = [FakeContainerBackend.MakeImage("alpine:latest")],
        };
        var service = new ImageService(backend, new AlertCenter());

        await service.LoadAsync();
        Assert.Single(service.Images);

        backend.Images.Add(FakeContainerBackend.MakeImage("redis:7"));
        await service.LoadAsync();

        Assert.Equal(2, service.Images.Count);
    }
}
