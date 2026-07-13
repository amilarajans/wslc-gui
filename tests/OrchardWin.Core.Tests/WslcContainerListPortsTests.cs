using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Tests;

/// Regression: wslc 2.9.x emits Ports as objects; List&lt;string&gt; deserializers used to
/// fail the entire container list → empty GUI even with running containers.
public sealed class WslcContainerListPortsTests
{
    [Fact]
    public void ParseJsonArray_ObjectPorts_DoesNotEmptyTheList()
    {
        // Shape verified against live `wslc container ps --all --format json` (2.9.3).
        const string json = """
            [
              {
                "CreatedAt": 1783947083,
                "Id": "611a6efbb8b4652133ae6176edfe03b007db853ed0fdca53f587d9e8f9da80c2",
                "Image": "milemate-app:latest",
                "Name": "milemate-app",
                "Ports": [
                  {
                    "BindingAddress": "127.0.0.1",
                    "ContainerPort": 8080,
                    "HostPort": 8080,
                    "Protocol": 6
                  }
                ],
                "State": 2,
                "StateChangedAt": 1783947084
              },
              {
                "CreatedAt": 1783947082,
                "Id": "2090e86cafe67913f7a65c8db7d746fb8be3c970a1271326def10c7933e048d0",
                "Image": "postgres:16-alpine",
                "Name": "milemate-db",
                "Ports": [],
                "State": 2,
                "StateChangedAt": 1783947083
              }
            ]
            """;

        // Exercise the same path ListContainers uses via a public thin helper on the backend.
        var containers = WslcCliContainerBackend.ParseContainerListJsonForTests(json);
        Assert.Equal(2, containers.Count);
        Assert.Contains(containers, c => c.Configuration.Hostname == "milemate-app");
        Assert.Contains(containers, c => c.Configuration.Hostname == "milemate-db");
        Assert.Equal("running", containers.First(c => c.Configuration.Hostname == "milemate-app").Status);

        var app = containers.First(c => c.Configuration.Hostname == "milemate-app");
        Assert.Single(app.Configuration.PublishedPorts);
        Assert.Equal(8080, app.Configuration.PublishedPorts[0].HostPort);
        Assert.Equal(8080, app.Configuration.PublishedPorts[0].ContainerPort);
        Assert.Equal("127.0.0.1", app.Configuration.PublishedPorts[0].HostAddress);
        Assert.Equal("tcp", app.Configuration.PublishedPorts[0].TransportProtocol);
    }

    [Fact]
    public void ParseJsonArray_LegacyStringPorts_StillWorks()
    {
        const string json = """
            [
              {
                "Id": "abc",
                "Image": "nginx:latest",
                "Name": "web",
                "Ports": [ "8080:80/tcp" ],
                "State": 2
              }
            ]
            """;

        var containers = WslcCliContainerBackend.ParseContainerListJsonForTests(json);
        Assert.Single(containers);
        Assert.Equal("web", containers[0].Configuration.Hostname);
        Assert.Single(containers[0].Configuration.PublishedPorts);
        Assert.Equal(8080, containers[0].Configuration.PublishedPorts[0].HostPort);
        Assert.Equal(80, containers[0].Configuration.PublishedPorts[0].ContainerPort);
    }
}
