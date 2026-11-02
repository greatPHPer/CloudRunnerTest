using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CloudRunner.HostAgent;

internal sealed class LinuxReleaseAssetDownloader
{
    private const long MaximumDownloadBytes = 100L * 1024 * 1024;
    private const int MaximumRedirects = 5;

    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        ".appimage",
        ".bin",
        ".elf",
        ".run"
    };

    private static readonly HttpClient Client = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task<string> DownloadAndVerifyAsync(
        string sourceUrl,
        string expectedSha256,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = ValidateReleaseUrl(sourceUrl);

        if (!Regex.IsMatch(expectedSha256, @"^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        using var response = await SendWithSafeRedirectsAsync(source, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declaredLength &&
            declaredLength > MaximumDownloadBytes)
        {
            throw new InvalidOperationException("The Linux application exceeds the 100 MB download limit.");
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > MaximumDownloadBytes)
                {
                    throw new InvalidOperationException("The Linux application exceeds the 100 MB download limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        var fileInfo = new FileInfo(destinationPath);
        if (fileInfo.Length < 64)
        {
            throw new InvalidOperationException("The downloaded Linux application is empty or too small to be an ELF executable.");
        }

        var header = new byte[20];
        await using (var file = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = await file.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
            if (read != header.Length)
            {
                throw new InvalidOperationException("The downloaded Linux application has an incomplete ELF header.");
            }
        }

        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
        {
            throw new InvalidOperationException(
                "The downloaded asset is not a Linux ELF executable. For this first Linux milestone, use an x86-64 ELF binary or x86-64 AppImage.");
        }

        if (header[4] != 2 || header[5] != 1 || header[18] != 62 || header[19] != 0)
        {
            throw new InvalidOperationException(
                "This first Linux runtime supports 64-bit little-endian x86-64 Linux executables only. ARM and 32-bit binaries are not supported yet.");
        }

        await using var hashStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualHash = await SHA256.HashDataAsync(hashStream, cancellationToken);
        var expectedHash = Convert.FromHexString(expectedSha256);

        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch. Expected {expectedSha256.ToUpperInvariant()}, got {Convert.ToHexString(actualHash)}.");
        }

        return Convert.ToHexString(actualHash);
    }

    private static Uri ValidateReleaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Only a direct HTTPS GitHub Release asset URL is accepted (no query string or fragment).");
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!Regex.IsMatch(
            path,
            @"^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+$",
            RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "URL must point directly to one GitHub Release asset under /owner/repository/releases/download/tag/.");
        }

        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                "This Linux prototype accepts .AppImage, .bin, .elf, .run, or extensionless ELF assets. .deb and archive packages are not supported yet.");
        }

        return uri;
    }

    private static async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;

        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !current.IsDefaultPort ||
                !string.IsNullOrEmpty(current.UserInfo) ||
                !AllowedRedirectHosts.Contains(current.Host))
            {
                throw new InvalidOperationException("GitHub redirected the download to an unapproved host.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("CloudRunner-HostAgent/1.0");

            var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    throw new InvalidOperationException("GitHub returned a redirect without a destination.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            return response;
        }

        throw new InvalidOperationException("The GitHub Release download exceeded the redirect limit.");
    }
}
