using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenmitsuControl.Updates;

/// <summary>The helper is ready and will wait for this process to exit. Cancel if closing is vetoed.</summary>
public sealed class UpdateHandoff : IDisposable
{
    private readonly string _cancelPath;
    internal UpdateHandoff(string directory) => (_cancelPath, StagingDirectory) = (Path.Combine(directory, "cancel"), directory);
    public string StagingDirectory { get; }
    public void Cancel()
    {
        try { File.WriteAllText(_cancelPath, "The application did not close for the update."); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    // Disposing an abandoned handoff cancels it; retain the object when closing for installation.
    public void Dispose() => Cancel();
}

public static class UpdateInstaller
{
    public static Task<UpdateHandoff> PrepareAsync(PreparedUpdate update, string targetPath, CancellationToken cancellationToken = default)
    {
        var currentPath = Environment.ProcessPath ?? throw new UpdateException("The running application path could not be determined.");
        if (!Path.GetFileName(currentPath).Equals("MillBeam.exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Automatic installation is available in the packaged MillBeam.exe. Open GitHub releases to download it.");
        if (!Path.GetFullPath(targetPath).Equals(Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Updates can only replace the running MillBeam application.");
        return PrepareCurrentProcessAsync(update, targetPath, cancellationToken);
    }

    private static async Task<UpdateHandoff> PrepareCurrentProcessAsync(PreparedUpdate update, string targetPath, CancellationToken cancellationToken)
    {
        using var current = Process.GetCurrentProcess();
        return await PrepareForProcessAsync(update, targetPath, current, TimeSpan.FromSeconds(60), cancellationToken, launchVisible: true);
    }

    // The internal seam lets tests use only their own disposable child process and temporary files.
    internal static async Task<UpdateHandoff> PrepareForProcessAsync(PreparedUpdate update, string targetPath, Process parent,
        TimeSpan parentExitTimeout, CancellationToken cancellationToken = default, bool launchVisible = false)
    {
        if (!OperatingSystem.IsWindows()) throw new UpdateException("Automatic installation is available on Windows only.");
        targetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetFullPath(update.StagingDirectory);
        var downloadPath = Path.GetFullPath(update.FilePath);
        if (Path.GetDirectoryName(downloadPath) != directory || !File.Exists(downloadPath) ||
            !File.Exists(targetPath) || !targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The downloaded update or installed application could not be found.");
        if (targetPath.Equals(downloadPath, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The update must be staged separately from the installed application.");
        VerifyOrdinaryFile(targetPath);
        VerifyOrdinaryFile(downloadPath);
        await VerifyFileAsync(downloadPath, update.Release, cancellationToken);
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var probe = Path.Combine(targetDirectory, $".millbeam-write-check-{Guid.NewGuid():N}");
        try
        {
            await using var writable = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new UpdateException("MillBeam cannot write to its application folder. Move MillBeam.exe to a writable folder and try again.", ex); }

        var scriptPath = Path.Combine(directory, "InstallUpdate.ps1");
        var planPath = Path.Combine(directory, "install-plan.json");
        var parentPath = parent.MainModule?.FileName ?? throw new UpdateException("The running application could not be identified.");
        var plan = new InstallPlan(downloadPath, targetPath, update.Release.SizeBytes, update.Release.Sha256,
            parent.Id, parent.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(parentPath),
            (int)parentExitTimeout.TotalMilliseconds, Guid.NewGuid().ToString("N"), launchVisible ? "Normal" : "Hidden");
        await using (var script = Assembly.GetExecutingAssembly().GetManifestResourceStream("GenmitsuControl.Updates.InstallUpdate.ps1")
            ?? throw new UpdateException("The update installer is missing from this build."))
        await using (var destination = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await script.CopyToAsync(destination, cancellationToken);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), cancellationToken);

        var handoff = new UpdateHandoff(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-PlanPath", planPath })
            start.ArgumentList.Add(argument);
        try
        {
            using var helper = Process.Start(start) ?? throw new UpdateException("The update installer could not be started.");
            var readyPath = Path.Combine(directory, "ready");
            var resultPath = Path.Combine(directory, "result.json");
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (File.Exists(readyPath) && await File.ReadAllTextAsync(readyPath, timeout.Token) == plan.Nonce)
                    return handoff;
                if (File.Exists(resultPath))
                {
                    var error = await File.ReadAllTextAsync(resultPath, timeout.Token);
                    using var result = JsonDocument.Parse(error);
                    throw new UpdateException(result.RootElement.GetProperty("Message").GetString() ?? "The installer could not prepare the update.");
                }
                if (helper.HasExited) throw new UpdateException("The update installer exited before it was ready. MillBeam has been left unchanged.");
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (Exception ex)
        {
            handoff.Cancel();
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new UpdateException("The update installer did not become ready in time. MillBeam has been left unchanged.", ex);
            if (ex is System.ComponentModel.Win32Exception)
                throw new UpdateException("Windows could not start the update installer. MillBeam has been left unchanged.", ex);
            throw;
        }
    }

    private static void VerifyOrdinaryFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UpdateException("Updates cannot replace a linked application file. Run MillBeam from an ordinary writable folder.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UpdateException("Updates cannot be installed through a linked folder. Run MillBeam from an ordinary writable folder.");
    }

    private static async Task VerifyFileAsync(string path, UpdateRelease release, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != release.SizeBytes || release.Sha256.Length != 64 || !release.Sha256.All(char.IsAsciiHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancellationToken), Convert.FromHexString(release.Sha256)))
            throw new UpdateException("The downloaded update failed its integrity check. The installed application has been left unchanged.");
    }

    private sealed record InstallPlan(string DownloadPath, string TargetPath, long SizeBytes, string Sha256,
        int ParentId, long ParentStartTicks, string ParentPath, int ParentExitTimeoutMilliseconds, string Nonce, string LaunchWindowStyle);
}
