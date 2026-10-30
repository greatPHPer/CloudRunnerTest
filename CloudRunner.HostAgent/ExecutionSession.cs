using System.Diagnostics;
using System.Security.Principal;

namespace CloudRunner.HostAgent;

internal sealed class ExecutionSession : IAsyncDisposable
{
    private readonly ReleaseAssetDownloader _downloader = new();
    private readonly string _jobsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudRunner", "Jobs");

    private Process? _process;
    private string? _jobDirectory;

    public async Task<AgentResponse> StartAsync(
        string url,
        string sha256,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        CleanupExitedProcess();

        if (_process is { HasExited: false })
        {
            return CurrentProcessResponse("An EXE is already running. Stop it before starting another session.", success: false);
        }

        EnsureStandardUserContext();
        ValidateArguments(arguments);

        Directory.CreateDirectory(_jobsRoot);
        _jobDirectory = Path.Combine(_jobsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobDirectory);

        var exePath = Path.Combine(_jobDirectory, "application.exe");

        try
        {
            var verifiedHash = await _downloader.DownloadAndVerifyAsync(
                url, sha256, exePath, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _jobDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                throw new InvalidOperationException("Windows did not start the downloaded application.");
            }

            var window = await WaitForMainWindowAsync(_process, cancellationToken);

            var message = window.Handle == 0
                ? "EXE started and SHA-256 verified, but no top-level window was detected yet. Refresh status after the application finishes opening."
                : "EXE started and SHA-256 verified. Its top-level window is ready for a local preview.";

            return new AgentResponse(
                true,
                $"{message} SHA-256: {verifiedHash}",
                _process.Id,
                window.Handle,
                window.Title);
        }
        catch
        {
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        await _process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // Preserve the original launch/download error.
                }

                _process.Dispose();
                _process = null;
            }

            DeleteJobDirectory();
            throw;
        }
    }


    public async Task<AgentResponse> StartNotepadAsync(CancellationToken cancellationToken)
    {
        CleanupExitedProcess();

        if (_process is { HasExited: false })
        {
            return CurrentProcessResponse("An EXE is already running. Stop it before starting the Notepad test.", success: false);
        }

        var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(notepadPath))
        {
            throw new FileNotFoundException("Windows Notepad was not found.", notepadPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = notepadPath,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException("Windows did not start Notepad.");
        }

        var window = await WaitForMainWindowAsync(_process, cancellationToken);
        return new AgentResponse(
            true,
            window.Handle == 0
                ? "Notepad started, but its top-level window is not ready yet. Refresh status."
                : "Notepad started. Its window is ready for a capture preview.",
            _process.Id,
            window.Handle,
            window.Title);
    }

    public AgentResponse GetStatus()
    {
        CleanupExitedProcess();

        if (_process is null)
        {
            return new AgentResponse(true, "No CloudRunner application is running.");
        }

        return CurrentProcessResponse("Application is running.");
    }

    public async Task<AgentResponse> StopAsync()
    {
        if (_process is null)
        {
            DeleteJobDirectory();
            return new AgentResponse(true, "No application was running. Temporary job folder cleanup was attempted.");
        }

        var process = _process;
        var processId = process.Id;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token);
            }

            var exitCode = process.HasExited ? process.ExitCode : (int?)null;
            return new AgentResponse(true, "Application stopped and its process tree was terminated.", processId, ExitCode: exitCode);
        }
        catch (OperationCanceledException)
        {
            return new AgentResponse(false, "Timed out while waiting for the application process tree to exit.", processId);
        }
        catch (Exception ex)
        {
            return new AgentResponse(false, "Could not stop application: " + ex.Message, processId);
        }
        finally
        {
            process.Dispose();
            _process = null;
            DeleteJobDirectory();
        }
    }

    private static async Task<(long Handle, string Title)> WaitForMainWindowAsync(
        Process process, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The EXE exited during startup with code {process.ExitCode}.");
            }

            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                return (handle.ToInt64(), process.MainWindowTitle);
            }

            await Task.Delay(150, cancellationToken);
        }

        process.Refresh();
        return (process.MainWindowHandle.ToInt64(), process.MainWindowTitle);
    }

    private AgentResponse CurrentProcessResponse(string message, bool success = true)
    {
        if (_process is null)
        {
            return new AgentResponse(true, message);
        }

        try
        {
            _process.Refresh();
            return new AgentResponse(
                success,
                message,
                _process.Id,
                _process.MainWindowHandle.ToInt64(),
                _process.MainWindowTitle);
        }
        catch (InvalidOperationException)
        {
            return new AgentResponse(true, "Application is no longer running.");
        }
    }

    private void CleanupExitedProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                return;
            }
        }
        catch
        {
            // Treat an inaccessible or invalid process handle as exited.
        }

        _process.Dispose();
        _process = null;
        DeleteJobDirectory();
    }

    private void DeleteJobDirectory()
    {
        var directory = _jobDirectory;
        _jobDirectory = null;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Windows may still have a file handle open; next session can clean stale job folders.
        }
    }

    private static void EnsureStandardUserContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "CloudRunner will not launch downloaded EXEs from an elevated Administrator session. " +
                "Close CloudRunner and restart it as a standard, non-elevated user.");
        }
    }

    private static void ValidateArguments(string[] arguments)
    {
        if (arguments.Length > 64)
        {
            throw new InvalidOperationException("A maximum of 64 command-line arguments is allowed.");
        }

        if (arguments.Any(argument => argument.Length > 4096) ||
            arguments.Sum(argument => (long)argument.Length) > 16000)
        {
            throw new InvalidOperationException("Command-line arguments exceed the CloudRunner size limit.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
