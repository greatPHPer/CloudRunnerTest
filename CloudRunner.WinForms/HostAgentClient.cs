using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudRunner.WinForms;

internal sealed class HostAgentClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private Process? _agentProcess;
    private string? _pipeName;

    public async Task<AgentReply> SendAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        await EnsureAgentStartedAsync(cancellationToken);

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_agentProcess is null || _agentProcess.HasExited)
            {
                throw new InvalidOperationException("CloudRunner HostAgent exited unexpectedly.");
            }

            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(300, cancellationToken);
            }
            catch (TimeoutException) when (attempt < 39)
            {
                await Task.Delay(150, cancellationToken);
                continue;
            }
            catch (IOException) when (attempt < 39)
            {
                await Task.Delay(150, cancellationToken);
                continue;
            }

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            var responseLine = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new IOException("HostAgent closed the named pipe without returning a response.");
            }

            return JsonSerializer.Deserialize<AgentReply>(responseLine, JsonOptions)
                   ?? throw new IOException("HostAgent returned an empty response.");
        }

        throw new TimeoutException("Could not connect to the local CloudRunner HostAgent.");
    }

    private Task EnsureAgentStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_agentProcess is { HasExited: false } && !string.IsNullOrWhiteSpace(_pipeName))
        {
            return Task.CompletedTask;
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

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_agentProcess is null)
        {
            return;
        }

        try
        {
            if (!_agentProcess.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync(new AgentCommand("shutdown"), timeout.Token);
                await _agentProcess.WaitForExitAsync(timeout.Token);
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
            _agentProcess.Dispose();
            _agentProcess = null;
            _pipeName = null;
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
