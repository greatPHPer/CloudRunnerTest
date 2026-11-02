using System.Diagnostics;
using System.Security.Principal;

namespace CloudRunner.HostAgent;

internal sealed class ExecutionSession : IAsyncDisposable
{
    private readonly ReleaseAssetDownloader _downloader = new();
    private readonly LinuxReleaseAssetDownloader _linuxDownloader = new();
    private readonly string _jobsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudRunner", "Jobs");

    private Process? _process;
    private Process? _linuxProcess;
    private KillOnCloseJob? _job;
    private string? _jobDirectory;
    private string? _linuxPidFileWslPath;
    private HashSet<long>? _linuxPreviousWindows;
    private long? _linuxWindowHandle;
    private string? _linuxWindowTitle;

    public async Task<AgentResponse> StartAsync(
        string url,
        string sha256,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        CleanupExitedProcess();

        if (HasActiveProcess())
        {
            return CurrentActiveProcessResponse(
                "An application is already running. Stop it before starting another session.",
                success: false);
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

            _job = new KillOnCloseJob();
            _job.Assign(_process);

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
                    // Preserve the original download or launch error.
                }

                _process.Dispose();
                _process = null;
            }

            _job?.Dispose();
            _job = null;
            DeleteJobDirectory();
            throw;
        }
    }

    public async Task<AgentResponse> StartLinuxAsync(
        string url,
        string sha256,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        CleanupExitedProcess();

        if (HasActiveProcess())
        {
            return CurrentActiveProcessResponse(
                "An application is already running. Stop it before starting another session.",
                success: false);
        }

        EnsureStandardUserContext();
        ValidateArguments(arguments);

        var environmentSummary = await WslRuntime.GetEnvironmentSummaryAsync(cancellationToken);

        Directory.CreateDirectory(_jobsRoot);
        _jobDirectory = Path.Combine(_jobsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobDirectory);

        try
        {
            var extension = Uri.TryCreate(url, UriKind.Absolute, out var assetUri)
                ? Path.GetExtension(assetUri.AbsolutePath)
                : string.Empty;
            var appImage = extension.Equals(".AppImage", StringComparison.OrdinalIgnoreCase);
            var linuxAssetPath = Path.Combine(
                _jobDirectory,
                appImage ? "application.AppImage" : "application.elf");
            var pidFilePath = Path.Combine(_jobDirectory, "application.pid");

            var verifiedHash = await _linuxDownloader.DownloadAndVerifyAsync(
                url, sha256, linuxAssetPath, cancellationToken);

            var linuxExecutablePath = await WslRuntime.ConvertWindowsPathAsync(
                linuxAssetPath, cancellationToken);
            _linuxPidFileWslPath = await WslRuntime.ConvertWindowsPathAsync(
                pidFilePath, cancellationToken);

            await WslRuntime.MakeExecutableAsync(linuxExecutablePath, cancellationToken);

            _linuxPreviousWindows = WslRuntime.SnapshotVisibleWindows();
            _linuxWindowHandle = null;
            _linuxWindowTitle = null;

            _linuxProcess = WslRuntime.StartLinuxApplication(
                linuxExecutablePath,
                _linuxPidFileWslPath,
                arguments,
                appImage);

            (long Handle, string Title)? window = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                window = WslRuntime.FindNewVisibleWindow(_linuxPreviousWindows);
                if (window.HasValue || _linuxProcess.HasExited)
                {
                    break;
                }

                await Task.Delay(250, cancellationToken);
            }

            if (window.HasValue)
            {
                _linuxWindowHandle = window.Value.Handle;
                _linuxWindowTitle = window.Value.Title;
            }

            if (_linuxProcess.HasExited && !window.HasValue)
            {
                var exitCode = _linuxProcess.ExitCode;
                throw new InvalidOperationException(
                    $"The Linux application exited during startup (WSL session exit code {exitCode}). " +
                    "Check that its Linux libraries are installed. For AppImage files, CloudRunner enables extract-and-run mode, but some AppImages still require additional packages.");
            }

            var message = window.HasValue
                ? "Linux application started in WSLg. Its new desktop window is ready for capture."
                : "Linux application started in WSLg, but no new desktop window was detected yet. Use Refresh process / window status after it finishes opening.";

            return new AgentResponse(
                true,
                $"{environmentSummary} {message} SHA-256: {verifiedHash}",
                _linuxProcess.Id,
                _linuxWindowHandle,
                _linuxWindowTitle);
        }
        catch
        {
            if (_linuxProcess is not null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_linuxPidFileWslPath))
                    {
                        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await WslRuntime.StopLinuxProcessGroupAsync(
                            _linuxPidFileWslPath, force: true, stopTimeout.Token);
                    }
                }
                catch
                {
                    // Fall back to terminating the WSL launcher below.
                }

                try
                {
                    if (!_linuxProcess.HasExited)
                    {
                        _linuxProcess.Kill(entireProcessTree: true);
                        await _linuxProcess.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // Preserve the original Linux download or launch error.
                }

                _linuxProcess.Dispose();
                _linuxProcess = null;
            }

            ResetLinuxWindowState();
            DeleteJobDirectory();
            throw;
        }
    }

    public async Task<AgentResponse> StartTestWindowAsync(CancellationToken cancellationToken)
    {
        CleanupExitedProcess();

        if (HasActiveProcess())
        {
            return CurrentActiveProcessResponse(
                "An application is already running. Stop it before starting the capture test.",
                success: false);
        }

        EnsureStandardUserContext();

        var testWindowPath = Path.Combine(AppContext.BaseDirectory, "CloudRunner.TestWindow.exe");
        if (!File.Exists(testWindowPath))
        {
            throw new FileNotFoundException(
                "The dedicated CloudRunner test window was not bundled beside HostAgent. Rebuild the complete solution.",
                testWindowPath);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = testWindowPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                throw new InvalidOperationException("Windows did not start the CloudRunner capture test window.");
            }

            _job = new KillOnCloseJob();
            _job.Assign(_process);

            var window = await WaitForMainWindowAsync(_process, cancellationToken);
            return new AgentResponse(
                true,
                window.Handle == 0
                    ? "The capture test process started, but its window is not ready. Refresh status."
                    : "Dedicated CloudRunner test window started. Its HWND is ready for capture.",
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
                    // Preserve the original startup error.
                }

                _process.Dispose();
                _process = null;
            }

            _job?.Dispose();
            _job = null;
            throw;
        }
    }

    public AgentResponse GetStatus()
    {
        CleanupExitedProcess();

        if (_process is not null)
        {
            return CurrentProcessResponse("Windows application is running.");
        }

        if (_linuxProcess is not null)
        {
            return CurrentLinuxProcessResponse("Linux application is running in WSL.");
        }

        return new AgentResponse(true, "No CloudRunner application is running.");
    }

    public async Task<AgentResponse> StopAsync()
    {
        if (_linuxProcess is not null)
        {
            return await StopLinuxAsync();
        }

        if (_process is null)
        {
            _job?.Dispose();
            _job = null;
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
            _job?.Dispose();
            _job = null;
            DeleteJobDirectory();
        }
    }

    private async Task<AgentResponse> StopLinuxAsync()
    {
        var process = _linuxProcess!;
        var processId = process.Id;
        var stopMessage = "Linux application process group stopped.";

        try
        {
            if (!process.HasExited && !string.IsNullOrWhiteSpace(_linuxPidFileWslPath))
            {
                using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await WslRuntime.StopLinuxProcessGroupAsync(
                    _linuxPidFileWslPath, force: false, gracefulTimeout.Token);

                using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(waitTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    using var forceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await WslRuntime.StopLinuxProcessGroupAsync(
                        _linuxPidFileWslPath, force: true, forceTimeout.Token);

                    using var finalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(finalTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                        stopMessage = "Linux application was force-stopped after it did not respond to termination.";
                    }
                }
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            return new AgentResponse(true, stopMessage, processId);
        }
        catch (Exception ex)
        {
            return new AgentResponse(false, "Could not stop the WSL application cleanly: " + ex.Message, processId);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort cleanup on Stop.
                }
            }

            process.Dispose();
            _linuxProcess = null;
            ResetLinuxWindowState();
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
                    $"The launched process exited before a top-level window was found (exit code {process.ExitCode}). " +
                    "Some packaged Windows apps and launcher EXEs hand off to another process. " +
                    "Use the executable that owns the actual application window.");
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

    private AgentResponse CurrentLinuxProcessResponse(string message, bool success = true)
    {
        if (_linuxProcess is null)
        {
            return new AgentResponse(true, message);
        }

        try
        {
            if (_linuxPreviousWindows is not null)
            {
                var discovered = WslRuntime.FindNewVisibleWindow(_linuxPreviousWindows);
                if (discovered.HasValue)
                {
                    _linuxWindowHandle = discovered.Value.Handle;
                    _linuxWindowTitle = discovered.Value.Title;
                }
            }

            if (_linuxProcess.HasExited)
            {
                return new AgentResponse(
                    false,
                    "The WSL application process has exited.",
                    _linuxProcess.Id,
                    _linuxWindowHandle,
                    _linuxWindowTitle,
                    _linuxProcess.ExitCode);
            }

            return new AgentResponse(
                success,
                message,
                _linuxProcess.Id,
                _linuxWindowHandle,
                _linuxWindowTitle);
        }
        catch (InvalidOperationException)
        {
            return new AgentResponse(true, "Linux application is no longer running.");
        }
    }

    private AgentResponse CurrentActiveProcessResponse(string message, bool success = true)
    {
        if (_process is not null)
        {
            return CurrentProcessResponse(message, success);
        }

        if (_linuxProcess is not null)
        {
            return CurrentLinuxProcessResponse(message, success);
        }

        return new AgentResponse(success, message);
    }

    private bool HasActiveProcess() =>
        (_process is not null && !_process.HasExited) ||
        (_linuxProcess is not null && !_linuxProcess.HasExited);

    private void CleanupExitedProcess()
    {
        if (_linuxProcess is not null)
        {
            var linuxExited = false;
            try
            {
                linuxExited = _linuxProcess.HasExited;
            }
            catch
            {
                // Treat an inaccessible or invalid process handle as exited.
                linuxExited = true;
            }

            if (linuxExited)
            {
                _linuxProcess.Dispose();
                _linuxProcess = null;
                ResetLinuxWindowState();
                DeleteJobDirectory();
            }
        }

        if (_process is null)
        {
            return;
        }

        var windowsExited = false;
        try
        {
            windowsExited = _process.HasExited;
        }
        catch
        {
            // Treat an inaccessible or invalid process handle as exited.
            windowsExited = true;
        }

        if (!windowsExited)
        {
            return;
        }

        _process.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
        DeleteJobDirectory();
    }

    private void ResetLinuxWindowState()
    {
        _linuxPidFileWslPath = null;
        _linuxPreviousWindows = null;
        _linuxWindowHandle = null;
        _linuxWindowTitle = null;
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
                "CloudRunner will not launch downloaded applications from an elevated Administrator session. " +
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
