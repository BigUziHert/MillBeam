using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GenmitsuControl.Updates;

if (args.FirstOrDefault() == "--verify-github-release")
{
    using var http = GitHubUpdateClient.CreateHttpClient();
    var updater = new GitHubUpdateClient(http);
    var release = await updater.CheckAsync(args.ElementAtOrDefault(1) ?? "1.4.4", includeDevelopment: true);
    if (release is null) { Console.WriteLine("No newer eligible GitHub release was found."); return 1; }
    var verified = await updater.DownloadAsync(release);
    Console.WriteLine($"Verified GitHub release {release.Tag}: {release.SizeBytes} bytes, SHA-256 {release.Sha256}");
    Console.WriteLine($"Staged (not installed): {verified.FilePath}");
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task ThrowsAsync(Func<Task> action, string contains)
{
    try { await action(); }
    catch (UpdateException ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception($"Expected an UpdateException mentioning '{contains}'.");
}
ReleaseVersion Version(string text)
{
    if (!ReleaseVersion.TryParse(text, out var version)) throw new Exception($"Invalid fixture version: {text}");
    return version;
}
byte[] payload = Encoding.UTF8.GetBytes("Verified executable fixture; no real application is run by network tests.");
string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
object Release(string tag, bool prerelease = false, bool draft = false, string? digest = null,
    long? size = null, string? assetUrl = null, string? name = null) => new
{
    tag_name = tag, prerelease, draft,
    html_url = $"https://github.com/BigUziHert/MillBeam/releases/tag/{tag}",
    assets = new[] { new { name = name ?? "MillBeam.exe", state = "uploaded", size = size ?? payload.Length,
        digest = digest ?? $"sha256:{hash}", browser_download_url = assetUrl ?? $"https://github.com/BigUziHert/MillBeam/releases/download/{tag}/MillBeam.exe" } }
};
HttpResponseMessage Json(params object[] releases) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) };
GitHubUpdateClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new HttpClient(new Handler(send)));
GitHubUpdateClient StaticClient(params object[] releases) => Client((_, _) => Task.FromResult(Json(releases)));
UpdateRelease DownloadRelease(string? digest = null, long? size = null, string? url = null) => new("1.5.0", "v1.5.0",
    "https://github.com/BigUziHert/MillBeam/releases/tag/v1.5.0",
    new Uri(url ?? "https://github.com/BigUziHert/MillBeam/releases/download/v1.5.0/MillBeam.exe"), size ?? payload.Length, digest ?? hash);

Test("SemVer compares numeric dev run/attempt identifiers and stable versions", () =>
{
    Check(Version("1.5.0-dev.10.1").CompareTo(Version("1.5.0-dev.9.10")) > 0, "Run numbers sorted lexically.");
    Check(Version("1.5.0-dev.10.10").CompareTo(Version("1.5.0-dev.10.2")) > 0, "Attempt numbers sorted lexically.");
    Check(Version("1.5.0").CompareTo(Version("1.5.0-dev.100.1")) > 0, "Stable must supersede development for same base.");
    Check(Version("1.5.1-dev.1.1").CompareTo(Version("1.5.0")) > 0, "Next base development version must compare newer.");
    Check(Version("v1.5.0+build.7").CompareTo(Version("1.5.0")) == 0, "Build metadata changes precedence.");
    Check(!ReleaseVersion.TryParse("1.5.0-dev.01.1", out _), "Leading zero prerelease accepted.");
    Check(!ReleaseVersion.TryParse("1.5", out _), "Truncated version accepted.");
    Check(!ReleaseVersion.TryParse("1.05.0", out _), "Leading zero core accepted.");
    return Task.CompletedTask;
});
Test("Newest eligible version wins regardless of release ordering", async () =>
{
    var result = await StaticClient(Release("v1.5.0-dev.9.1", true), Release("v1.5.0-dev.10.2", true), Release("v1.5.0-dev.10.1", true)).CheckAsync("1.5.0-dev.0.0", true);
    Check(result?.Version == "1.5.0-dev.10.2", "Wrong latest version.");
});
Test("Stable channel excludes drafts and development releases", async () =>
{
    var result = await StaticClient(Release("v2.0.0", draft: true), Release("v1.6.0-dev.1.1", true), Release("v1.5.0")).CheckAsync("1.4.4", false);
    Check(result?.Version == "1.5.0", "Stable channel included unpublished/development.");
});
Test("Development channel accepts stable upgrade but rejects unrelated prereleases", async () =>
{
    var result = await StaticClient(Release("v1.6.0-beta.1", true), Release("v1.5.0"), Release("v1.5.0-dev.999.1", true)).CheckAsync("1.5.0-dev.2.1", true);
    Check(result?.Version == "1.5.0", "Stable or unrelated prerelease selection failed.");
});
Test("No update for equal, older, malformed or flag-mismatched release", async () =>
{
    var result = await StaticClient(Release("v1.5.0"), Release("v1.4.4"), Release("garbage"), Release("v1.6.0", true), Release("v1.6.0-dev.1.1")).CheckAsync("1.5.0", true);
    Check(result is null, "Should not downgrade or use malformed metadata.");
});
Test("Check uses pagination, GitHub headers and no credential", async () =>
{
    var requests = 0;
    var client = Client((request, _) =>
    {
        requests++;
        Check(request.Headers.UserAgent.ToString().Contains("MillBeam"), "Missing user agent.");
        Check(request.Headers.Contains("X-GitHub-Api-Version"), "Missing API version.");
        Check(request.Headers.Authorization is null, "Unexpected credential.");
        return Task.FromResult(requests == 1 ? Json(Enumerable.Repeat(Release("v1.4.4"), 100).ToArray()) : Json(Release("v1.5.0")));
    });
    Check((await client.CheckAsync("1.4.4", false))?.Version == "1.5.0" && requests == 2, "Pagination failed.");
});
Test("Reject release without GitHub SHA-256 digest", () => ThrowsAsync(() => StaticClient(Release("v1.5.0", digest: "")).CheckAsync("1.4.4", false), "SHA-256"));
Test("Reject wrong asset, oversized asset and repository substitution", async () =>
{
    await ThrowsAsync(() => StaticClient(Release("v1.5.0", name: "other.exe")).CheckAsync("1.4.4", false), "MillBeam.exe");
    await ThrowsAsync(() => StaticClient(Release("v1.5.0", size: GitHubUpdateClient.MaximumDownloadBytes + 1)).CheckAsync("1.4.4", false), "metadata");
    await ThrowsAsync(() => StaticClient(Release("v1.5.0", assetUrl: "https://github.com/attacker/MillBeam/releases/download/v1.5.0/MillBeam.exe")).CheckAsync("1.4.4", false), "metadata");
});
Test("Friendly rate limit, network, missing release, malformed JSON errors", async () =>
{
    await ThrowsAsync(() => Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))).CheckAsync("1.4.4", false), "limiting");
    await ThrowsAsync(() => Client((_, _) => throw new HttpRequestException("offline")).CheckAsync("1.4.4", false), "reach GitHub");
    await ThrowsAsync(() => Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))).CheckAsync("1.4.4", false), "not be found");
    await ThrowsAsync(() => Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid") })).CheckAsync("1.4.4", false), "invalid release");
});
Test("Download validates bytes and stages outside installed application", async () =>
{
    var prepared = await Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })).DownloadAsync(DownloadRelease());
    Check(File.Exists(prepared.FilePath), "Staged file missing.");
    Check((await File.ReadAllBytesAsync(prepared.FilePath)).SequenceEqual(payload), "Staged bytes changed.");
    Check(Path.GetDirectoryName(prepared.FilePath) == prepared.StagingDirectory, "Staging location mismatch.");
    File.Delete(prepared.FilePath);
});
Test("Download rejects digest mismatch, truncated and oversized bodies", async () =>
{
    GitHubUpdateClient Bytes(byte[] bytes) => Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(bytes)) }));
    await ThrowsAsync(() => Bytes(payload).DownloadAsync(DownloadRelease(new string('0', 64))), "integrity");
    await ThrowsAsync(() => Bytes(payload[..^1]).DownloadAsync(DownloadRelease()), "size");
    await ThrowsAsync(() => Bytes(payload.Concat(new byte[1]).ToArray()).DownloadAsync(DownloadRelease()), "size");
});
Test("Reject insecure URL before any request", async () =>
{
    var requests = 0;
    var client = Client((_, _) => { requests++; return Task.FromResult(Json()); });
    await ThrowsAsync(() => client.DownloadAsync(DownloadRelease(url: "http://github.com/BigUziHert/MillBeam/releases/download/v1.5.0/MillBeam.exe")), "metadata");
    Check(requests == 0, "Insecure URL was contacted.");
});
Test("Redirect destination is checked before contacting untrusted host", async () =>
{
    var requests = 0;
    var client = Client((_, _) =>
    {
        requests++;
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://github.com.evil.invalid/MillBeam.exe");
        return Task.FromResult(response);
    });
    await ThrowsAsync(() => client.DownloadAsync(DownloadRelease()), "untrusted");
    Check(requests == 1, "Untrusted host was contacted.");
});
Test("Official release CDN redirect downloads with hash verification", async () =>
{
    var requests = 0;
    var client = Client((request, _) =>
    {
        if (++requests == 1)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/fixture?sig=test");
            return Task.FromResult(response);
        }
        Check(request.RequestUri!.Host == "release-assets.githubusercontent.com", "Wrong CDN.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    });
    var prepared = await client.DownloadAsync(DownloadRelease());
    Check(requests == 2, "Did not follow CDN redirect.");
    File.Delete(prepared.FilePath);
});
Test("User cancellation remains cancellation, not a failed update", async () =>
{
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try { await Client((_, token) => Task.FromCanceled<HttpResponseMessage>(token)).CheckAsync("1.4.4", false, cancel.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Cancellation was swallowed.");
});

if (OperatingSystem.IsWindows())
{
    // All executable changes below stay in a unique temp fixture directory; these are cmd.exe copies,
    // and the only process stopped by a test is the sleeping child created by that same test.
    async Task<(PreparedUpdate Update, string Target, Process Parent)> Fixture(bool invalidUpdate = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "MillBeam-update-tests", Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        var target = Path.Combine(root, "MillBeam.exe");
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(cmd, target);
        var bytes = invalidUpdate ? Encoding.UTF8.GetBytes("Not a Windows executable") : (await File.ReadAllBytesAsync(cmd)).Concat(new byte[] { 0x4d, 0x42 }).ToArray();
        var download = Path.Combine(stage, "MillBeam.download");
        await File.WriteAllBytesAsync(download, bytes);
        var release = DownloadRelease(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(arg);
        return (new PreparedUpdate(release, download, stage), target, Process.Start(start)!);
    }
    async Task<string> WaitResult(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var file = Path.Combine(directory, "result.json");
        while (!File.Exists(file)) await Task.Delay(100, timeout.Token);
        // Helper writes the tiny report synchronously; tolerate the creation/write boundary.
        for (var i = 0; ; i++)
        {
            try { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, timeout.Token)); return doc.RootElement.GetProperty("Status").GetString()!; }
            catch (JsonException) when (i < 10) { await Task.Delay(50, timeout.Token); }
        }
    }
    void End(Process process) { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); process.Dispose(); }
    Test("Installer waits for exact parent, atomically replaces and preserves previous executable", async () =>
    {
        var fixture = await Fixture();
        try
        {
            var original = SHA256.HashData(await File.ReadAllBytesAsync(fixture.Target));
            using var token = new CancellationTokenSource();
            var handoff = await UpdateInstaller.PrepareForProcessAsync(fixture.Update, fixture.Target, fixture.Parent, TimeSpan.FromSeconds(20), token.Token);
            token.Cancel(); // Dialog cancellation after readiness must not cancel the detached helper.
            Check(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(original), "Replaced application before it exited.");
            End(fixture.Parent);
            Check(await WaitResult(handoff.StagingDirectory) == "Installed", "Helper did not install.");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Target + ".previous")).SequenceEqual(original), "Backup does not contain previous executable.");
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Target))) == fixture.Update.Release.Sha256, "Wrong installed bytes.");
        }
        finally { if (!fixture.Parent.SafeHasExited()) End(fixture.Parent); }
    });
    Test("Installer cancellation leaves current executable unchanged", async () =>
    {
        var fixture = await Fixture();
        try
        {
            var original = await File.ReadAllBytesAsync(fixture.Target);
            var handoff = await UpdateInstaller.PrepareForProcessAsync(fixture.Update, fixture.Target, fixture.Parent, TimeSpan.FromSeconds(20));
            handoff.Cancel();
            Check(await WaitResult(handoff.StagingDirectory) == "NotInstalled", "Cancellation did not abort.");
            Check((await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(original), "Cancellation changed target.");
        }
        finally { End(fixture.Parent); }
    });
    Test("Installer parent exit timeout leaves current executable unchanged", async () =>
    {
        var fixture = await Fixture();
        try
        {
            var original = await File.ReadAllBytesAsync(fixture.Target);
            var handoff = await UpdateInstaller.PrepareForProcessAsync(fixture.Update, fixture.Target, fixture.Parent, TimeSpan.FromSeconds(1));
            Check(await WaitResult(handoff.StagingDirectory) == "NotInstalled", "Parent timeout did not abort.");
            Check((await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(original), "Timed out update changed target.");
        }
        finally { End(fixture.Parent); }
    });
    Test("Installer rejects changed staged bytes before handoff", async () =>
    {
        var fixture = await Fixture();
        try
        {
            await File.AppendAllTextAsync(fixture.Update.FilePath, "corrupt");
            await ThrowsAsync(() => UpdateInstaller.PrepareForProcessAsync(fixture.Update, fixture.Target, fixture.Parent, TimeSpan.FromSeconds(10)), "integrity");
            Check(!File.Exists(fixture.Target + ".previous"), "Corrupt update replaced current application.");
        }
        finally { End(fixture.Parent); }
    });
    Test("Installer rolls back exact old bytes when updated executable cannot launch", async () =>
    {
        var fixture = await Fixture(invalidUpdate: true);
        try
        {
            var original = await File.ReadAllBytesAsync(fixture.Target);
            var handoff = await UpdateInstaller.PrepareForProcessAsync(fixture.Update, fixture.Target, fixture.Parent, TimeSpan.FromSeconds(20));
            End(fixture.Parent);
            Check(await WaitResult(handoff.StagingDirectory) == "RolledBack", "Failed launch did not restore previous executable.");
            Check((await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(original), "Rollback did not restore exact previous bytes.");
        }
        finally { if (!fixture.Parent.SafeHasExited()) End(fixture.Parent); }
    });
}

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} updater checks passed.");
return failed == 0 ? 0 : 1;

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}
static class ProcessExtensions
{
    public static bool SafeHasExited(this Process process) { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
}
