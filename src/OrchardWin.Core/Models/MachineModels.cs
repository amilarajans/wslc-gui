namespace OrchardWin.Core.Models;

/// <summary>
/// A container "Machine" - a persistent, stateful lightweight Linux environment, expressed
/// entirely in app-owned types so the WSL backend's raw shapes never leak past the backend
/// boundary. See ARCHITECTURE.md "Machines" for the platform mapping: Apple's container
/// machine (an init-capable container that behaves like a full VM: boots, gets an IP, has a
/// home-directory mount) has no 1:1 Windows primitive. It is reimplemented here as a WSL
/// distro registered from an OCI/container rootfs, combining <c>wsl.exe</c> distro lifecycle
/// (start/stop/set-default/unregister) with <c>wslc</c> for the backing container's image
/// pull and resource stats - the same "container that acts like a machine" shape Orchard's
/// own model already assumes (see <see cref="ContainerId"/>).
/// </summary>
public sealed record Machine
{
    public required string Id { get; init; } // WSL distro registration name
    public required string Status { get; init; } // "running" | "stopped"
    public bool IsDefault { get; init; }
    public int Cpus { get; init; }
    public long MemoryBytes { get; init; }
    public long? DiskSizeBytes { get; init; }

    /// Home directory mount mode: `rw`, `ro`, or `none`.
    public string HomeMount { get; init; } = "rw";
    public bool Virtualization { get; init; }

    /// Always null on Windows - WSL ships one Microsoft-maintained kernel per WSL version,
    /// not a per-machine swappable one. Kept for shape parity with Orchard's model; the
    /// Settings > Kernel page shows the shared WSL kernel version instead of a picker.
    public string? KernelPath { get; init; }

    public required string ImageReference { get; init; }
    public required Platform Platform { get; init; }
    public string? IpAddress { get; init; }

    /// The backing wslc container id this machine's distro proxies stats/logs through.
    public string? ContainerId { get; init; }
    public DateTimeOffset? CreatedDate { get; init; }
    public DateTimeOffset? StartedDate { get; init; }
    public bool Initialized { get; init; }
    public MachineUserSetup? UserSetup { get; init; }

    public bool IsRunning => Status == "running";
    public bool IsStopped => Status == "stopped";
}

/// The host user mapped into a machine at first-boot provisioning.
public sealed record MachineUserSetup
{
    public required string Username { get; init; }
    public int Uid { get; init; }
    public int Gid { get; init; }
}

/// Everything needed to create a container machine, in app-owned types.
/// Nullable numeric fields mean "use the runtime default".
public sealed record MachineCreateSpec
{
    public required string Name { get; init; }
    public required string ImageRef { get; init; }
    public int? Cpus { get; init; }
    public int? MemoryGiB { get; init; }
    public string? HomeMount { get; init; } // "ro" / "rw" / "none", null = default (rw)
    public bool Virtualization { get; init; }
    public bool SetDefault { get; init; }
    public bool NoBoot { get; init; }
}

/// Editable boot-time configuration for an existing machine. Applied via SetConfig, which
/// only takes effect on next boot.
public sealed record MachineConfigSpec
{
    public required int Cpus { get; init; }
    public required int MemoryGiB { get; init; }
    public required string HomeMount { get; init; }
    public required bool Virtualization { get; init; }
}
