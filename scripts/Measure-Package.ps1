#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [string] $Archive,
    [Parameter(Mandatory)]
    [string] $BaselineArchive,
    [ValidateRange(1, 10)]
    [int] $Iterations = 3,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\benchmarks')
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$samples = [Collections.Generic.List[object]]::new()
$metadata = @{}
$archives = @{ Baseline = (Resolve-Path -LiteralPath $BaselineArchive).Path; Compact = (Resolve-Path -LiteralPath $Archive).Path }
foreach ($name in $archives.Keys) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archives[$name])
    try {
        $files = @($zip.Entries | Where-Object Name)
        $metadata[$name] = @{
            files = $files.Count
            archiveBytes = (Get-Item -LiteralPath $archives[$name]).Length
            expandedBytes = ($files | Measure-Object Length -Sum).Sum
        }
    } finally { $zip.Dispose() }
}
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $order = if ($iteration % 2) { @('Baseline', 'Compact') } else { @('Compact', 'Baseline') }
    foreach ($name in $order) {
        $stage = Join-Path $output ('extract-' + [guid]::NewGuid().ToString('N'))
        try {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            [IO.Compression.ZipFile]::ExtractToDirectory($archives[$name], $stage)
            $clock.Stop()
            $files = (Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object).Count
            if ($files -ne $metadata[$name].files) { throw 'Extraction file count mismatch.' }
            $sample = [ordered]@{ archive = $name; iteration = $iteration; extractionSeconds = $clock.Elapsed.TotalSeconds }
            if ($name -eq 'Compact') {
                foreach ($launch in @('first', 'repeat')) {
                    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $stage 'PresencePlus.exe'))
                    $start.ArgumentList.Add('--check-package')
                    $start.UseShellExecute = $false
                    $start.CreateNoWindow = $true
                    $start.RedirectStandardOutput = $true
                    $start.RedirectStandardError = $true
                    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $stage 'runtime-cache'
                    $clock.Restart()
                    $process = [Diagnostics.Process]::Start($start)
                    try {
                        if (-not $process.WaitForExit(30000)) {
                            Stop-Process -Id $process.Id
                            throw 'Windows runtime startup check timed out.'
                        }
                        $clock.Stop()
                        if ($process.ExitCode -ne 0 -or $process.StandardOutput.ReadToEnd().Trim() -ne 'PresencePlus Windows runtime loaded.') {
                            throw "Windows runtime startup check failed: $($process.StandardError.ReadToEnd())"
                        }
                        $sample[$launch + 'RuntimeStartupSeconds'] = $clock.Elapsed.TotalSeconds
                    } finally { $process.Dispose() }
                }
                $sample.runtimeExtractedFiles = if (Test-Path -LiteralPath (Join-Path $stage 'runtime-cache')) {
                    (Get-ChildItem -LiteralPath (Join-Path $stage 'runtime-cache') -Recurse -File | Measure-Object).Count
                } else { 0 }
            }
            $samples.Add([pscustomobject]$sample)
            Write-Output ([pscustomobject]$sample | ConvertTo-Json -Compress)
        } finally {
            if (Test-Path -LiteralPath $stage) { [IO.Directory]::Delete($stage, $true) }
        }
    }
}
$result = @{
    method = '.NET ZipFile.ExtractToDirectory; alternating order; fresh destination each run; security settings unchanged. Not an Explorer timing or a controlled cold-cache test.'
    metadata = $metadata
    samples = $samples.ToArray()
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'package-measurements.json')
