using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CloudRunner.HostAgent;

internal sealed class AgentServer : IAsyncDisposable
{
    private readonly ExecutionSession _session = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.WaitForConnectionAsync(cancellationToken);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        var shouldExit = false;
        while (!shouldExit && !cancellationToken.IsCancellationRequested)
        {
            // The connection stays open for the life of the controller. If the controller
            // crashes or is killed, EOF causes this agent to stop its child process.
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AgentRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<AgentRequest>(line, JsonOptions);
            }
            catch (JsonException)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new AgentResponse(false, "Invalid request JSON."), JsonOptions));
                continue;
            }

            if (request is null)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new AgentResponse(false, "Empty request."), JsonOptions));
                continue;
            }

            AgentResponse response;
            try
            {
                switch (request.Command.Trim().ToLowerInvariant())
                {
                    case "start":
                        response = await _session.StartAsync(
                            request.Url ?? string.Empty,
                            request.Sha256 ?? string.Empty,
                            request.Arguments ?? [],
                            cancellationToken);
                        break;

                    case "start-test-window":
                        response = await _session.StartTestWindowAsync(cancellationToken);
                        break;

                    case "status":
                        response = _session.GetStatus();
                        break;

                    case "stop":
                        response = await _session.StopAsync();
                        break;

                    case "shutdown":
                        response = await _session.StopAsync();
                        shouldExit = true;
                        response = response with
                        {
                            Message = "HostAgent shutdown requested. " + response.Message
                        };
                        break;

                    default:
                        response = new AgentResponse(false, "Unknown HostAgent command.");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                response = new AgentResponse(false, "The HostAgent command was cancelled.");
            }
            catch (Exception ex)
            {
                response = new AgentResponse(false, ex.Message);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
    }
}
