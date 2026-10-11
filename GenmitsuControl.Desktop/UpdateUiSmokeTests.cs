using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenmitsuControl.Updates;

namespace GenmitsuControl.Desktop;

public partial class UpdateWindow
{
    /// <summary>Exercises real WPF update states using only in-memory HTTP responses.</summary>
    internal static async Task RunUpdateUiTestsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        var payload = Encoding.UTF8.GetBytes("MillBeam updater UI test, never installed.");
        string releaseJson = JsonSerializer.Serialize(new[] { new
        {
            draft = false, prerelease = false, tag_name = "v1.5.1",
            html_url = "https://github.com/BigUziHert/MillBeam/releases/tag/v1.5.1",
            assets = new[] { new
            {
                name = "MillBeam.exe", state = "uploaded", size = payload.Length,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                browser_download_url = "https://github.com/BigUziHert/MillBeam/releases/download/v1.5.1/MillBeam.exe"
            } }
        } });

        foreach (string scenario in new[] { "Up to date", "Newer release", "Check failed", "Close during check", "Close during download", "Preparation failed", "Close during preparation" })
        {
            void Assert(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException($"Update UI check failed: {scenario}: {description}");
                checks.Add($"PASS {scenario}: {description}");
                File.WriteAllLines(Path.Combine(folder, "update-ui-smoke-results.txt"), checks);
            }

            var pendingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requestCount = 0, prepareCount = 0;
            PreparedUpdate? staged = null;
            async Task WaitForCancellation(CancellationToken token)
            {
                pendingStarted.TrySetResult(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    pendingCancelled.TrySetResult(true);
                    throw;
                }
            }
            var handler = new UpdateUiHttpHandler(async (request, token) =>
            {
                requestCount++;
                bool metadata = request.RequestUri!.AbsolutePath == "/repos/BigUziHert/MillBeam/releases";
                if ((scenario == "Close during check" && metadata) || (scenario == "Close during download" && !metadata))
                    await WaitForCancellation(token);
                if (metadata)
                {
                    Assert(request.RequestUri.Host == "api.github.com", "check requests target the configured repository API");
                    if (scenario == "Check failed") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scenario == "Up to date" ? "[]" : releaseJson) };
                }
                Assert(request.RequestUri.AbsoluteUri == "https://github.com/BigUziHert/MillBeam/releases/download/v1.5.1/MillBeam.exe",
                    "download requests use the release asset selected by the checker");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            });
            async Task<UpdateHandoff> FakePrepare(PreparedUpdate prepared, string target, CancellationToken token)
            {
                prepareCount++;
                staged = prepared;
                Assert(File.ReadAllBytes(prepared.FilePath).SequenceEqual(payload), "preparation receives the verified fake download");
                if (scenario == "Close during preparation") await WaitForCancellation(token);
                throw new UpdateException("Simulated installation preparation failure; no installer was started.");
            }
            var window = new UpdateWindow(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, "1.5.0-dev.1.1", FakePrepare)
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            Application.Current.MainWindow = window;
            int closedCount = 0;
            window.Closed += (_, _) => closedCount++;
            try
            {
                window.Show();
                if (scenario == "Close during check")
                {
                    await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(!window.UpdateAction.IsEnabled && window._working, "check disables repeated actions while pending");
                }
                else
                {
                    await WaitUntilAsync(() => !window._working && requestCount > 0);
                    Assert(window.UpdateAction.IsEnabled && window.DevelopmentCheck.IsEnabled, "finished check re-enables the action and channel controls");
                    if (scenario == "Up to date")
                    {
                        Assert(window._release is null && window.UpdateStatus.Text.Contains("No newer release"), "an empty release list reports up to date");
                        Assert((string)window.UpdateAction.Content == "Check again", "up-to-date action offers another check");
                    }
                    else if (scenario == "Check failed")
                    {
                        Assert(window._release is null && window.UpdateStatus.Text == "Could not check for updates.", "HTTP failure is shown inside the update dialog");
                        Assert(window.UpdateDetail.Text.Contains("limiting update requests"), "the GitHub failure explanation remains visible");
                        Assert((string)window.UpdateAction.Content == "Check again", "failed check remains retryable");
                    }
                    else
                    {
                        Assert(window._release?.Version == "1.5.1" && (string)window.UpdateAction.Content == "Download and install", "a newer release offers download and installation");
                        if (scenario == "Newer release")
                        {
                            window.DevelopmentCheck.IsChecked = false;
                            Assert(window._release is null && (string)window.UpdateAction.Content == "Check again", "changing channels discards the previously selected release");
                        }
                        else
                        {
                            window.UpdateAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (scenario == "Preparation failed")
                            {
                                await WaitUntilAsync(() => !window._working);
                                Assert(prepareCount == 1 && window.UpdateStatus.Text == "The update was not installed.", "preparation failure keeps the application open with a clear error");
                                Assert(window._release is null && window.UpdateAction.IsEnabled && (string)window.UpdateAction.Content == "Check again", "preparation failure permits a fresh check");
                            }
                            else
                            {
                                await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                Assert(!window.UpdateAction.IsEnabled && !window.DevelopmentCheck.IsEnabled, "pending download or preparation disables duplicate actions");
                            }
                        }
                    }
                }
                Assert(window.Handoff is null, "no fake response starts or hands off to an installer");
                window.Close();
                if (scenario.StartsWith("Close during", StringComparison.Ordinal))
                {
                    await pendingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(window._lifetime.IsCancellationRequested, "closing cancels the pending operation");
                    Assert(prepareCount == (scenario == "Close during preparation" ? 1 : 0), "cancellation does not start a later installation stage");
                }
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(closedCount == 1 && window.Handoff is null, "dialog closes once without an installation handoff");
            }
            finally
            {
                if (closedCount == 0) window.Close();
                if (staged is not null)
                {
                    File.Delete(staged.FilePath);
                    // This directory is the individual fake download's staging
                    // folder; nonrecursive deletion cannot remove other data.
                    Directory.Delete(staged.StagingDirectory);
                }
            }
        }

        static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate()) await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class UpdateUiHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => response(request, cancellationToken);
    }
}
