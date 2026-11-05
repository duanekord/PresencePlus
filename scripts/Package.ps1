#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),
    [string] $NodeDirectory
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
    & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $stage -NodeDirectory $NodeDirectory -Version $Version
    foreach ($required in @('PresencePlus.exe', 'System.Private.CoreLib.dll', 'System.Windows.Forms.dll',
        'Matter\node.exe', 'Matter\dist\main.js', 'Matter\dist\control.js', 'Matter\NODE-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) {
            throw "Incomplete portable package: missing $required"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'PresencePlus.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) {
        throw 'The portable package must include the .NET runtime.'
    }
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
    $notices.Add('Dependency license and notice files are preserved alongside packages under Matter\node_modules.')
    $notices.Add('No open-source license has yet been selected for PresencePlus itself.')
    $notices.Add('')
    $modules = Join-Path $stage 'Matter\node_modules'
    foreach ($manifest in (Get-ChildItem -LiteralPath $modules -Filter package.json -File -Recurse | Sort-Object FullName)) {
        $metadata = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json
        if (-not $metadata.name -or -not $metadata.version) { continue }
        $license = if ($metadata.license -is [string]) { $metadata.license } else { $metadata.license.type }
        if (-not $license) { $license = 'See package license files' }
        $relative = [IO.Path]::GetRelativePath($stage, $manifest.DirectoryName)
        $notices.Add("$($metadata.name) $($metadata.version) | $license | $relative")
    }
    $notices | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Encoding utf8

    if (Test-Path -LiteralPath (Join-Path $stage 'settings.json')) { throw 'User preferences must not be packaged.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporary -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
    Write-Output "Portable download: $archive"
    Write-Output "SHA256: $hash"
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    [IO.Directory]::Delete($stage, $true)
}
