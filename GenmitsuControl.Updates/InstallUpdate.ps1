param([Parameter(Mandatory = $true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$stagingDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PlanPath))
$cancelPath = Join-Path $stagingDirectory 'cancel'
$readyPath = Join-Path $stagingDirectory 'ready'
$resultPath = Join-Path $stagingDirectory 'result.json'
$replacementPath = $null
$replaced = $false
$newProcess = $null
$plan = $null
$parent = $null
$parentExited = $false

function Write-Result([string]$status, [string]$message) {
    $json = @{ Status = $status; Message = $message; TimestampUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($resultPath, $json)
}
function Check-Cancel {
    if ([IO.File]::Exists($cancelPath)) { throw 'Update cancelled. The installed application was left unchanged.' }
}
function Verify-Bytes([string]$path) {
    $info = Get-Item -LiteralPath $path -Force
    if ($info.Length -ne $plan.SizeBytes) { throw 'The staged update changed size. The update was not installed.' }
    $stream = [IO.File]::OpenRead($path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if (-not [string]::Equals($actualHash, $plan.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The staged update failed its SHA-256 integrity check. The update was not installed.'
    }
}
function Get-ExactParent {
    $parent = Get-Process -Id $plan.ParentId -ErrorAction SilentlyContinue
    if ($null -eq $parent) { return $null }
    try {
        if ($parent.StartTime.ToUniversalTime().Ticks -ne $plan.ParentStartTicks -or
            -not [string]::Equals($parent.Path, $plan.ParentPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The application process identity changed. The update was not installed.'
        }
        return $parent
    }
    catch { $parent.Dispose(); throw }
}
function Verify-OrdinaryFile([string]$path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked files are not supported by the updater.' }
    $directory = $item.Directory
    while ($null -ne $directory) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked folders are not supported by the updater.' }
        $directory = $directory.Parent
    }
}

try {
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    if ($plan.SizeBytes -le 0 -or $plan.SizeBytes -gt 268435456 -or $plan.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $plan.ParentExitTimeoutMilliseconds -le 0 -or $plan.ParentExitTimeoutMilliseconds -gt 60000 -or
        $plan.Nonce -notmatch '^[a-f0-9]{32}$' -or $plan.LaunchWindowStyle -notin @('Normal', 'Hidden')) { throw 'The update plan is invalid.' }
    $plan.TargetPath = [IO.Path]::GetFullPath($plan.TargetPath)
    $plan.DownloadPath = [IO.Path]::GetFullPath($plan.DownloadPath)
    if ([IO.Path]::GetDirectoryName($plan.DownloadPath) -ne $stagingDirectory -or
        [IO.Path]::GetExtension($plan.TargetPath) -ne '.exe' -or $plan.TargetPath -eq $plan.DownloadPath) { throw 'The update file locations are invalid.' }
    Verify-OrdinaryFile $plan.TargetPath
    Verify-OrdinaryFile $plan.DownloadPath
    Verify-Bytes $plan.DownloadPath
    Check-Cancel
    $parent = Get-ExactParent
    if ($null -eq $parent) { throw 'The application exited before the installer was ready. No files were changed.' }
    # Retain a handle to this exact process: a reused PID can never satisfy the exit wait.
    $null = $parent.Handle

    # The replacement file must be on the same volume as the target for atomic File.Replace.
    $targetDirectory = [IO.Path]::GetDirectoryName($plan.TargetPath)
    $replacementPath = Join-Path $targetDirectory ('.millbeam-update-' + $plan.Nonce + '.exe')
    [IO.File]::Copy($plan.DownloadPath, $replacementPath, $false)
    Verify-Bytes $replacementPath
    $backupPath = $plan.TargetPath + '.previous'
    if ([IO.File]::Exists($backupPath)) { Verify-OrdinaryFile $backupPath }
    [IO.File]::WriteAllText($readyPath, $plan.Nonce)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($plan.ParentExitTimeoutMilliseconds)
    while ($true) {
        Check-Cancel
        if ($parent.WaitForExit(100)) { $parentExited = $true; break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'MillBeam did not close in time. The update was cancelled and the installed application was left unchanged.' }
    }
    Check-Cancel
    Verify-OrdinaryFile $plan.TargetPath
    Verify-Bytes $replacementPath
    # No replacement is attempted until the exact application process has exited.
    $replaceDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($true) {
        try { [IO.File]::Replace($replacementPath, $plan.TargetPath, $backupPath, $true); $replaced = $true; break }
        catch [IO.IOException] {
            Check-Cancel
            if ([DateTime]::UtcNow -ge $replaceDeadline) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    Verify-Bytes $plan.TargetPath
    $newProcess = Start-Process -FilePath $plan.TargetPath -WorkingDirectory $targetDirectory -WindowStyle $plan.LaunchWindowStyle -PassThru
    # A process creation failure or immediate nonzero exit restores the previous executable.
    if ($newProcess.WaitForExit(1500) -and $newProcess.ExitCode -ne 0) { throw 'The updated application exited with an error during startup.' }
    Write-Result 'Installed' 'The update was installed and MillBeam was restarted. The previous executable is saved beside it with a .previous suffix.'
    try { [IO.File]::Delete($plan.DownloadPath) } catch { }
}
catch {
    $failure = $_.Exception.Message
    if ($replaced) {
        try {
            if ($null -ne $newProcess -and -not $newProcess.HasExited) { $newProcess.Kill(); $newProcess.WaitForExit(5000) | Out-Null }
            $failedPath = Join-Path $targetDirectory ('.millbeam-failed-' + $plan.Nonce + '.exe')
            # A rollback uses another atomic replacement and preserves the failed bytes for diagnosis.
            $rollbackDeadline = [DateTime]::UtcNow.AddSeconds(15)
            while ($true) {
                try { [IO.File]::Replace($backupPath, $plan.TargetPath, $failedPath, $true); break }
                catch [IO.IOException] {
                    if ([DateTime]::UtcNow -ge $rollbackDeadline) { throw }
                    Start-Sleep -Milliseconds 250
                }
            }
            Start-Process -FilePath $plan.TargetPath -WorkingDirectory ([IO.Path]::GetDirectoryName($plan.TargetPath)) -WindowStyle $plan.LaunchWindowStyle | Out-Null
            Write-Result 'RolledBack' ('The previous application was restored because the update could not start. ' + $failure)
        }
        catch { Write-Result 'RecoveryRequired' ('The update failed. The previous executable is at ' + $backupPath + '. ' + $failure + ' Recovery error: ' + $_.Exception.Message) }
    }
    else {
        if ($parentExited) {
            try { Start-Process -FilePath $plan.TargetPath -WorkingDirectory ([IO.Path]::GetDirectoryName($plan.TargetPath)) -WindowStyle $plan.LaunchWindowStyle | Out-Null }
            catch { $failure += ' The original application could not be reopened: ' + $_.Exception.Message }
        }
        Write-Result 'NotInstalled' $failure
    }
    exit 1
}
finally {
    if ($null -ne $newProcess) { $newProcess.Dispose() }
    if ($null -ne $parent) { $parent.Dispose() }
    if ($null -ne $replacementPath -and [IO.File]::Exists($replacementPath)) {
        try { [IO.File]::Delete($replacementPath) } catch { }
    }
}
