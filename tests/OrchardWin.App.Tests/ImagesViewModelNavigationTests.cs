using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;
using OrchardWin.Core.Tests.Fakes;

namespace OrchardWin.App.Tests;

/// Regression for blank Images tab after switching away and back.
/// Frame navigation builds a new ViewModel; LoadAsync must re-project rows even when
/// ImageService does not re-raise Images PropertyChanged (unchanged list).
public sealed class ImagesViewModelNavigationTests
{
    [Fact]
    public async Task NewViewModel_HydratesRows_FromAlreadyLoadedService()
    {
        var backend = new FakeContainerBackend
        {
            Images =
            [
                FakeContainerBackend.MakeImage("alpine:latest"),
                FakeContainerBackend.MakeImage("postgres:16-alpine"),
            ],
        };
        var services = new AppServices(backend: backend);

        // First visit: load images into the shared service (composition root survives navigation).
        var firstVisit = new ImagesViewModel(services);
        await firstVisit.LoadAsync();
        Assert.Equal(2, firstVisit.Rows.Count);

        // Leave tab: firstVisit discarded. Service still holds Images.
        // Return: brand-new ViewModel must show existing data immediately (constructor Refresh)
        // and after LoadAsync even when the backend returns the same list.
        var secondVisit = new ImagesViewModel(services);
        Assert.Equal(2, secondVisit.Rows.Count); // ctor hydration

        await secondVisit.LoadAsync(); // user refresh / page OnNavigatedTo
        Assert.Equal(2, secondVisit.Rows.Count);
        Assert.Contains(secondVisit.Rows, r => r.Name == "alpine");
        Assert.Contains(secondVisit.Rows, r => r.Name == "postgres");
    }

    [Fact]
    public async Task RefreshCommand_DoesNotEmptyRows_WhenListUnchanged()
    {
        var backend = new FakeContainerBackend
        {
            Images = [FakeContainerBackend.MakeImage("redis:7")],
        };
        var services = new AppServices(backend: backend);
        var vm = new ImagesViewModel(services);
        await vm.LoadAsync();
        Assert.Single(vm.Rows);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Rows);
        Assert.Equal("redis", vm.Rows[0].Name);
    }

    [Fact]
    public async Task LoadAsync_AfterEmptyThenPopulate_ShowsRows()
    {
        var backend = new FakeContainerBackend { Images = [] };
        var services = new AppServices(backend: backend);
        var vm = new ImagesViewModel(services);
        await vm.LoadAsync();
        Assert.Empty(vm.Rows);

        backend.Images.Add(FakeContainerBackend.MakeImage("nginx:latest"));
        await vm.LoadAsync();

        Assert.Single(vm.Rows);
        Assert.Equal("nginx", vm.Rows[0].Name);
    }
}
