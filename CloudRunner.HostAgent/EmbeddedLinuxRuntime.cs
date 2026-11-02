using System.Diagnostics;

namespace CloudRunner.HostAgent;

internal sealed record EmbeddedLinuxRuntimePaths(
    string RootDirectory,
    string QemuBinaryDirectory,
    string QemuShareDirectory,
    string QemuSystemExecutable,
    string QemuImageExecutable,
    string KernelImage,
    string RootFilesystemImage);

internal static class EmbeddedLinuxRuntime
{
    public static EmbeddedLinuxRuntimePaths Resolve()
    {
        // HostAgent is published into <CloudRunner>/Agent, while the embedded
        // runtime is placed beside that folder under <CloudRunner>/Runtime.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime", "EmbeddedLinux"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Runtime", "EmbeddedLinux"))
        };

        var root = candidates.FirstOrDefault(Directory.Exists);
        if (root is null)
        {
            throw new DirectoryNotFoundException(
                "The bundled Embedded Linux runtime was not found. Expected Runtime\\EmbeddedLinux beside the CloudRunner application. " +
                "Download the CloudRunner Embedded Linux package and preserve its Runtime folder.");
        }

        var qemuBin = Path.Combine(root, "Qemu", "bin");
        var qemuShare = Path.Combine(root, "Qemu", "share", "qemu");
        var paths = new EmbeddedLinuxRuntimePaths(
            root,
            qemuBin,
            qemuShare,
            Path.Combine(qemuBin, "qemu-system-x86_64.exe"),
            Path.Combine(qemuBin, "qemu-img.exe"),
            Path.Combine(root, "Linux", "bzImage"),
            Path.Combine(root, "Linux", "rootfs.ext4"));

        var missing = new[]
        {
            paths.QemuSystemExecutable,
            paths.QemuImageExecutable,
            paths.KernelImage,
            paths.RootFilesystemImage
        }.Where(path => !File.Exists(path)).ToArray();

        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                "The Embedded Linux runtime package is incomplete. Missing: " +
                string.Join("; ", missing.Select(Path.GetFileName)));
        }

        if (!Directory.Exists(paths.QemuShareDirectory))
        {
            throw new DirectoryNotFoundException(
                "The QEMU share\\qemu firmware/data directory is missing from the Embedded Linux package.");
        }

        return paths;
    }

    public static async Task CreateWritableOverlayAsync(
        EmbeddedLinuxRuntimePaths runtime,
        string overlayPath,
        CancellationToken cancellationToken)
    {
        var rootSize = new FileInfo(runtime.RootFilesystemImage).Length;
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.QemuImageExecutable,
            WorkingDirectory = runtime.QemuBinaryDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("qcow2");
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add("raw");
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add(runtime.RootFilesystemImage);
        startInfo.ArgumentList.Add(overlayPath);
        startInfo.ArgumentList.Add(rootSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start qemu-img to create the guest disk overlay.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "qemu-img could not create a writable guest disk overlay. " +
                    FirstUsefulLine(stderr, stdout));
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Preserve cancellation/timeout.
            }

            throw;
        }
    }

    public static ProcessStartInfo CreateQemuStartInfo(
        EmbeddedLinuxRuntimePaths runtime,
        string application,
        string overlayPath,
        string serialLogPath)
    {
        if (application is not ("xeyes" or "xterm"))
        {
            throw new ArgumentOutOfRangeException(nameof(application), "Only xeyes and xterm are enabled in this guest-image milestone.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.QemuSystemExecutable,
            WorkingDirectory = runtime.QemuBinaryDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        // MSYS2 QEMU DLLs are bundled alongside qemu-system-x86_64.exe.
        startInfo.Environment["PATH"] = runtime.QemuBinaryDirectory +
            Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        Add("-name", $"CloudRunner Embedded Linux - {application}");
        Add("-machine", "q35,accel=whpx:tcg");
        Add("-cpu", "qemu64");
        Add("-m", "1536");
        Add("-smp", "2");
        Add("-kernel", runtime.KernelImage);
        Add("-drive", $"file={overlayPath},if=virtio,format=qcow2,cache=writeback");
        Add("-append", $"root=/dev/vda rootfstype=ext4 rw rootwait console=ttyS0 loglevel=4 cloudrunner.app={application}");
        Add("-device", "virtio-vga");
        Add("-device", "qemu-xhci");
        Add("-device", "usb-tablet");
        Add("-device", "usb-kbd");
        Add("-display", "sdl,show-cursor=on,window-close=on");
        Add("-serial", "file:" + serialLogPath.Replace('\\', '/'));
        Add("-monitor", "none");
        Add("-net", "none");
        Add("-rtc", "base=localtime");
        Add("-boot", "order=c");
        Add("-no-reboot");
        Add("-L", runtime.QemuShareDirectory);

        return startInfo;

        void Add(string name, string value)
        {
            startInfo.ArgumentList.Add(name);
            startInfo.ArgumentList.Add(value);
        }
    }

    private static string FirstUsefulLine(string first, string second)
    {
        var value = !string.IsNullOrWhiteSpace(first) ? first : second;
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "No additional details were returned.";
    }
}
