using System.Text;
using OrchardWin.Core.Services;

namespace OrchardWin.Core.Tests;

public sealed class WslVersionParsingTests
{
    private const string SampleEnglish = """
        WSL version: 2.9.3.0
        Kernel version: 6.18.35.2-1
        WSLg version: 1.0.79
        MSRDC version: 1.2.7214
        Direct3D version: 1.611.1-81528511
        DXCore version: 10.0.26100.1-240331-1435.ge-release
        Windows version: 10.0.26200.8655
        """;

    [Fact]
    public void ParseKernelInfo_ReadsEnglishWslVersionOutput()
    {
        var info = SystemService.ParseKernelInfo(SampleEnglish);

        Assert.Equal("2.9.3.0", info.WslVersion);
        Assert.Equal("6.18.35.2-1", info.KernelVersion);
        Assert.Equal("1.0.79", info.WslgVersion);
        Assert.Equal("10.0.26200.8655", info.WindowsVersion);
    }

    [Fact]
    public void ParseKernelInfo_StripsEmbeddedNulls_FromUtf16Misdecode()
    {
        // What ProcessCommandRunner used to produce when reading UTF-16 as UTF-8/default.
        var corrupted = string.Join("", SampleEnglish.Select(c => c + "\0"));
        var info = SystemService.ParseKernelInfo(corrupted);

        Assert.Equal("2.9.3.0", info.WslVersion);
        Assert.Equal("6.18.35.2-1", info.KernelVersion);
    }

    [Fact]
    public void DecodeConsoleOutput_HandlesUtf16LeWithoutBom()
    {
        var utf16 = Encoding.Unicode.GetBytes(SampleEnglish);
        var decoded = ProcessCommandRunner.DecodeConsoleOutput(utf16);

        Assert.DoesNotContain('\0', decoded);
        Assert.Contains("WSL version: 2.9.3.0", decoded);
        Assert.Contains("Kernel version:", decoded);
    }

    [Fact]
    public void DecodeConsoleOutput_HandlesUtf8()
    {
        var utf8 = Encoding.UTF8.GetBytes("wslc 2.9.3.0\n");
        var decoded = ProcessCommandRunner.DecodeConsoleOutput(utf8);
        Assert.Equal("wslc 2.9.3.0\n", decoded);
    }

    [Fact]
    public void LooksLikeUtf16Le_DetectsWslVersionBytes()
    {
        var utf16 = Encoding.Unicode.GetBytes("WSL version: 2.9.3.0\n");
        Assert.True(ProcessCommandRunner.LooksLikeUtf16Le(utf16));

        var utf8 = Encoding.UTF8.GetBytes("WSL version: 2.9.3.0\n");
        Assert.False(ProcessCommandRunner.LooksLikeUtf16Le(utf8));
    }
}
