using System.Drawing.Imaging;
using WgcSharp;

namespace CloudRunner.WinForms;

internal static class WindowCaptureService
{
    public static Task<byte[]> CaptureJpegAsync(
        long windowHandle,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle), "A valid application window handle is required.");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException(
                "This CloudRunner build requires Windows 10 version 2004 (build 19041) or later for its selected capture dependency.");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var bitmap = WindowCapture.CaptureWindow(
                new IntPtr(windowHandle),
                CaptureStrategy.WgcOnly,
                timeoutMs: 3000);

            if (bitmap is null)
            {
                throw new InvalidOperationException(
                    "Windows Graphics Capture did not return a frame. Make sure the application window is open, visible and not minimized.");
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }, cancellationToken);
    }
}
