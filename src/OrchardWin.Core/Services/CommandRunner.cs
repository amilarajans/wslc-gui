using System.Diagnostics;
using System.Text;

namespace OrchardWin.Core.Services;

/// Result of running an external process. Ported from Orchard's `ProcessResult`.
public sealed record ProcessResult(int ExitCode, string? Stdout, string? Stderr)
{
    public bool Failed => ExitCode != 0;
}

/// Abstraction over running external CLI commands (wslc.exe, wsl.exe), so callers can be
/// driven with a mock in tests. Mirrors Orchard's `CommandRunner` protocol exactly - same
/// two-method shape (plain run vs. elevated run), same reason for existing: every CLI-backed
/// service in this app goes through here instead of hand-rolling `Process` calls.
public interface ICommandRunner
{
    Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct = default);

    /// Run elevated (Windows UAC prompt), for operations that need admin rights - e.g.
    /// editing the hosts file for DNS domains. Mirrors Orchard's `runWithSudo`, which shows
    /// a macOS admin-privileges prompt via AppleScript; this shows the Windows UAC consent
    /// dialog instead. Both interrupt the user exactly once per call, synchronously.
    Task<ProcessResult> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// The production `ICommandRunner`: spawns real processes.
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both streams concurrently *before* awaiting exit: a child that writes more
        // than the pipe buffer would otherwise block forever waiting for us to read, and
        // we'd block forever awaiting exit - the same deadlock Orchard's Process wrapper
        // guards against on macOS.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = (await stdoutTask).TrimEnd('\n', '\r');
        var stderr = (await stderrTask).TrimEnd('\n', '\r');

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    /// Elevated processes launched via the shell (`UseShellExecute = true`, `Verb = "runas"`)
    /// cannot have their stdout/stderr redirected directly - Windows has no equivalent of
    /// AppleScript's `do shell script … with administrator privileges`, which returns
    /// captured output inline. Instead: run an elevated `cmd.exe /c` that redirects its own
    /// output to temp files, wait for it to exit, then read them back.
    public async Task<ProcessResult> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var stdoutFile = Path.GetTempFileName();
        var stderrFile = Path.GetTempFileName();
        try
        {
            var command = BuildElevatedCommand(program, arguments, stdoutFile, stderrFile);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: user declined the UAC prompt.
                return new ProcessResult(1223, null, "Elevation was cancelled by the user.");
            }
            await process.WaitForExitAsync(ct);

            var stdout = File.Exists(stdoutFile) ? await File.ReadAllTextAsync(stdoutFile, ct) : null;
            var stderr = File.Exists(stderrFile) ? await File.ReadAllTextAsync(stderrFile, ct) : null;
            return new ProcessResult(process.ExitCode, stdout?.TrimEnd('\n', '\r'), stderr?.TrimEnd('\n', '\r'));
        }
        finally
        {
            File.Delete(stdoutFile);
            File.Delete(stderrFile);
        }
    }

    /// Build the `cmd.exe /c` command line that runs `program` + `arguments`, redirecting
    /// output to the given temp files. Every token is quoted for cmd.exe's parsing rules -
    /// wrap in double quotes, double any embedded double quote - so a space or quote in a
    /// user-supplied argument (e.g. a DNS domain) is treated as literal text, never as a
    /// shell metacharacter. Mirrors Orchard's `shellQuote`/`adminScript` pair.
    internal static string BuildElevatedCommand(string program, IReadOnlyList<string> arguments, string stdoutFile, string stderrFile)
    {
        var parts = new List<string> { program };
        parts.AddRange(arguments);
        var quoted = string.Join(' ', parts.Select(CmdQuote));
        return $"{quoted} > {CmdQuote(stdoutFile)} 2> {CmdQuote(stderrFile)}";
    }

    internal static string CmdQuote(string token)
    {
        if (token.Length > 0 && token.IndexOfAny([' ', '"', '\t']) < 0) return token;
        var sb = new StringBuilder("\"");
        foreach (var c in token)
        {
            if (c == '"') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
