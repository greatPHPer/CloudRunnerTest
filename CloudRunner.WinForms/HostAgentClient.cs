using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudRunner.WinForms;

internal sealed class HostAgentClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _agentProcess;
    private string? _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task<AgentReply> SendAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer!.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            var responseLine = await _reader!.ReadLineAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new IOException("HostAgent closed the named pipe without returning a response.");
            }

            return JsonSerializer.Deserialize<AgentReply>(responseLine, JsonOptions)
                   ?? throw new IOException("HostAgent returned an empty response.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        ClosePipe();
        EnsureAgentProcess();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_agentProcess is null || _agentProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "CloudRunner HostAgent exited unexpectedly. Check %LOCALAPPDATA%\\CloudRunner\\Logs\\host-agent.log.");
            }

            var candidate = new NamedPipeClientStream(
                ".", _pipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await candidate.ConnectAsync(300, cancellationToken);
                _pipe = candidate;
                _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };
                return;
            }
            catch (TimeoutException) when (attempt < 39)
            {
                candidate.Dispose();
                await Task.Delay(150, cancellationToken);
            }
            catch (IOException) when (attempt < 39)
            {
                candidate.Dispose();
                await Task.Delay(150, cancellationToken);
            }
        }

        throw new TimeoutException("Could not connect to the local CloudRunner HostAgent.");
    }

    private void EnsureAgentProcess()
    {
        if (_agentProcess is { HasExited: false } && !string.IsNullOrWhiteSpace(_pipeName))
        {
            return;
        }

        _agentProcess?.Dispose();
        _agentProcess = null;

        var agentPath = Path.Combine(AppContext.BaseDirectory, "Agent", "CloudRunner.HostAgent.exe");
        if (!File.Exists(agentPath))
        {
            throw new FileNotFoundException(
                "CloudRunner.HostAgent.exe was not found beside the WinForms application. Rebuild/publish the full solution.",
                agentPath);
        }

        _pipeName = "CloudRunner_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            WorkingDirectory = Path.GetDirectoryName(agentPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(_pipeName);

        _agentProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start CloudRunner.HostAgent.");
    }

    private void ClosePipe()
    {
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_agentProcess is null)
            {
                return;
            }

            try
            {
                if (_pipe is { IsConnected: true } && _writer is not null && _reader is not null &&
                    !_agentProcess.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(
                        new AgentCommand("shutdown"), JsonOptions));
                    var response = await _reader.ReadLineAsync(timeout.Token);
                    _ = response;
                    await _agentProcess.WaitForExitAsync(timeout.Token);
                }
                else if (!_agentProcess.HasExited)
                {
                    _agentProcess.Kill(entireProcessTree: true);
                    await _agentProcess.WaitForExitAsync();
                }
            }
            catch
            {
                try
                {
                    if (!_agentProcess.HasExited)
                    {
                        _agentProcess.Kill(entireProcessTree: true);
                        await _agentProcess.WaitForExitAsync();
                    }
                }
                catch
                {
                    // Shutdown is best-effort during application exit.
                }
            }
            finally
            {
                ClosePipe();
                _agentProcess.Dispose();
                _agentProcess = null;
                _pipeName = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

internal sealed record AgentCommand(
    string Command,
    string? Url = null,
    string? Sha256 = null,
    string[]? Arguments = null);

internal sealed record AgentReply(
    bool Success,
    string Message,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? WindowTitle = null,
    int? ExitCode = null);
