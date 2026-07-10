namespace OrchardWin.Core.Models;

public sealed class ContainerRunConfig
{
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public bool Detached { get; set; } = true;
    public bool RemoveAfterStop { get; set; }
    public List<EnvironmentVariable> EnvironmentVariables { get; set; } = [];
    public List<PortMapping> PortMappings { get; set; } = [];
    public List<VolumeMapping> VolumeMappings { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public string CommandOverride { get; set; } = "";
    public string DnsDomain { get; set; } = "";
    public string Network { get; set; } = "";
    /// Labels to stamp on the container at creation (e.g. the sandbox marker).
    public Dictionary<string, string> Labels { get; set; } = [];

    public sealed class EnvironmentVariable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public sealed class PortMapping
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string HostPort { get; set; } = "";
        public string ContainerPort { get; set; } = "";
        public string TransportProtocol { get; set; } = "tcp";
    }

    public sealed class VolumeMapping
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string HostPath { get; set; } = "";
        public string ContainerPath { get; set; } = "";
        public bool ReadOnly { get; set; }
    }
}
