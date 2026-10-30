using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CloudRunner.HostAgent;

internal sealed class ReleaseAssetDownloader
{
    private const long MaximumDownloadBytes = 100L * 1024 * 1024;
    private const int MaximumRedirects = 5;

    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
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
            throw new InvalidOperationException("The EXE exceeds the 100 MB download limit.");
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
                    throw new InvalidOperationException("The EXE exceeds the 100 MB download limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        var fileInfo = new FileInfo(destinationPath);
        if (fileInfo.Length < 2)
        {
            throw new InvalidOperationException("The downloaded asset is empty or too small to be a Windows executable.");
        }

        await using (var file = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var first = file.ReadByte();
            var second = file.ReadByte();
            if (first != 0x4D || second != 0x5A)
            {
                throw new InvalidOperationException("The downloaded asset does not have a Windows MZ executable header.");
            }
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
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Only a direct HTTPS GitHub Release asset URL is accepted (no query string or fragment).");
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!Regex.IsMatch(
            path,
            @"^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "URL must point directly to an .exe under /owner/repository/releases/download/tag/.");
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
