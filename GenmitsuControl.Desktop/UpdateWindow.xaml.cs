using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using GenmitsuControl.Updates;

namespace GenmitsuControl.Desktop;

public partial class UpdateWindow : Window
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GitHubUpdateClient _client;
    private readonly string _version;
    private readonly Func<PreparedUpdate, string, CancellationToken, Task<UpdateHandoff>> _prepareUpdate;
    private UpdateRelease? _release;
    private bool _working;
    private bool _initialized;
    public UpdateHandoff? Handoff { get; private set; }

    public UpdateWindow() : this(GitHubUpdateClient.CreateHttpClient(),
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.5.0-dev.0.0").Split('+')[0],
        UpdateInstaller.PrepareAsync) { }

    // Tests use an in-memory HTTP handler and a preparation delegate that cannot
    // start an installer, so exercising the real dialog never reaches a network
    // or replaces a file.
    internal UpdateWindow(HttpClient http, string version,
        Func<PreparedUpdate, string, CancellationToken, Task<UpdateHandoff>> prepareUpdate)
    {
        _http = http;
        _version = version;
        _prepareUpdate = prepareUpdate;
        InitializeComponent();
        _client = new GitHubUpdateClient(_http);
        InstalledVersion.Text = "Installed: " + _version;
        DevelopmentCheck.IsChecked = _version.Contains("-dev.", StringComparison.OrdinalIgnoreCase);
        _initialized = true;
        Loaded += async (_, _) => await CheckAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _http.Dispose(); };
    }

    private void SetWorking(bool working)
    {
        _working = working;
        UpdateAction.IsEnabled = DevelopmentCheck.IsEnabled = !working;
        UpdateProgress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckAsync()
    {
        if (_working || _lifetime.IsCancellationRequested) return;
        SetWorking(true);
        _release = null;
        UpdateStatus.Text = "Checking GitHub…";
        UpdateDetail.Text = "";
        try
        {
            _release = await _client.CheckAsync(_version, DevelopmentCheck.IsChecked == true, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            UpdateStatus.Text = _release is null ? "No newer release is available." : "MillBeam " + _release.Version + " is available.";
            UpdateDetail.Text = _release is null ? "You're up to date for the selected release channel." : "Download, verify, and restart to install this release.";
            UpdateAction.Content = _release is null ? "Check again" : "Download and install";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowFailure("Could not check for updates.", ex); }
        finally { if (!_lifetime.IsCancellationRequested) SetWorking(false); }
    }

    private void ShowFailure(string heading, Exception ex)
    {
        _release = null;
        UpdateStatus.Text = heading;
        UpdateDetail.Text = ex.Message;
        UpdateAction.Content = "Check again";
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        if (_release is null) { await CheckAsync(); return; }
        SetWorking(true);
        UpdateStatus.Text = "Downloading MillBeam " + _release.Version + "…";
        UpdateDetail.Text = "The download will be checked before anything is installed.";
        try
        {
            using var prepared = await _client.DownloadAsync(_release, _lifetime.Token);
            UpdateStatus.Text = "Preparing to restart…";
            var target = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find this MillBeam executable. Download the portable app from GitHub releases.");
            Handoff = await _prepareUpdate(prepared, target, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) { Handoff.Cancel(); Handoff = null; return; }
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { Handoff?.Cancel(); }
        catch (Exception ex) { Handoff?.Cancel(); Handoff = null; ShowFailure("The update was not installed.", ex); }
        finally { if (!_lifetime.IsCancellationRequested) SetWorking(false); }
    }

    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _working) return;
        _release = null;
        UpdateStatus.Text = "Ready to check the selected channel.";
        UpdateDetail.Text = "";
        UpdateAction.Content = "Check again";
    }

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/BigUziHert/MillBeam/releases") { UseShellExecute = true }); }
        catch (Exception ex) { UpdateDetail.Text = "Could not open GitHub: " + ex.Message; }
    }
}
