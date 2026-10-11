using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenmitsuControl.Updates;

public sealed record UpdateRelease(string Version, string Tag, string HtmlUrl, Uri AssetUri, long SizeBytes, string Sha256);
public sealed record PreparedUpdate(UpdateRelease Release, string FilePath, string StagingDirectory) : IDisposable
{
    public void Dispose() => GitHubUpdateClient.TryDeleteFile(FilePath);
}
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GitHubUpdateClient(HttpClient httpClient)
{
    public const string Repository = "BigUziHert/MillBeam";
    public const long MaximumDownloadBytes = 256L * 1024 * 1024;
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumPages = 10;
    private static readonly Uri ApiBase = new($"https://api.github.com/repos/{Repository}/releases");

    // Redirects are followed explicitly only after validating their destination.
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<UpdateRelease?> CheckAsync(string currentVersion, bool includeDevelopment, CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            throw new UpdateException("The installed version could not be read. Download MillBeam from its GitHub releases page.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            JsonElement? candidate = null;
            ReleaseVersion? newest = null;
            for (var page = 1; page <= MaximumPages; page++)
            {
                using var response = await SendAsync(new Uri($"{ApiBase}?per_page=100&page={page}"), true, timeout.Token);
                await EnsureSuccessAsync(response, timeout.Token);
                var bytes = await ReadBoundedAsync(response.Content, MaximumMetadataBytes, timeout.Token);
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new UpdateException("GitHub returned an unexpected release list. Please try again later.");
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object) continue;
                    if (!TryBoolean(release, "draft", out var draft) || draft) continue;
                    if (!TryBoolean(release, "prerelease", out var prerelease)) continue;
                    var tag = ReadString(release, "tag_name");
                    if (!ReleaseVersion.TryParse(tag, out var version) || !tag!.StartsWith('v')) continue;
                    if (version.IsPrerelease != prerelease || (prerelease && (!includeDevelopment || !version.IsDevelopment))) continue;
                    if (version.CompareTo(current) <= 0 || (newest is not null && version.CompareTo(newest) <= 0)) continue;
                    candidate = release.Clone();
                    newest = version;
                }
                if (document.RootElement.GetArrayLength() < 100) break;
                if (page == MaximumPages)
                    throw new UpdateException("GitHub returned too many releases to check safely. Open the releases page to choose an update.");
            }
            return candidate is null ? null : ParseRelease(candidate.Value, newest!);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateException("The update check timed out. Check your internet connection and try again.", ex); }
        catch (HttpRequestException ex)
        { throw new UpdateException("Could not reach GitHub. Check your internet connection and try again.", ex); }
        catch (JsonException ex)
        { throw new UpdateException("GitHub returned invalid release information. Please try again later.", ex); }
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MillBeam", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "MillBeam.download");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await SendAsync(release.AssetUri, false, timeout.Token);
            await EnsureSuccessAsync(response, timeout.Token);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.SizeBytes)
                throw new UpdateException("The download size does not match the GitHub release. No update was installed.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using (var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += count;
                    if (total > release.SizeBytes || total > MaximumDownloadBytes)
                        throw new UpdateException("The download exceeded the expected size. No update was installed.");
                    digest.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                if (total != release.SizeBytes || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new UpdateException("The update failed its SHA-256 integrity check. No update was installed. Please download it again.");
                await output.FlushAsync(timeout.Token);
            }
            return new PreparedUpdate(release, filePath, directory);
        }
        catch (Exception ex)
        {
            TryDeleteFile(filePath);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new UpdateException("The download timed out. No update was installed. Please try again.", ex);
            if (ex is HttpRequestException)
                throw new UpdateException("The download was interrupted. No update was installed. Please try again.", ex);
            throw;
        }
    }

    private static UpdateRelease ParseRelease(JsonElement release, ReleaseVersion version)
    {
        var tag = ReadString(release, "tag_name")!;
        var expectedHtml = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
        var html = ReadString(release, "html_url");
        if (!string.Equals(html, expectedHtml, StringComparison.Ordinal))
            throw new UpdateException("The release page does not belong to the MillBeam repository.");
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new UpdateException("This release has no downloadable Windows application yet. Please try again after its build finishes.");
        var executable = assets.EnumerateArray().Where(x => ReadString(x, "name") == "MillBeam.exe").ToArray();
        if (executable.Length != 1)
            throw new UpdateException("This release does not have one complete MillBeam.exe download yet. Please try again after its build finishes.");
        var asset = executable[0];
        var digest = ReadString(asset, "digest");
        if (ReadString(asset, "state") != "uploaded" || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var sizeBytes) ||
            !Uri.TryCreate(ReadString(asset, "browser_download_url"), UriKind.Absolute, out var uri) || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new UpdateException("The release download is missing verified size or SHA-256 information. No update was downloaded.");
        var result = new UpdateRelease(version.Text, tag, html!, uri, sizeBytes, digest[7..]);
        ValidateRelease(result);
        return result;
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(release.Tag)}/MillBeam.exe";
        if (!ReleaseVersion.TryParse(release.Tag, out var version) || version.Text != release.Version ||
            release.AssetUri.AbsoluteUri != expected || release.AssetUri.UserInfo.Length != 0 ||
            release.SizeBytes <= 0 || release.SizeBytes > MaximumDownloadBytes ||
            release.Sha256.Length != 64 || !release.Sha256.All(char.IsAsciiHexDigit))
            throw new UpdateException("The update download metadata is not valid for MillBeam. No update was downloaded.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initial, bool metadata, CancellationToken cancellationToken)
    {
        var uri = initial;
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            if (!TrustedUri(uri, metadata)) throw new UpdateException("GitHub redirected the update to an untrusted address. The request was stopped.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MillBeam-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(metadata ? "application/vnd.github+json" : "application/octet-stream"));
            if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // Also reject unexpected final addresses if an injected test/client handler follows redirects.
            if (response.RequestMessage?.RequestUri is { } final && !TrustedUri(final, metadata))
            {
                response.Dispose();
                throw new UpdateException("The update response came from an untrusted address.");
            }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new UpdateException("GitHub returned an incomplete download redirect.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new UpdateException("GitHub returned too many download redirects. Please try again later.");
    }

    private static bool TrustedUri(Uri uri, bool metadata)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        if (metadata) return uri.Host == "api.github.com" && uri.AbsolutePath == ApiBase.AbsolutePath;
        return (uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)) ||
            uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken _)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        if ((int)response.StatusCode is 403 or 429)
            throw new UpdateException("GitHub is limiting update requests. Please wait a few minutes and try again.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException("The MillBeam release could not be found on GitHub. It may not have finished publishing yet.");
        throw new UpdateException($"GitHub returned HTTP {(int)response.StatusCode}. Please try again later.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) throw new UpdateException("GitHub's release information exceeded the download limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximum) throw new UpdateException("GitHub's release information exceeded the download limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool TryBoolean(JsonElement parent, string property, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(property, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }
    private static string? ReadString(JsonElement parent, string property) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
