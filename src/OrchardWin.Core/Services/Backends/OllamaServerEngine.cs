using System.Diagnostics;

namespace OrchardWin.Core.Services.Backends;

/// <see cref="IServerProcess"/> backed by a real <see cref="Process"/>, streaming
/// stdout+stderr line-by-line into a log file. Ported from Orchard's `LiveServerProcess`,
/// adapted to .NET's redirection model: `Process` has no direct equivalent of NSTask's
/// "hand it a FileHandle" trick, so output is captured via the standard
/// OutputDataReceived/ErrorDataReceived async-read pattern and written through a shared,
/// lock-guarded `StreamWriter` instead.
internal sealed class OllamaServerProcess : IServerProcess
{
    private readonly Process _process;
    private readonly StreamWriter _logWriter;
    private readonly Lock _logLock = new();

    public Action<int>? TerminationHandler { get; set; }

    public OllamaServerProcess(Process process, StreamWriter logWriter)
    {
        _process = process;
        _logWriter = logWriter;
        _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        // `Process.Exited` (like NSTask's terminationHandler) fires on an arbitrary
        // ThreadPool thread, not necessarily whatever thread started the process. Unlike
        // Orchard's `LiveServerProcess`, which explicitly hops to `DispatchQueue.main` so
        // `ModelServerService`'s handler runs main-actor-isolated, this app has no actor
        // isolation to preserve at this layer - the delegate is invoked synchronously on
        // whatever thread .NET gives us. A CommunityToolkit/WinUI App layer that needs the
        // callback on the UI thread is responsible for marshalling there itself.
        _process.Exited += OnExited;
        _process.EnableRaisingEvents = true;
    }

    private void WriteLog(string? line)
    {
        if (line is null) return;
        lock (_logLock)
        {
            try { _logWriter.WriteLine(line); }
            catch (ObjectDisposedException) { /* process outlived the writer's close - drop */ }
            catch (IOException) { /* best-effort logging only */ }
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var code = _process.ExitCode;
        lock (_logLock)
        {
            try { _logWriter.Flush(); _logWriter.Dispose(); }
            catch (IOException) { /* best-effort */ }
        }
        TerminationHandler?.Invoke(code);
    }

    public void Terminate()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill - Exited still fires normally.
        }
    }
}

/// <see cref="IModelServerEngine"/> wrapping `ollama.exe`. Orchard's `MLXServerEngine` wraps
/// Apple's `mlx_lm.server`, an OpenAI-compatible one-process-per-model-per-port server; there
/// is no Windows/MLX equivalent (MLX is Apple Silicon-only). Ollama's actual model is
/// different: a single `ollama serve` daemon binds one host:port and serves *any* locally
/// pulled model, chosen per-request by the `model` field in the HTTP body - it does not take
/// a model argument at launch the way `mlx_lm.server --model ... --port ...` does.
///
/// Design choice (documented per the task's "pick the simplest faithful mapping" guidance):
/// treat the launched `ollama serve` process itself as the supervised unit, exactly like
/// `mlx_lm.server` - one `IServerProcess` per `Launch()` call, bound to the requested
/// `host:port` via the `OLLAMA_HOST` environment variable, logs redirected to `logPath`,
/// terminated by killing that process. `model` is not a server-launch argument under this
/// mapping (Ollama has none), so `Launch` best-effort fires `ollama pull &lt;model&gt;`
/// against the daemon it just started, so the model is resident before the first chat
/// request - this mirrors `ModelServerService`'s per-model-server bookkeeping (one
/// `ManagedModelServer` per model+port) even though, under the hood, all managed servers on
/// distinct ports are really independent `ollama serve` daemons that could each in principle
/// serve *any* model.
public sealed class OllamaServerEngine : IModelServerEngine
{
    // VERIFY: default Ollama-for-Windows install location. Confirmed pattern for Windows
    // per-user installers in general (%LOCALAPPDATA%\Programs\<App>); not yet re-verified
    // against Ollama's actual Windows installer output on a real machine.
    private static IEnumerable<string> CandidateBinaryPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (trimmed.Length == 0) continue;
            yield return Path.Combine(trimmed, "ollama.exe");
            // This project builds with plain `dotnet build` on any OS (see csproj remarks),
            // so also check the extension-less name for local dev/test runs off Windows.
            yield return Path.Combine(trimmed, "ollama");
        }
    }

    public string? LocateBinary() => CandidateBinaryPaths().FirstOrDefault(File.Exists);

    public IServerProcess Launch(string model, string host, ushort port, string logPath)
    {
        var binary = LocateBinary();
        if (binary is null)
            throw OrchardWinException.BinaryNotFound(CandidateBinaryPaths().ToList());

        var logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDirectory)) Directory.CreateDirectory(logDirectory);
        var logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        // VERIFY: OLLAMA_HOST=host:port is Ollama's documented way to change the daemon's
        // bind address on macOS/Linux; behaviour of the Windows ollama.exe build has not
        // been re-verified against real `wslc`/Ollama-for-Windows hardware.
        psi.Environment["OLLAMA_HOST"] = $"{host}:{port}";

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw OrchardWinException.Generic($"Failed to start {binary}.");
        }
        catch (Exception ex) when (ex is not OrchardWinException)
        {
            logWriter.Dispose();
            throw OrchardWinException.Generic($"Failed to start {binary}: {ex.Message}");
        }

        var wrapper = new OllamaServerProcess(process, logWriter);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Best-effort: make sure `model` is actually pulled/resident on the daemon we just
        // started, so the first chat request doesn't stall on (or silently fail behind) a
        // multi-GB download. Fire-and-forget by design - a pull failure should surface later
        // as a chat error against this server, not as a `Launch` failure, since the server
        // process itself did start successfully.
        // VERIFY: `ollama pull <model>` argument shape / exit-code semantics on Windows.
        _ = PullModelBestEffortAsync(binary, host, port, model);

        return wrapper;
    }

    private static async Task PullModelBestEffortAsync(string binary, string host, ushort port, string model)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("pull");
            psi.ArgumentList.Add(model);
            psi.Environment["OLLAMA_HOST"] = $"{host}:{port}";

            using var pull = new Process { StartInfo = psi };
            pull.Start();
            var stdoutTask = pull.StandardOutput.ReadToEndAsync();
            var stderrTask = pull.StandardError.ReadToEndAsync();
            await pull.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            if (pull.ExitCode != 0)
                Log.Backend.Error($"ollama pull {model} exited {pull.ExitCode}: {await stderrTask}");
        }
        catch (Exception ex)
        {
            Log.Backend.Error($"ollama pull {model} failed to start: {ex.Message}");
        }
    }
}
