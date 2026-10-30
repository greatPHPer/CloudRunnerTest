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

            // Auto tries Windows Graphics Capture first, then falls back to
            // DXGI Desktop Duplication if WGC fails on this device/window.
            using var bitmap = WindowCapture.CaptureWindow(
                new IntPtr(windowHandle),
                CaptureStrategy.Auto,
                timeoutMs: 3000);

            if (bitmap is null)
            {
                throw new InvalidOperationException(
                    "Both Windows Graphics Capture and the DXGI fallback failed to return a frame. " +
                    "Make sure the application window is open, visible, not minimized, and displayed on a D3D11-capable desktop.");
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }, cancellationToken);
    }
}
