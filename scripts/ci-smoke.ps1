#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = (Resolve-Path -LiteralPath $Executable).Path
$results = [IO.Path]::GetFullPath($ResultsDirectory)
foreach ($scenario in @(
    @{ Name = 'ui'; Option = '--smoke-test'; Prefix = 'ui-smoke' },
    @{ Name = 'shutdown'; Option = '--shutdown-test'; Prefix = 'shutdown-smoke' },
    @{ Name = 'update-ui'; Option = '--update-ui-test'; Prefix = 'update-ui-smoke' }
)) {
    $folder = Join-Path $results $scenario.Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $process = Start-Process -FilePath $exe -ArgumentList @($scenario.Option, ('"' + $folder + '"')) `
        -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $folder 'stdout.txt') -RedirectStandardError (Join-Path $folder 'stderr.txt')
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Only this simulator test process is terminated on timeout.
            $process.Kill($true)
            $process.WaitForExit()
            throw "The $($scenario.Name) simulator smoke test exceeded $TimeoutSeconds seconds."
        }
        $process.Refresh()
        $failure = Join-Path $folder ($scenario.Prefix + '-error.txt')
        if (Test-Path -LiteralPath $failure) {
            Get-Content -LiteralPath $failure | Write-Host
            throw "The $($scenario.Name) simulator smoke test wrote an error report."
        }
        if ($process.ExitCode -ne 0) { throw "The $($scenario.Name) smoke test exited with code $($process.ExitCode)." }
        $report = Join-Path $folder ($scenario.Prefix + '-results.txt')
        $checks = @(Get-Content -LiteralPath $report)
        if ($checks.Count -eq 0 -or @($checks | Where-Object { $_ -notmatch '^PASS ' }).Count -ne 0) {
            throw "The $($scenario.Name) smoke test did not produce a complete passing report."
        }
        Write-Host "$($scenario.Name): $($checks.Count) passing simulator checks."
    } finally { $process.Dispose() }
}
