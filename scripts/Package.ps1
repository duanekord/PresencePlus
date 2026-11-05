#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),
    [string] $NodeDirectory,
    [switch] $Unbundled
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ("stage-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$archive = Join-Path $output "PresencePlus-$Version-win-x64.zip"
$temporary = $archive + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $stage -NodeDirectory $NodeDirectory -Version $Version -Unbundled:$Unbundled
    foreach ($required in @('PresencePlus.exe', 'Matter\node.exe', 'Matter\dist\main.js', 'Matter\NODE-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) {
            throw "Incomplete portable package: missing $required"
        }
    }
    $runtimeFile = if ($Unbundled) { 'PresencePlus.runtimeconfig.json' } else { 'runtime-manifest.json' }
    $runtime = Get-Content -LiteralPath (Join-Path $stage $runtimeFile) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) {
        throw 'The portable package must include the .NET runtime.'
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'PresencePlus.exe')).ProductVersion.Split('+')[0]
    if ($productVersion -ne $Version) { throw "App version $productVersion does not match package version $Version." }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\screenshots') -Destination (Join-Path $stage 'docs') -Recurse

    $cache = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate .NET runtime license files.' }
    $cachePath = ($cache -replace '^[^:]+:\s*', '').Trim()
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $id = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
        $package = Join-Path $cachePath "$id\$($framework.version)"
        $licenses = @(Get-ChildItem -LiteralPath $package -File |
            Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES|NOTICE)')
        if (-not ($licenses | Where-Object Name -Match '^LICENSE')) {
            throw "Missing runtime license: $id $($framework.version)"
        }
        $destination = Join-Path $stage "licenses\$id"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $licenses | Copy-Item -Destination $destination
    }

    $notices = [Collections.Generic.List[string]]::new()
    $notices.Add("PresencePlus $Version - third-party components")
    $notices.Add('Runtime licenses are in licenses\. Node licensing is in Matter\NODE-LICENSE.txt.')
    $notices.Add('Bundled dependency license and notice files are in Matter\licenses; unbundled packages retain their original files.')
    $notices.Add('No open-source license has yet been selected for PresencePlus itself.')
    $notices.Add('')
    if (-not $Unbundled) {
        $inventory = Get-Content -LiteralPath (Join-Path $stage 'Matter\DEPENDENCIES.json') -Raw | ConvertFrom-Json
        foreach ($package in $inventory) {
            $notices.Add("$($package.name) $($package.version) | $($package.license) | Matter\$($package.notices)")
        }
    } else {
        foreach ($manifest in (Get-ChildItem -LiteralPath (Join-Path $stage 'Matter\node_modules') -Filter package.json -File -Recurse | Sort-Object FullName)) {
            $metadata = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json
            if (-not $metadata.name -or -not $metadata.version) { continue }
            $license = if ($metadata.license -is [string]) { $metadata.license } else { $metadata.license.type }
            if (-not $license) { $license = 'See package license files' }
            $relative = [IO.Path]::GetRelativePath($stage, $manifest.DirectoryName)
            $notices.Add("$($metadata.name) $($metadata.version) | $license | $relative")
        }
    }
    $notices | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Encoding utf8

    if (Test-Path -LiteralPath (Join-Path $stage 'settings.json')) { throw 'User preferences must not be packaged.' }
    & (Join-Path $stage 'Matter\node.exe') (Join-Path $root 'src\PresencePlus.Matter\check-bundle.mjs') (Join-Path $stage 'Matter')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Matter helper checks failed.' }
    $check = Start-Process -FilePath (Join-Path $stage 'PresencePlus.exe') -ArgumentList '--check-package' `
        -PassThru -RedirectStandardOutput (Join-Path $stage 'package-check.txt') -WindowStyle Hidden
    if (-not $check.WaitForExit(30000)) {
        Stop-Process -Id $check.Id
        throw 'Packaged Windows runtime check timed out.'
    }
    if ($check.ExitCode -ne 0 -or
        (Get-Content -LiteralPath (Join-Path $stage 'package-check.txt') -Raw).Trim() -ne 'PresencePlus Windows runtime loaded.') {
        throw 'Packaged Windows runtime check failed.'
    }
    Remove-Item -LiteralPath (Join-Path $stage 'package-check.txt')
    $count = (Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object).Count
    if (-not $Unbundled -and $count -gt 100) { throw "Compact package exceeds the 100-file limit: $count" }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporary -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
    Write-Output "Portable download: $archive"
    Write-Output "SHA256: $hash"
    Write-Output "Archive files: $count"
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    [IO.Directory]::Delete($stage, $true)
}
