namespace CloudRunner.HostAgent;

internal sealed record AgentRequest(
    string Command,
    string? Url = null,
    string? Sha256 = null,
    string[]? Arguments = null);

internal sealed record AgentResponse(
    bool Success,
    string Message,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? WindowTitle = null,
    int? ExitCode = null);
