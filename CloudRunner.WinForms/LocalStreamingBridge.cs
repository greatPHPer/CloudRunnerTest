using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CloudRunner.WinForms;

internal sealed class LocalStreamingBridge : IDisposable
{
    public const int VncPort = 5900;
    public const int WebSocketPort = 6080;

    private readonly string _installRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudRunner", "Streaming");

    private Process? _process;

    public string PythonPath => Path.Combine(_installRoot, ".venv", "Scripts", "python.exe");
    public string NoVncPath => Path.Combine(_installRoot, "noVNC");
    public string ViewerUrl =>
        $"http://127.0.0.1:{WebSocketPort}/vnc.html#autoconnect=1&path=websockify&resize=scale&reconnect=1";

    public bool ToolsInstalled =>
        File.Exists(PythonPath) && File.Exists(Path.Combine(NoVncPath, "vnc.html"));

    public bool BridgeRunning => _process is { HasExited: false };

    public async Task<bool> IsVncServerAvailableAsync(CancellationToken cancellationToken = default) =>
        await CanConnectAsync(VncPort, 700, cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!ToolsInstalled)
        {
            throw new InvalidOperationException(
                "Streaming tools are not installed. Click 'Install noVNC + websockify' first.");
        }

        if (!await IsVncServerAvailableAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "TightVNC is not accepting a local connection on 127.0.0.1:5900. " +
                "Install TightVNC, start its Server in application mode, and check its access/password settings.");
        }

        if (await CanConnectAsync(WebSocketPort, 300, cancellationToken))
        {
            throw new InvalidOperationException(
                "Port 6080 is already in use. Stop the other websockify/noVNC instance before continuing.");
        }

        Stop();

        var startInfo = new ProcessStartInfo
        {
            FileName = PythonPath,
            WorkingDirectory = _installRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Bind explicitly to IPv4 loopback. Do not change this to 0.0.0.0.
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("websockify");
        startInfo.ArgumentList.Add("--web");
        startInfo.ArgumentList.Add(NoVncPath);
        startInfo.ArgumentList.Add($"127.0.0.1:{WebSocketPort}");
        startInfo.ArgumentList.Add($"127.0.0.1:{VncPort}");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Debug.WriteLine("[websockify] " + e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Debug.WriteLine("[websockify] " + e.Data);
        };

        if (!_process.Start())
            throw new InvalidOperationException("Could not start websockify.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                Stop();
                throw new InvalidOperationException(
                    $"websockify stopped during startup (exit code {exitCode}). Check the Python/websockify installation.");
            }

            if (await CanConnectAsync(WebSocketPort, 250, cancellationToken))
                return;

            await Task.Delay(250, cancellationToken);
        }

        Stop();
        throw new TimeoutException("websockify did not start listening on 127.0.0.1:6080.");
    }

    public void Stop()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cleanup is best-effort; Windows may already have terminated the process.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task<bool> CanConnectAsync(
        int port, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
