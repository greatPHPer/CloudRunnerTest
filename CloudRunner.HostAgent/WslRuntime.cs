using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudRunner.HostAgent;

internal static class WslRuntime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static string WslExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static async Task<string> GetEnvironmentSummaryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(WslExePath))
        {
            throw new PlatformNotSupportedException(
                "Windows Subsystem for Linux was not found. Install WSL from an elevated PowerShell using: wsl --install");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19044))
        {
            throw new PlatformNotSupportedException(
                "Linux GUI applications through WSLg require Windows 10 build 19044 or later, or Windows 11.");
        }

        var status = await RunAsync(["--status"], cancellationToken);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "WSL is installed but could not report its status. " + FirstUsefulLine(status.StandardError, status.StandardOutput));
        }

        var environment = await RunAsync(
            ["--exec", "/bin/sh", "-c",
             "printf '%s\\n%s\\n%s\\n%s\\n' \"$WSL_DISTRO_NAME\" \"$DISPLAY\" \"$WAYLAND_DISPLAY\" \"$(id -u)\""],
            cancellationToken);

        if (environment.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "WSL has no usable default Linux distribution. Install Ubuntu with: wsl --install -d Ubuntu. " +
                FirstUsefulLine(environment.StandardError, environment.StandardOutput));
        }

        var lines = environment.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var distro = lines.ElementAtOrDefault(0) ?? "default distribution";
        var display = lines.ElementAtOrDefault(1) ?? string.Empty;
        var wayland = lines.ElementAtOrDefault(2) ?? string.Empty;
        var userId = lines.ElementAtOrDefault(3) ?? string.Empty;

        if (userId == "0")
        {
            throw new InvalidOperationException(
                $"The default WSL user for '{distro}' is root. CloudRunner will not launch downloaded applications as root. " +
                "Set a regular default Linux user for this distribution, then retry.");
        }

        if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(wayland))
        {
            throw new InvalidOperationException(
                $"WSL distribution '{distro}' is running, but WSLg GUI display variables were not found. " +
                "Use a WSL 2 distribution on a WSLg-supported Windows version, then run wsl --update and wsl --shutdown.");
        }

        var displayDescription = !string.IsNullOrWhiteSpace(wayland)
            ? $"Wayland={wayland}, DISPLAY={display}"
            : $"DISPLAY={display}";

        return $"WSL is ready. Distribution: {distro}. Linux user ID: {userId}. GUI display: {displayDescription}.";
    }

    public static async Task<string> ConvertWindowsPathAsync(
        string windowsPath,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(["--exec", "wslpath", "-a", windowsPath], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "WSL could not translate the downloaded file path. " +
                FirstUsefulLine(result.StandardError, result.StandardOutput));
        }

        var path = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WSL returned an invalid Linux file path.");
        }

        return path;
    }

    public static async Task MakeExecutableAsync(string linuxPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["--exec", "/bin/chmod", "+x", "--", linuxPath], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "WSL could not mark the downloaded Linux application as executable. " +
                FirstUsefulLine(result.StandardError, result.StandardOutput));
        }
    }

    public static async Task<string> CreateLinuxSessionDirectoryAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException("Linux session ID must be a 32-character GUID.", nameof(sessionId));
        }

        var homeResult = await RunAsync(["--exec", "/usr/bin/printenv", "HOME"], cancellationToken);
        if (homeResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not determine the default WSL user's home directory. " +
                FirstUsefulLine(homeResult.StandardError, homeResult.StandardOutput));
        }

        var home = homeResult.StandardOutput.Trim().TrimEnd('/');
        if (!home.StartsWith("/home/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CloudRunner requires a regular WSL user with a home directory under /home. Root or system users are not supported.");
        }

        var sessionDirectory = $"{home}/.cache/cloudrunner/jobs/{sessionId}";
        var create = await RunAsync(
            ["--exec", "/bin/mkdir", "-p", "--", sessionDirectory],
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create the Linux session folder inside the WSL filesystem. " +
                FirstUsefulLine(create.StandardError, create.StandardOutput));
        }

        return sessionDirectory;
    }

    public static async Task CopyLinuxFileAsync(
        string sourceLinuxPath,
        string destinationLinuxPath,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["--exec", "/bin/cp", "--", sourceLinuxPath, destinationLinuxPath],
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not copy the downloaded application into the WSL Linux filesystem. " +
                FirstUsefulLine(result.StandardError, result.StandardOutput));
        }
    }

    public static async Task RemoveLinuxSessionDirectoryAsync(
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) ||
            !Guid.TryParseExact(Path.GetFileName(sessionDirectory), "N", out _))
        {
            return;
        }

        var homeResult = await RunAsync(["--exec", "/usr/bin/printenv", "HOME"], cancellationToken);
        if (homeResult.ExitCode != 0)
        {
            return;
        }

        var home = homeResult.StandardOutput.Trim().TrimEnd('/');
        var expectedPrefix = $"{home}/.cache/cloudrunner/jobs/";
        if (!sessionDirectory.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove a Linux folder outside CloudRunner's WSL job directory.");
        }

        _ = await RunAsync(
            ["--exec", "/bin/rm", "-rf", "--", sessionDirectory],
            cancellationToken);
    }

    public static async Task EnsureExecutableExistsAsync(
        string linuxPath,
        string installHint,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(["--exec", "/usr/bin/test", "-x", linuxPath], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new FileNotFoundException(
                $"The WSL GUI test program was not found at {linuxPath}. Install it inside Ubuntu with: {installHint}");
        }
    }

    public static Process StartLinuxApplication(
        string linuxExecutablePath,
        string linuxPidFilePath,
        IReadOnlyList<string> arguments,
        bool isAppImage)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WslExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("/usr/bin/setsid");
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");

        var script = isAppImage
            ? "export APPIMAGE_EXTRACT_AND_RUN=1; printf '%s' \"$$\" > \"$1\"; shift; exec \"$@\"" 
            : "printf '%s' \"$$\" > \"$1\"; shift; exec \"$@\"";

        // The shell writes its PID before replacing itself with the application.
        // setsid gives this application a dedicated process group, allowing Stop
        // to terminate the application and its same-session child processes.
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("cloudrunner");
        startInfo.ArgumentList.Add(linuxPidFilePath);
        startInfo.ArgumentList.Add(linuxExecutablePath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the WSL Linux application session.");
    }

    public static async Task StopLinuxProcessGroupAsync(
        string linuxPidFilePath,
        bool force,
        CancellationToken cancellationToken)
    {
        var signal = force ? "KILL" : "TERM";
        var script =
            "pid=$(cat \"$1\" 2>/dev/null || true); " +
            "case \"$pid\" in ''|*[!0-9]*) exit 0;; esac; " +
            $"/bin/kill -{signal} -- \"-$pid\" 2>/dev/null || " +
            $"/bin/kill -{signal} \"$pid\" 2>/dev/null || true";

        _ = await RunAsync(
            ["--exec", "/bin/sh", "-c", script, "cloudrunner-stop", linuxPidFilePath],
            cancellationToken);
    }

    public static HashSet<long> SnapshotVisibleWindows() =>
        EnumerateVisibleWindows().Select(window => window.Handle).ToHashSet();

    public static (long Handle, string Title)? FindNewVisibleWindow(HashSet<long> previousHandles)
    {
        return EnumerateVisibleWindows()
            .Where(window => !previousHandles.Contains(window.Handle))
            .OrderByDescending(window => window.Area)
            .Select(window => ((long Handle, string Title)?)(window.Handle, window.Title))
            .FirstOrDefault();
    }

    private static List<VisibleWindow> EnumerateVisibleWindows()
    {
        var windows = new List<VisibleWindow>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            _ = GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title) ||
                title.Contains("CloudRunner", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!GetWindowRect(handle, out var rect))
            {
                return true;
            }

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            if (width < 120 || height < 80)
            {
                return true;
            }

            windows.Add(new VisibleWindow(handle.ToInt64(), title, (long)width * height));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WslExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start wsl.exe.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Preserve the cancellation or timeout error.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("The WSL command did not finish within 20 seconds.");
        }
    }

    private static string FirstUsefulLine(string first, string second)
    {
        var value = !string.IsNullOrWhiteSpace(first) ? first : second;
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "No additional details were returned.";
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record VisibleWindow(long Handle, string Title, long Area);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
